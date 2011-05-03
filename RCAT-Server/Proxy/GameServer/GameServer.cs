﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Alchemy.Server.Classes;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RCAT;

namespace Proxy
{
    
    public class GameServer
    {
        protected static TcpListener serverListener = null;

        protected List<ServerContext> onlineServers = new List<ServerContext>();

        protected TimeSpan TimeOut = new TimeSpan(0, 30, 0);

        /// <summary>
        /// This Semaphore limits how many connection events we have active at a time.
        /// </summary>
        private SemaphoreSlim ConnectReady = new SemaphoreSlim(10);

        protected int roundrobin = 0;

        protected JsonSerializer serializer = new JsonSerializer();

        public static ILog Log;

        public int serverPort = 882;

        protected void RegisterProxyMethods()
        {
            Proxy.sendSetPositionToServer = SendPosition;
            Proxy.sendClientDisconnectToServer = SendClientDisconnect;
            Proxy.sendClientConnectToServer = SendClientConnect;
        }

        public GameServer(ILog log)
        {
            // Servers register their existence and communicate with proxy through TCP
            Log = log;
            RegisterProxyMethods();

            if (serverListener == null)
            {
                try
                {
                    serverListener = new TcpListener(IPAddress.Any, serverPort);
                    ThreadPool.QueueUserWorkItem(serverListen, null);
                }
                catch { Log.Error("Game Server failed to start"); }
            }

            // Accept commands on the console and keep it alive
        }

        protected void serverListen(object State)
        {
            serverListener.Start();
            while (serverListener != null)
            {
                try
                {
                    serverListener.BeginAcceptTcpClient(RunServer, null);
                    ConnectReady.Wait();
                }
                catch {/* Ignore */ }
            }
        }

        protected void RunServer(IAsyncResult AResult)
        {
            // Server connection
            TcpClient TcpConnection = null;
            try
            {
                if (serverListener != null)
                    TcpConnection = serverListener.EndAcceptTcpClient(AResult);
            }
            catch (Exception e) { Log.Error("Connect Failed", e); }
            
            ConnectReady.Release();
            if (TcpConnection != null)
            {
                using (ServerContext SContext = new ServerContext())
                //each server has its own context
                {
                    SContext.gameServer = this;
                    SContext.serverConnection = TcpConnection;
                    SContext.ClientAddress = SContext.serverConnection.Client.RemoteEndPoint;
                    onlineServers.Add(SContext);
                    try
                    {
                        while (SContext.serverConnection.Connected)
                        {
                            if (SContext.ReceiveReady.Wait(TimeOut))
                            {
                                SContext.serverConnection.Client.BeginReceive(SContext.Buffer, 0, SContext.Buffer.Length, SContext.sflag, new AsyncCallback(DoReceive), SContext);
                            }
                            else
                            {
                                Log.Warn("Game Server timed out. Disconnecting.");
                                break;
                            }
                        }
                    }
                    catch (Exception e) { Log.Error("Game Server Forcefully Disconnected", e); }
                }
            }
        }

        // Events generated by servers connecting to proxy
        private void DoReceive(IAsyncResult AResult)
        {
            ServerContext SContext = (ServerContext)AResult.AsyncState;
            int received = 0;

            try
            {
                received = SContext.serverConnection.Client.EndReceive(AResult);
            }
            catch (Exception e) { Log.Error("[GAMESERVER]: Game Server Forcefully Disconnected", e); }

            if (received > 0)
            {
                string values = UTF8Encoding.UTF8.GetString(SContext.Buffer, 0, received);
                if ((received == RCATContext.DefaultBufferSize && !values.EndsWith("\0")) || SContext.IsTruncated)
                {
                    // There is more data to retrieve. Save current message and prepare for more
                    if (SContext.IsTruncated == false)
                    {
                        // Last message was not truncated
                        SContext.sb = values.Split(new char[]{'\0'} , StringSplitOptions.RemoveEmptyEntries);
                        SContext.IsTruncated = true;
                        SContext.serverConnection.Client.BeginReceive(SContext.Buffer, 0, RCATContext.DefaultBufferSize, SocketFlags.None, new AsyncCallback(DoReceive), SContext);
                    }
                    else
                    {
                        string[] tmp = values.Split(new char[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);


                        var list = new List<string>();
                        list.AddRange(SContext.sb);
                        // Append last element in RContext.sb to first element of tmp array
                        list[SContext.sb.Length - 1] = list[SContext.sb.Length - 1] + tmp[0];
                        // Exclude the first element of tmp, and add it to the list
                        var segment = new ArraySegment<string>(tmp, 1, tmp.Length - 1);
                        list.AddRange(segment.Array);

                        SContext.sb = list.ToArray();
                        Log.Info("[RCAT]: Appended truncated message.");

                        if (values.EndsWith("\0"))
                        {
                            SContext.IsTruncated = false;
                            HandleRequest(SContext);
                            SContext.ReceiveReady.Release();
                        }
                        else
                        {
                            SContext.serverConnection.Client.BeginReceive(SContext.Buffer, 0, RCATContext.DefaultBufferSize, SocketFlags.None, new AsyncCallback(DoReceive), SContext);
                        }
                    }
                }
                else
                {
                    SContext.sb = values.Split('\0');
                    HandleRequest(SContext);
                    SContext.ReceiveReady.Release();
                }

            }
            else
            {
                onlineServers.Remove(SContext);
                SContext.Dispose();
            }
        }

        // Handles the server request. If position, message.data is a ClientBroadcast object. 
        protected void HandleRequest(ServerContext server)
        {
            int i = 0;
            //Newtonsoft.Json.Linq.JObject test = new Newtonsoft.Json.Linq.JObject();
            //test.Value<ClientBroadcast>(test)
             //   User Value<User>(message.Data)
            try
            {
                foreach (string s in server.sb)
                {
                    if (s != "")
                    {
                        Message message = Newtonsoft.Json.JsonConvert.DeserializeObject<Message>(s);
                        if (message.Type == ResponseType.Position)
                        {
                            ClientBroadcast cb = (ClientBroadcast)serializer.Deserialize(new JTokenReader(message.Data), typeof(ClientBroadcast));
                            Proxy.broadcastToClients(cb);
                            // TODO: Implement SendAllUsers
                        }
                        i++;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("Error parsing JSON in GameServer.HandleRequest. JSON: " + server.sb[i]);
                //Log.Error("Error parsing JSON in GameServer.HandleRequest",e);
                Log.Debug(e);
            }
        }

        // Sends client data to the server
        public void SendPosition(User client)
        {
            //ServerContext server = clientPerServer[client.Name];
            ServerContext server = PickServer();
            
            TimeStampedMessage resp = new TimeStampedMessage();
            resp.Type = ResponseType.Position;
            resp.Data = client;
            resp.TimeStamp = DateTime.Now.Ticks;

            Log.Info("Sending Client info: " + resp.Data.ToString());
            
            server.Send(Newtonsoft.Json.JsonConvert.SerializeObject(resp) + '\0');
        }

        public void SendClientConnect(UserContext client)
        {
            ServerContext server = PickServer();

            Message resp = new Message();
            resp.Type = ResponseType.Connection;
            resp.Data = client.ClientAddress.ToString();

            server.Send(Newtonsoft.Json.JsonConvert.SerializeObject(resp));
        }

        protected ServerContext PickServer()
        {
            ServerContext server =  onlineServers[roundrobin];
            roundrobin++;
            if (roundrobin == onlineServers.Count)
                roundrobin = 0;
            return server;
        }

        public void SendClientDisconnect(UserContext client)
        {
            ServerContext server = PickServer();

            Message resp = new Message();
            resp.Type = ResponseType.Disconnect;
            resp.Data = client.ClientAddress.ToString();

            server.Send(Newtonsoft.Json.JsonConvert.SerializeObject(resp));
        }

        public void Stop()
        {
            serverListener.Stop();
        }
    }
}
