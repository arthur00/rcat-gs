﻿using System;
using System.Text;
using System.Net.Sockets;
using System.Threading;
using System.Net;
using log4net;
using RCAT;
using System.Collections.Generic;
using Alchemy.Server.Classes;

namespace Proxy
{
    

    public class GameServer
    {
        protected static TcpListener serverListener = null;

        protected List<ServerContext> onlineServers = new List<ServerContext>();
        protected Dictionary<string, ServerContext> clientPerServer = new Dictionary<string, ServerContext>();

        protected TimeSpan TimeOut = new TimeSpan(0, 2, 0);

        protected int roundrobin = 0;

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
                                SContext.serverConnection.Client.BeginReceive(SContext.Buffer, 0, SContext.Buffer.Length, SocketFlags.None, new AsyncCallback(DoReceive), SContext);
                            }
                            else
                            {
                                Log.Warn("TIMED OUT");
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

            // TODO: No packets bigger then BufferSize are allowed at this time
            if (received > 0)
            {
                SContext.sb.Append(UTF8Encoding.UTF8.GetString(SContext.Buffer, 0, received));
                HandleRequest(SContext);
                SContext.ReceiveReady.Release();
                if (received == ServerContext.BufferSize)
                {
                    throw new Exception("[GAMESERVER]: HTTP Connect packet reached maximum size. FIXME!!");
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
            Message message = Newtonsoft.Json.JsonConvert.DeserializeObject<Message>(server.sb.ToString());
            if (message.Type == ResponseType.Position)
                Proxy.broadcastToClients(message.Data);
            // TODO: Implement SendAllUsers
        }

        // Sends client data to the server
        public void SendPosition(User client)
        {
            ServerContext server = clientPerServer[client.Name];
            
            Message resp = new Message();
            resp.Type = ResponseType.Position;
            resp.Data = client;

            server.Send(UTF8Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(resp)));
        }

        public void SendClientConnect(UserContext client)
        {
            ServerContext server = PickServer();
            clientPerServer.Add(client.ClientAddress.ToString(), server);

            Message resp = new Message();
            resp.Type = ResponseType.Connection;
            resp.Data = client.ClientAddress.ToString();

            server.Send(UTF8Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(resp)));
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
            ServerContext server = clientPerServer[client.ClientAddress.ToString()];
            clientPerServer.Remove(client.ClientAddress.ToString());

            Message resp = new Message();
            resp.Type = ResponseType.Disconnect;
            resp.Data = client.ClientAddress.ToString();

            server.Send(UTF8Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(resp)));
        }

        public void Stop()
        {
            serverListener.Stop();
        }
    }
}