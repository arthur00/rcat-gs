﻿using System;
using System.Threading;
using System.Text;
using Alchemy.Server;
using System.Net;
using Alchemy.Server.Classes;
using log4net;
using RCAT;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Proxy
{
    public class ClientServer
    {
        class LoggingObject
        {
            public long lastupdate;
            public UserContext user;
            public long timetoprocess;

            public LoggingObject(long _lastupdate, UserContext _user, long _timetoprocess)
            {
                lastupdate = _lastupdate;
                timetoprocess = _timetoprocess;
                user = _user;
            }
        }

        WSServer clientListener = null;
        JsonSerializer serializer = new JsonSerializer();

        protected static ILog Log = null;

        private static string RoundTripLogName = Properties.Settings.Default.log_roundtrip;

        protected void RegisterProxyMethods()
        {
            Proxy.broadcastToClients = BroadcastToClients;
            Proxy.sendToClient = SendToClient;
        }

        public ClientServer(ILog log)
        {
            Log = log;
            RegisterProxyMethods();
            // Client server uses Alchemy Websockets
            clientListener = new WSServer(Properties.Settings.Default.client_listener_port, IPAddress.Any);
            clientListener.Log.Logger.IsEnabledFor(log4net.Core.Level.Debug);
            clientListener.DefaultOnReceive = new OnEventDelegate(OnReceive);
            clientListener.DefaultOnSend = new OnEventDelegate(OnSend);
            clientListener.DefaultOnConnect = new OnEventDelegate(OnConnect);
            clientListener.DefaultOnDisconnect = new OnEventDelegate(OnDisconnect);
            clientListener.TimeOut = new TimeSpan(0, 0, 30);

            clientListener.Start();
        }

        /// <summary>
        /// Events generated by client connections
        /// </summary>
        /// <param name="AContext"></param>
        public static void OnConnect(UserContext AContext)
        {
            Log.Info("[CLIENT->PROXY]: " + AContext.ClientAddress.ToString() + " connected.");

            User me = new User();
            me.n = AContext.ClientAddress.ToString();
            me.Context = AContext;
            AContext.TimeToProcess = DateTime.Now.Ticks;

            Proxy.onlineUsers.Add(me.n, me.Context);
            Proxy.sendClientConnectToServer(AContext);
        }

        /// <summary>
        /// when proxy receives a msg in JSON format from a client, log it, convert it, and forward it to servant layer
        /// </summary>
        /// <param name="AContext"></param>
        public static void OnReceive(UserContext AContext)
        {
            //Log.Info("[CLIENT->PROXY]: Received " + AContext.DataFrame.ToString() + " from : " + AContext.ClientAddress.ToString());
            long timestamp = DateTime.Now.Ticks;
            User me = new User();
            me.n = AContext.ClientAddress.ToString();
            AContext.ReceivedPackets++;
            try
            {
                string json = AContext.DataFrame.ToString();
                Position pos = JsonConvert.DeserializeObject<Position>(json);
                me.p = pos;
                //Log.Info("[CLIENT->PROXY]: Position received from Client: " + pos.t.ToString() + ":" + pos.l.ToString() + ":" + pos.z.ToString());
            }
            catch (Exception e)
            {
                // Hack: Java client is throwing weird messages filled with '/0'. Temp fix
                string test = AContext.DataFrame.ToString();
                if (test.StartsWith("\0") == false)
                    Log.Warn("[CLIENT->PROXY]: Error parsing Json into a position in ClientServer.OnReceive, JSON message was: " + AContext.DataFrame.ToString() + ". Error is: " + e.Message + e.StackTrace);
            }
            Proxy.sendSetPositionToServer(me,timestamp); // so far, the messages received only deal with user position updates

        }

        /// <summary>
        /// when a message is sent from the proxy to one client, log it
        /// </summary>
        /// <param name="AContext"></param>
        public static void OnSend(UserContext AContext)
        {
            Log.Info("[PROXY->CLIENT]: Sent: " + UTF8Encoding.UTF8.GetString(AContext.SentData) + " to: " + AContext.ClientAddress.ToString());
        }

        /// <summary>
        /// when a user disconnects, notify the servant layer
        /// </summary>
        /// <param name="AContext"></param>
        public static void OnDisconnect(UserContext AContext)
        {
            Log.Info("[CLIENT->PROXY]: Client " + AContext.ClientAddress.ToString() + " disconnected.");

            //Proxy.onlineUsers.Remove(AContext.ClientAddress.ToString());
            Proxy.sendClientDisconnectToServer(AContext); //handled by the gameserver side of the proxy
        }

        /// <summary>
        /// Stops the TCP Listener for new clients
        /// </summary>
        public void Stop()
        {
            clientListener.Stop();
        }

        
        /// <summary>
        ///send the same data to multiple clients (broadcast contains the data to send and the array of clients to send to) 
        /// </summary>
        /// <param name="broadcast"></param>
        public void BroadcastToClients(ClientMessage broadcast)
        {
            try
            {
                string name = (string)broadcast.Data.SelectToken("n");
                UserContext user = null;
                if (Proxy.onlineUsers.ContainsKey(name))
                    user = Proxy.onlineUsers[name];
                else
                {
                    Log.Debug("User " + name + " not present in this Proxy");
                    return;
                }
                long lastupdate = user.LastUpdate;
                if (broadcast.Type == ResponseType.Disconnect)
                    lastupdate = 0; // Just to be sure it will enter next if

                user.SendingSemaphore.Wait();
                if (broadcast.TimeStamp >= lastupdate)
                {
                    user.LastUpdate = broadcast.TimeStamp;
                    Message m = new Message();
                    m.Type = broadcast.Type;
                    m.Data = broadcast.Data;

                    string json = JsonConvert.SerializeObject(m);
                    foreach (string client in broadcast.clients)
                    {
                        try
                        {
                            UserContext cl = Proxy.onlineUsers[client];
                            cl.Send(json);
                        }
                        catch
                        {
                            Log.Debug("[PROXY->CLIENT]: User " + client + " not found.");
                        }
                    }
                }
                else
                {
                    user.LatePackets++;
                    user.SendingSemaphore.Release();
                    return;
                }

                if (broadcast.Type == ResponseType.Disconnect)
                {
                    if (Proxy.onlineUsers.ContainsKey(name))
                        Proxy.onlineUsers.Remove(name);
                }
                else
                {
                    user.SentCounter--;
                    if (user.SentCounter <= 0 && lastupdate > 0)
                    {
                        user.SentCounter = UserContext.DefaultSentCounter;
                        long timetoprocess = user.TimeToProcess;
                        LoggingObject logobj = new LoggingObject(broadcast.TimeStamp, user, timetoprocess);
                        ThreadPool.QueueUserWorkItem(new WaitCallback(LogRoundTrip), logobj);
                        user.TimeToProcess = DateTime.Now.Ticks;
                    }
                }
                user.SendingSemaphore.Release();
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        public void SendToClient(ClientMessage message)
        {
            string name = message.clients[0];
            UserContext user = Proxy.onlineUsers[name];
            try
            {
                Message m = new Message();
                m.Type = message.Type;
                m.Data = message.Data;

                string json = JsonConvert.SerializeObject(m);

                user.Send(json);
            }
            catch
            {
                Log.Debug("[PROXY->CLIENT]: User " + user + " not found.");
            }
        }

        static void LogRoundTrip(Object stateInfo)
        {
            LoggingObject logobj = (LoggingObject)stateInfo;
            UserContext user = logobj.user;
            long lastupdate = logobj.lastupdate;

            long now = DateTime.Now.Ticks;
            long roundtrip = now - lastupdate;
            long timeprocess = now - logobj.timetoprocess;
            user.RoundtripLog.Append(user.ClientAddress + "\t" + (roundtrip/10000).ToString() + "\t" + (timeprocess/10000).ToString() + "\t" + user.LatePackets.ToString() + "\t" + user.ReceivedPackets + "\n");
            user.LatePackets = 0;
            user.ReceivedPackets = 0;

            // Flush every 10 Seconds
            if (now - Proxy.startTime > 100000)
            {
                Proxy.DiskLock.Wait();
                // Example #4: Append new text to an existing file
                using (System.IO.StreamWriter file = new System.IO.StreamWriter(@"C:\Temp\"+RoundTripLogName, true))
                {
                    file.Write(user.RoundtripLog);
                }
                Proxy.DiskLock.Release();
            }
            Proxy.startTime = now;
            user.RoundtripLog.Clear();
        }
    }
}
