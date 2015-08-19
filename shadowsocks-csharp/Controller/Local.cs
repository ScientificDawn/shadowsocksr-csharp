﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Net.Sockets;
using Shadowsocks.Encryption;
using Shadowsocks.Model;
using System.Timers;

namespace Shadowsocks.Controller
{

    class Local : Listener.Service
    {
        private Configuration _config;
        public Local(Configuration config)
        {
            this._config = config;
        }

        public bool Handle(byte[] firstPacket, int length, Socket socket)
        {
            if (length < 2
                || (firstPacket[0] != 5 && firstPacket[0] != 4))
            {
                return false;
            }
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
            Handler handler = new Handler();

            //handler.config = _config;
            handler.getCurrentServer = delegate(bool usingRandom, bool forceRandom) { return _config.GetCurrentServer(usingRandom, forceRandom); };
            handler.connection = socket;
            handler.reconnectTimesRemain = _config.reconnectTimes;

            handler.server = _config.GetCurrentServer(true);
            if (_config.socks5enable)
            {
                handler.socks5RemoteHost = _config.socks5Host;
                handler.socks5RemotePort = _config.socks5Port;
                handler.socks5RemoteUsername = _config.socks5User;
                handler.socks5RemotePassword = _config.socks5Pass;
            }
            handler.TTL = _config.TTL;

            handler.Start(firstPacket, length);
            return true;
        }
    }

    class SpeedTester
    {
        public DateTime timeConnectBegin;
        public DateTime timeConnectEnd;
        public DateTime timeBeginUpload;
        public DateTime timeBeginDownload;
        public long sizeUpload = 0;
        public long sizeDownload = 0;
        private List<TransLog> sizeDownloadList = new List<TransLog>();

        public void BeginConnect()
        {
            timeConnectBegin = DateTime.Now;
        }

        public void EndConnect()
        {
            timeConnectEnd = DateTime.Now;
        }

        public void BeginUpload()
        {
            timeBeginUpload = DateTime.Now;
        }

        public void BeginDownload()
        {
            timeBeginDownload = DateTime.Now;
        }

        public void AddDownloadSize(int size)
        {
            if (sizeDownloadList.Count == 2)
                sizeDownloadList[1] = new TransLog(size, DateTime.Now);
            else
                sizeDownloadList.Add(new TransLog(size, DateTime.Now));
            sizeDownload += size;
        }

        public void AddUploadSize(int size)
        {
            sizeUpload += size;
        }

        public long GetAvgDownloadSpeed()
        {
            if (sizeDownloadList == null || sizeDownloadList.Count < 2 || (sizeDownloadList[sizeDownloadList.Count - 1].recvTime - sizeDownloadList[0].recvTime).TotalSeconds <= 0.001)
                return 0;
            return (long)((sizeDownload - sizeDownloadList[0].size) / (sizeDownloadList[sizeDownloadList.Count - 1].recvTime - sizeDownloadList[0].recvTime).TotalSeconds);
        }
    }

    class Handler
    {
        //public Configuration config; // for GetCurrentServer(true) only
        public delegate Server GetCurrentServer(bool usingRandom = false, bool forceRandom = false);
        public GetCurrentServer getCurrentServer;
        public Server server;
        public Double TTL = 0; // Second
        // Connection socket
        public Socket connection;
        public Socket connectionUDP;
        protected IPEndPoint connectionUDPEndPoint;
        // Server socks5 proxy
        public string socks5RemoteHost;
        public int socks5RemotePort = 0;
        public string socks5RemoteUsername;
        public string socks5RemotePassword;
        // Reconnect
        public int reconnectTimesRemain = 0;
        protected int reconnectTimes = 0;
        //public Encryptor encryptor;
        protected IEncryptor encryptor;
        protected IEncryptor encryptorUDP;
        // remote socket.
        protected Socket remote;
        protected Socket remoteUDP;
        protected IPEndPoint remoteUDPEndPoint;
        // Connect command
        protected byte command;
        // Init data
        protected byte[] _firstPacket;
        protected int _firstPacketLength;
        // Size of receive buffer.
        protected const int RecvSize = 16384;
        protected const int BufferSize = RecvSize + 32;
        protected const int AutoSwitchOffErrorTimes = 50;
        // remote receive buffer
        protected byte[] remoteRecvBuffer = new byte[RecvSize];
        // remote send buffer
        protected byte[] remoteSendBuffer = new byte[BufferSize];
        // remote header send buffer
        protected byte[] remoteHeaderSendBuffer;
        // connection receive buffer
        protected byte[] connetionRecvBuffer = new byte[RecvSize];
        // connection send buffer
        protected byte[] connetionSendBuffer = new byte[BufferSize];

        protected byte[] remoteUDPRecvBuffer = new byte[RecvSize * 2];
        protected int remoteUDPRecvBufferLength = 0;

        protected bool connectionShutdown = false;
        protected bool remoteShutdown = false;
        protected bool closed = false;

        protected object encryptionLock = new object();
        protected object decryptionLock = new object();
        protected object recvUDPoverTCPLock = new object();

        protected bool connectionTCPIdle;
        protected bool connectionUDPIdle;
        protected bool remoteTCPIdle;
        protected bool remoteUDPIdle;

        protected SpeedTester speedTester = new SpeedTester();
        protected int lastErrCode;
        protected bool autoSwitchOff = true;
        protected Random random = new Random();
        protected Timer timer;
        protected object timerLock = new object();
        protected int connectionPacketNumber;

        enum ConnectState
        {
            END = -1,
            READY = 0,
            HANDSHAKE = 1,
            CONNECTING = 2,
            CONNECTED = 3,
        }
        private ConnectState state = ConnectState.READY;

        private ConnectState State
        {
            get
            {
                return this.state;
            }
            set
            {
                lock (server)
                {
                    this.state = value;
                }
            }
        }

        private void ResetTimeout(Double time)
        {
            if (time <= 0 && timer == null)
                return;

            lock (timerLock)
            {
                if (time <= 0)
                {
                    if (timer != null)
                    {
                        timer.Enabled = false;
                        timer.Elapsed -= timer_Elapsed;
                        timer.Dispose();
                        timer = null;
                    }
                }
                else
                {
                    if (timer == null)
                    {
                        timer = new Timer(time * 1000.0);
                        timer.Elapsed += timer_Elapsed;
                        timer.Start();
                    }
                    else
                    {
                        timer.Interval = time * 1000.0;
                        timer.Stop();
                        timer.Start();
                    }
                }
            }
        }

        private void timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (closed)
            {
                return;
            }
            try
            {
                if (connection != null)
                {
                    if (lastErrCode == 0)
                    {
                        lastErrCode = 8;
                        if (speedTester.sizeDownload == 0)
                        {
                            server.ServerSpeedLog().AddTimeoutTimes();
                            if (server.ServerSpeedLog().ErrorContinurousTimes >= AutoSwitchOffErrorTimes && autoSwitchOff)
                            {
                                server.setEnable(false);
                            }
                        }
                    }
                    connection.Shutdown(SocketShutdown.Both);
                }
            }
            catch (Exception)
            {
                //
            }
        }

        public int LogSocketException(Exception e)
        {
            // just log useful exceptions, not all of them
            if (e is SocketException)
            {
                SocketException se = (SocketException)e;
                if (se.SocketErrorCode == SocketError.ConnectionAborted)
                {
                    // closed by browser when sending
                    // normally happens when download is canceled or a tab is closed before page is loaded
                }
                else if (se.ErrorCode == 11004)
                {
                    if (lastErrCode == 0)
                    {
                        lastErrCode = 1;
                        server.ServerSpeedLog().AddErrorTimes();
                        if (server.ServerSpeedLog().ErrorConnectTimes >= 3 && autoSwitchOff)
                        {
                            server.setEnable(false);
                        }
                    }
                    return 1; // proxy DNS error
                }
                else if (se.SocketErrorCode == SocketError.HostNotFound)
                {
                    if (lastErrCode == 0)
                    {
                        lastErrCode = 2;
                        server.ServerSpeedLog().AddErrorTimes();
                        if (server.ServerSpeedLog().ErrorConnectTimes >= 3 && autoSwitchOff)
                        {
                            server.setEnable(false);
                        }
                    }
                    return 2; // ip not exist
                }
                else if (se.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    if (lastErrCode == 0)
                    {
                        lastErrCode = 1;
                        server.ServerSpeedLog().AddErrorTimes();
                        if (server.ServerSpeedLog().ErrorConnectTimes >= 3 && autoSwitchOff)
                        {
                            server.setEnable(false);
                        }
                    }
                    return 2; // proxy ip/port error
                }
                else if (se.SocketErrorCode == SocketError.NetworkUnreachable)
                {
                    if (lastErrCode == 0)
                    {
                        lastErrCode = 3;
                        server.ServerSpeedLog().AddErrorTimes();
                        if (server.ServerSpeedLog().ErrorConnectTimes >= 3 && autoSwitchOff)
                        {
                            server.setEnable(false);
                        }
                    }
                    return 3; // proxy ip/port error
                }
                else if (se.SocketErrorCode == SocketError.TimedOut)
                {
                    if (lastErrCode == 0)
                    {
                        lastErrCode = 8;
                        if (speedTester.sizeDownload == 0)
                        {
                            server.ServerSpeedLog().AddTimeoutTimes();
                            if (server.ServerSpeedLog().ErrorContinurousTimes >= AutoSwitchOffErrorTimes && autoSwitchOff)
                            {
                                server.setEnable(false);
                            }
                        }
                    }
                    return 8; // proxy server no response too slow
                }
                else
                {
                    if (lastErrCode == 0)
                    {
                        lastErrCode = -1;
                        if (this.State != ConnectState.CONNECTED || this.State != ConnectState.END)
                        {
                            server.ServerSpeedLog().AddNoDataTimes();
                            if (server.ServerSpeedLog().ErrorContinurousTimes >= AutoSwitchOffErrorTimes && autoSwitchOff)
                            {
                                server.setEnable(false);
                            }
                        }
                    }
                    return 0;
                }
            }
            return 0;
        }

        public void ReConnect()
        {
            ResetTimeout(0);

            reconnectTimesRemain--;
            reconnectTimes++;

            lock (server)
            {
                server.ServerSpeedLog().AddDisconnectTimes();
                server.GetConnections().DecRef(this.connection);
            }

            if (reconnectTimes < 2)
            {
                server = this.getCurrentServer(true);
            }
            else
            {
                server = this.getCurrentServer(true, true);
            }

            CloseSocket(ref remote);
            CloseSocket(ref remoteUDP);

            connectionShutdown = false;
            remoteShutdown = false;

            speedTester.sizeUpload = 0;
            speedTester.sizeDownload = 0;

            lastErrCode = 0;

            try
            {
                Connect();
            }
            catch (Exception e)
            {
                LogSocketException(e);
                if (!Logging.LogSocketException(server.remarks, server.server, e))
                    Logging.LogUsefulException(e);
                this.Close();
            }
        }

        public void Start(byte[] firstPacket, int length)
        {
            this._firstPacket = firstPacket;
            this._firstPacketLength = length;
            if (socks5RemotePort > 0)
            {
                autoSwitchOff = false;
            }
            if (this.State == ConnectState.READY)
            {
                this.State = ConnectState.HANDSHAKE;
                this.HandshakeReceive();
            }
        }

        private void BeginConnect(IPAddress ipAddress, int serverPort)
        {
            IPEndPoint remoteEP = new IPEndPoint(ipAddress, serverPort);
            remoteUDPEndPoint = remoteEP;

            if (socks5RemotePort != 0
                || connectionUDP == null && !server.tcp_over_udp
                || connectionUDP != null && server.udp_over_tcp)
            {
                remote = new Socket(ipAddress.AddressFamily,
                    SocketType.Stream, ProtocolType.Tcp);
                remote.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
            }

            if (connectionUDP != null && !server.udp_over_tcp)
            {
                try
                {
                    remoteUDP = new Socket(ipAddress.AddressFamily,
                        SocketType.Dgram, ProtocolType.Udp);
                    remoteUDP.Bind(new IPEndPoint(ipAddress.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
                }
                catch (SocketException)
                {
                    remoteUDP = null;
                }
            }

            {
                // Connect to the remote endpoint.
                if (socks5RemotePort == 0 && connectionUDP != null && !server.udp_over_tcp)
                {
                    ConnectState _state = this.State;
                    if (_state == ConnectState.CONNECTING)
                    {
                        this.State = ConnectState.CONNECTED;
                        StartPipe();
                    }
                    else if (_state == ConnectState.CONNECTED)
                    {
                        //ERROR
                    }
                }
                else
                {
                    speedTester.BeginConnect();
                    remote.BeginConnect(remoteEP,
                        new AsyncCallback(ConnectCallback), null);
                }
            }
        }

        private void DnsCallback(IAsyncResult ar)
        {
            if (closed)
            {
                return;
            }
            try
            {
                IPAddress ipAddress;
                IPHostEntry ipHostInfo = Dns.EndGetHostEntry(ar);
                ipAddress = ipHostInfo.AddressList[0];
                int serverPort = server.server_port;
                BeginConnect(ipAddress, serverPort);
            }
            catch (Exception e)
            {
                LogSocketException(e);
                if (!Logging.LogSocketException(server.remarks, server.server, e))
                    Logging.LogUsefulException(e);
                this.Close();
            }
        }

        private void CheckClose()
        {
            if (connectionShutdown && remoteShutdown)
            {
                this.Close();
            }
        }

        public bool TryReconnect()
        {
            if (connectionShutdown)
            {
            }
            else if (this.State == ConnectState.CONNECTING)
            {
                if (reconnectTimesRemain > 0)
                {
                    this.ReConnect();
                    return true;
                }
            }
            return false;
        }

        private void CloseSocket(ref Socket sock)
        {
            if (sock != null)
            {
                try
                {
                    sock.Shutdown(SocketShutdown.Both);
                    sock.Close();
                }
                catch (Exception e)
                {
                    //Logging.LogUsefulException(e);
                }
                sock = null;
            }
        }

        public void Close()
        {
            lock (this)
            {
                if (closed)
                {
                    return;
                }
                closed = true;
            }
            try
            {
                if (TryReconnect())
                    return;
                lock (server)
                {
                    if (this.State != ConnectState.END)
                    {
                        this.State = ConnectState.END;
                        server.ServerSpeedLog().AddDisconnectTimes();
                        server.GetConnections().DecRef(this.connection);
                        server.ServerSpeedLog().AddSpeedLog(new TransLog((int)speedTester.GetAvgDownloadSpeed(), DateTime.Now));
                    }
                    getCurrentServer = null;
                    ResetTimeout(0);
                    speedTester = null;
                }
                CloseSocket(ref connection);
                CloseSocket(ref connectionUDP);
                CloseSocket(ref remote);
                CloseSocket(ref remoteUDP);

                lock (encryptionLock)
                {
                    lock (decryptionLock)
                    {
                        if (encryptor != null)
                            ((IDisposable)encryptor).Dispose();
                        if (encryptorUDP != null)
                            ((IDisposable)encryptorUDP).Dispose();
                    }
                }
            }
            catch (Exception e)
            {
                Logging.LogUsefulException(e);
            }
        }

        private bool ConnectProxyServer(string strRemoteHost, int iRemotePort, Socket sProxyServer, int socketErrorCode)
        {
            //构造Socks5代理服务器第一连接头(无用户名密码)
            byte[] bySock5Send = new Byte[10];
            bySock5Send[0] = 5;
            bySock5Send[1] = 1;
            bySock5Send[2] = 0;

            //发送Socks5代理第一次连接信息
            sProxyServer.Send(bySock5Send, 3, SocketFlags.None);

            byte[] bySock5Receive = new byte[32];
            int iRecCount = sProxyServer.Receive(bySock5Receive, bySock5Receive.Length, SocketFlags.None);

            if (iRecCount < 2)
            {
                //sProxyServer.Close();
                throw new SocketException(socketErrorCode);
                //throw new Exception("不能获得代理服务器正确响应。");
            }

            if (bySock5Receive[0] != 5 || (bySock5Receive[1] != 0 && bySock5Receive[1] != 2))
            {
                //sProxyServer.Close();
                throw new SocketException(socketErrorCode);
                //throw new Exception("代理服务其返回的响应错误。");
            }

            if (bySock5Receive[1] != 0) // auth
            {
                if (bySock5Receive[1] == 2)
                {
                    if (socks5RemoteUsername.Length == 0)
                    {
                        throw new SocketException(socketErrorCode);
                        //throw new Exception("代理服务器需要进行身份确认。");
                    }
                    else
                    {
                        bySock5Send = new Byte[socks5RemoteUsername.Length + socks5RemotePassword.Length + 3];
                        bySock5Send[0] = 1;
                        bySock5Send[1] = (Byte)socks5RemoteUsername.Length;
                        for (int i = 0; i < socks5RemoteUsername.Length; ++i)
                        {
                            bySock5Send[2 + i] = (Byte)socks5RemoteUsername[i];
                        }
                        bySock5Send[socks5RemoteUsername.Length + 2] = (Byte)socks5RemotePassword.Length;
                        for (int i = 0; i < socks5RemotePassword.Length; ++i)
                        {
                            bySock5Send[socks5RemoteUsername.Length + 3 + i] = (Byte)socks5RemotePassword[i];
                        }
                        sProxyServer.Send(bySock5Send, bySock5Send.Length, SocketFlags.None);
                        iRecCount = sProxyServer.Receive(bySock5Receive, bySock5Receive.Length, SocketFlags.None);

                        if (bySock5Receive[0] != 1 || bySock5Receive[1] != 0)
                        {
                            throw new SocketException((int)SocketError.ConnectionRefused);
                        }
                    }
                }
                else
                {
                    return false;
                }
            }
            // connect
            if (command == 1) // TCP
            {
                List<byte> dataSock5Send = new List<byte>();
                dataSock5Send.Add(5);
                dataSock5Send.Add(1);
                dataSock5Send.Add(0);

                IPAddress ipAdd;
                bool ForceRemoteDnsResolve = false;
                bool parsed = IPAddress.TryParse(strRemoteHost, out ipAdd);
                if (!parsed && !ForceRemoteDnsResolve)
                {
                    if (server.DnsTargetBuffer().isExpired(strRemoteHost))
                    {
                        try
                        {
                            IPHostEntry ipHostInfo = Dns.GetHostEntry(strRemoteHost);
                            ipAdd = ipHostInfo.AddressList[0];
                            server.DnsTargetBuffer().UpdateDns(strRemoteHost, ipAdd);
                        }
                        catch (Exception)
                        {
                        }
                    }
                    else
                    {
                        ipAdd = server.DnsTargetBuffer().ip;
                    }
                }
                if (ipAdd == null)
                {
                    dataSock5Send.Add(3); // remote DNS resolve
                    dataSock5Send.Add((byte)strRemoteHost.Length);
                    for (int i = 0; i < strRemoteHost.Length; ++i)
                    {
                        dataSock5Send.Add((byte)strRemoteHost[i]);
                    }
                }
                else
                {
                    byte[] addBytes = ipAdd.GetAddressBytes();
                    if (addBytes.GetLength(0) > 4)
                    {
                        dataSock5Send.Add(4); // IPv6
                        for (int i = 0; i < 16; ++i)
                        {
                            dataSock5Send.Add(addBytes[i]);
                        }
                    }
                    else
                    {
                        dataSock5Send.Add(1); // IPv4
                        for (int i = 0; i < 4; ++i)
                        {
                            dataSock5Send.Add(addBytes[i]);
                        }
                    }
                }

                dataSock5Send.Add((byte)(iRemotePort / 256));
                dataSock5Send.Add((byte)(iRemotePort % 256));

                //sProxyServer.Send(bySock5Send, bySock5Send.Length, SocketFlags.None);
                sProxyServer.Send(dataSock5Send.ToArray(), dataSock5Send.Count, SocketFlags.None);
                iRecCount = sProxyServer.Receive(bySock5Receive, bySock5Receive.Length, SocketFlags.None);

                if (bySock5Receive[0] != 5 || bySock5Receive[1] != 0)
                {
                    //sProxyServer.Close();
                    throw new SocketException(socketErrorCode);
                    //throw new Exception("第二次连接Socks5代理返回数据出错。");
                }
                return true;
            }
            else if (command == 3) // UDP
            {
                List<byte> dataSock5Send = new List<byte>();
                dataSock5Send.Add(5);
                dataSock5Send.Add(3);
                dataSock5Send.Add(0);

                IPAddress ipAdd = remoteUDPEndPoint.Address;
                {
                    byte[] addBytes = ipAdd.GetAddressBytes();
                    if (addBytes.GetLength(0) > 4)
                    {
                        dataSock5Send.Add(4); // IPv6
                        for (int i = 0; i < 16; ++i)
                        {
                            dataSock5Send.Add(addBytes[i]);
                        }
                    }
                    else
                    {
                        dataSock5Send.Add(1); // IPv4
                        for (int i = 0; i < 4; ++i)
                        {
                            dataSock5Send.Add(addBytes[i]);
                        }
                    }
                }

                dataSock5Send.Add((byte)(0));
                dataSock5Send.Add((byte)(0));

                //sProxyServer.Send(bySock5Send, bySock5Send.Length, SocketFlags.None);
                sProxyServer.Send(dataSock5Send.ToArray(), dataSock5Send.Count, SocketFlags.None);
                iRecCount = sProxyServer.Receive(bySock5Receive, bySock5Receive.Length, SocketFlags.None);

                if (bySock5Receive[0] != 5 || bySock5Receive[1] != 0)
                {
                    //sProxyServer.Close();
                    throw new SocketException(socketErrorCode);
                    //throw new Exception("第二次连接Socks5代理返回数据出错。");
                }
                else
                {
                    bool ipv6 = bySock5Receive[0] == 4;
                    byte[] addr;
                    int port;
                    if (!ipv6)
                    {
                        addr = new byte[4];
                        Array.Copy(bySock5Receive, 4, addr, 0, 4);
                        port = bySock5Receive[8] * 0x100 + bySock5Receive[9];
                    }
                    else
                    {
                        addr = new byte[16];
                        Array.Copy(bySock5Receive, 4, addr, 0, 16);
                        port = bySock5Receive[20] * 0x100 + bySock5Receive[21];
                    }
                    ipAdd = new IPAddress(addr);
                    remoteUDPEndPoint = new IPEndPoint(ipAdd, port);
                }
                return true;
            }
            return false;
        }

        private static bool IsIPv4MappedToIPv6(IPAddress ip)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
                return true;
            byte[] addr = ip.GetAddressBytes();
            for (int i = 0; i < 10; ++i)
                if (addr[0] != 0)
                    return false;
            if (addr[10] != 0xFF || addr[11] != 0xFF)
            {
                return false;
            }
            return true;
        }

        private void RspSocks4aHandshakeReceive()
        {
            List<byte> firstPacket = new List<byte>();
            for (int i = 0; i < _firstPacketLength; ++i)
            {
                firstPacket.Add(_firstPacket[i]);
            }
            List<byte> dataSockSend = firstPacket.GetRange(0, 4);
            dataSockSend[0] = 0;
            dataSockSend[1] = 90;

            bool remoteDNS = (_firstPacket[4] == 0 && _firstPacket[5] == 0 && _firstPacket[6] == 0 && _firstPacket[7] == 1) ? true : false;
            if (remoteDNS)
            {
                for (int i = 0; i < 4; ++i)
                {
                    dataSockSend.Add(0);
                }
                int addrStartPos = firstPacket.IndexOf(0x0, 8);
                List<byte> addr = firstPacket.GetRange(addrStartPos + 1, firstPacket.Count - addrStartPos - 2);
                remoteHeaderSendBuffer = new byte[2 + addr.Count + 2];
                remoteHeaderSendBuffer[0] = 3;
                remoteHeaderSendBuffer[1] = (byte)addr.Count;
                Array.Copy(addr.ToArray(), 0, remoteHeaderSendBuffer, 2, addr.Count);
                remoteHeaderSendBuffer[2 + addr.Count] = dataSockSend[2];
                remoteHeaderSendBuffer[2 + addr.Count + 1] = dataSockSend[3];
            }
            else
            {
                for (int i = 0; i < 4; ++i)
                {
                    dataSockSend.Add(_firstPacket[4 + i]);
                }
                remoteHeaderSendBuffer = new byte[1 + 4 + 2];
                remoteHeaderSendBuffer[0] = 1;
                Array.Copy(dataSockSend.ToArray(), 4, remoteHeaderSendBuffer, 1, 4);
                remoteHeaderSendBuffer[1 + 4] = dataSockSend[2];
                remoteHeaderSendBuffer[1 + 4 + 1] = dataSockSend[3];
            }
            command = 1; // Set TCP connect command
            connection.BeginSend(dataSockSend.ToArray(), 0, dataSockSend.Count, 0, new AsyncCallback(StartConnect), null);
        }

        private void RspSocks5HandshakeReceive()
        {
            byte[] response = { 5, 0 };
            if (_firstPacket[0] != 5)
            {
                response = new byte[] { 0, 91 };
                Console.WriteLine("socks 4/5 protocol error");
            }
            connection.BeginSend(response, 0, response.Length, 0, new AsyncCallback(HandshakeSendCallback), null);
        }

        private void HandshakeReceive()
        {
            if (closed)
            {
                return;
            }
            try
            {
                int bytesRead = _firstPacketLength;

                if (bytesRead > 1)
                {
                    if (_firstPacket[0] == 4 && _firstPacketLength >= 9)
                    {
                        RspSocks4aHandshakeReceive();
                    }
                    else
                    {
                        RspSocks5HandshakeReceive();
                    }
                }
                else
                {
                    this.Close();
                }
            }
            catch (Exception e)
            {
                LogSocketException(e);
                if (!Logging.LogSocketException(server.remarks, server.server, e))
                    Logging.LogUsefulException(e);
                this.Close();
            }
        }

        private void HandshakeSendCallback(IAsyncResult ar)
        {
            if (closed)
            {
                return;
            }
            try
            {
                connection.EndSend(ar);

                // +----+-----+-------+------+----------+----------+
                // |VER | CMD |  RSV  | ATYP | DST.ADDR | DST.PORT |
                // +----+-----+-------+------+----------+----------+
                // | 1  |  1  | X'00' |  1   | Variable |    2     |
                // +----+-----+-------+------+----------+----------+
                // Recv first 3 bytes
                connection.BeginReceive(connetionRecvBuffer, 0, 3, 0,
                    new AsyncCallback(HandshakeReceive2Callback), null);
            }
            catch (Exception e)
            {
                LogSocketException(e);
                if (!Logging.LogSocketException(server.remarks, server.server, e))
                    Logging.LogUsefulException(e);
                this.Close();
            }
        }

        private void RspSocks5UDPHeader(int bytesRead)
        {
            bool ipv6 = connection.AddressFamily == AddressFamily.InterNetworkV6;
            int udpPort = 0;
            if (bytesRead >= 3 + 6)
            {
                ipv6 = remoteHeaderSendBuffer[0] == 4;
                if (!ipv6)
                    udpPort = remoteHeaderSendBuffer[5] * 0x100 + remoteHeaderSendBuffer[6];
                else
                    udpPort = remoteHeaderSendBuffer[17] * 0x100 + remoteHeaderSendBuffer[18];
            }
            if (!ipv6)
            {
                remoteHeaderSendBuffer = new byte[1 + 4 + 2];
                remoteHeaderSendBuffer[0] = 0x10 + 1;
                remoteHeaderSendBuffer[5] = (byte)(udpPort / 0x100);
                remoteHeaderSendBuffer[6] = (byte)(udpPort % 0x100);
            }
            else
            {
                remoteHeaderSendBuffer = new byte[1 + 16 + 2];
                remoteHeaderSendBuffer[0] = 0x10 + 4;
                remoteHeaderSendBuffer[17] = (byte)(udpPort / 0x100);
                remoteHeaderSendBuffer[18] = (byte)(udpPort % 0x100);
            }

            connectionUDPEndPoint = null;
            int port = 0;
            IPAddress ip = ipv6 ? IPAddress.IPv6Any : IPAddress.Any;
            connectionUDP = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            for (; port < 65536; ++port)
            {
                try
                {
                    connectionUDP.Bind(new IPEndPoint(ip, port));
                    break;
                }
                catch (Exception)
                {
                    //
                }
            }
            port = ((IPEndPoint)connectionUDP.LocalEndPoint).Port;
            if (!ipv6)
            {
                byte[] response = { 5, 0, 0, 1,
                                0, 0, 0, 0,
                                (byte)(port / 0x100), (byte)(port % 0x100) };
                byte[] ip_bytes = ((IPEndPoint)connection.LocalEndPoint).Address.GetAddressBytes();
                Array.Copy(ip_bytes, 0, response, 4, 4);
                connection.BeginSend(response, 0, response.Length, 0, new AsyncCallback(StartConnect), null);
            }
            else
            {
                byte[] response = { 5, 0, 0, 4,
                                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                                (byte)(port / 0x100), (byte)(port % 0x100) };
                byte[] ip_bytes = ((IPEndPoint)connection.LocalEndPoint).Address.GetAddressBytes();
                Array.Copy(ip_bytes, 0, response, 4, 16);
                connection.BeginSend(response, 0, response.Length, 0, new AsyncCallback(StartConnect), null);
            }
        }

        private void RspSocks5TCPHeader()
        {
            if (connection.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] response = { 5, 0, 0, 1,
                                0, 0, 0, 0,
                                0, 0 };
                connection.BeginSend(response, 0, response.Length, 0, new AsyncCallback(StartConnect), null);
            }
            else
            {
                byte[] response = { 5, 0, 0, 4,
                                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                                0, 0 };
                connection.BeginSend(response, 0, response.Length, 0, new AsyncCallback(StartConnect), null);
            }
        }

        private void HandshakeReceive2Callback(IAsyncResult ar)
        {
            if (closed)
            {
                return;
            }
            try
            {
                int bytesRead = connection.EndReceive(ar);

                if (bytesRead >= 3)
                {
                    command = connetionRecvBuffer[1];
                    if (bytesRead > 3)
                    {
                        remoteHeaderSendBuffer = new byte[bytesRead - 3];
                        Array.Copy(connetionRecvBuffer, 3, remoteHeaderSendBuffer, 0, remoteHeaderSendBuffer.Length);
                    }
                    else
                    {
                        remoteHeaderSendBuffer = null;
                    }

                    if (command == 3) // UDP
                    {
                        connection.BeginReceive(connetionRecvBuffer, 0, 1024, 0,
                            new AsyncCallback(HandshakeReceive3Callback), null);
                    }
                    else
                    {
                        RspSocks5TCPHeader();
                        if (socks5RemotePort > 0)
                        {
                            if (server.tcp_over_udp)
                            {
                                command = 3;
                            }
                        }
                    }
                }
                else
                {
                    Console.WriteLine("failed to recv data in handshakeReceive2Callback");
                    this.Close();
                }
            }
            catch (Exception e)
            {
                LogSocketException(e);
                if (!Logging.LogSocketException(server.remarks, server.server, e))
                    Logging.LogUsefulException(e);
                this.Close();
            }
        }
        private void HandshakeReceive3Callback(IAsyncResult ar)
        {
            if (closed)
            {
                return;
            }
            try
            {
                int bytesRead = connection.EndReceive(ar);

                if (bytesRead >= 6)
                {
                    remoteHeaderSendBuffer = new byte[bytesRead];
                    Array.Copy(connetionRecvBuffer, 0, remoteHeaderSendBuffer, 0, remoteHeaderSendBuffer.Length);

                    RspSocks5UDPHeader(bytesRead + 3);
                    if (socks5RemotePort > 0)
                    {
                        if (server.udp_over_tcp)
                        {
                            command = 1;
                        }
                    }
                }
                else
                {
                    Console.WriteLine("failed to recv data in handshakeReceive3Callback");
                    this.Close();
                }
            }
            catch (Exception e)
            {
                LogSocketException(e);
                if (!Logging.LogSocketException(server.remarks, server.server, e))
                    Logging.LogUsefulException(e);
                this.Close();
            }
        }
        private void Connect()
        {
            lock (server)
            {
                server.ServerSpeedLog().AddConnectTimes();
                if (this.State == ConnectState.HANDSHAKE)
                {
                    this.State = ConnectState.CONNECTING;
                }
                server.GetConnections().AddRef(this.connection);
                encryptor = EncryptorFactory.GetEncryptor(server.method, server.password);
                encryptorUDP = EncryptorFactory.GetEncryptor(server.method, server.password);
            }
            closed = false;
            {
                IPAddress ipAddress;
                string serverURI = server.server;
                int serverPort = server.server_port;
                if (socks5RemotePort > 0)
                {
                    serverURI = socks5RemoteHost;
                    serverPort = socks5RemotePort;
                }
                bool parsed = IPAddress.TryParse(serverURI, out ipAddress);
                if (!parsed)
                {
                    //IPHostEntry ipHostInfo = Dns.GetHostEntry(serverURI);
                    //ipAddress = ipHostInfo.AddressList[0];
                    if (server.DnsBuffer().isExpired(serverURI))
                    {
                        Dns.BeginGetHostEntry(serverURI, new AsyncCallback(DnsCallback), null);
                        return;
                    }
                    else
                    {
                        ipAddress = server.DnsBuffer().ip;
                    }
                }
                //else
                BeginConnect(ipAddress, serverPort);
            }
        }
        private void StartConnect(IAsyncResult ar)
        {
            try
            {
                connection.EndSend(ar);
                Connect();
            }
            catch (Exception e)
            {
                LogSocketException(e);
                if (!Logging.LogSocketException(server.remarks, server.server, e))
                    Logging.LogUsefulException(e);
                this.Close();
            }

        }

        private void ConnectCallback(IAsyncResult ar)
        {
            if (closed)
            {
                return;
            }
            try
            {
                // Complete the connection.
                {
                    remote.EndConnect(ar);
                }
                if (socks5RemotePort > 0)
                {
                    if (ConnectProxyServer(server.server, server.server_port, remote, (int)SocketError.ConnectionReset))
                    {
                    }
                    else
                    {
                        throw new SocketException((int)SocketError.ConnectionReset);
                    }
                }
                speedTester.EndConnect();
                server.ServerSpeedLog().AddConnectTime((int)(speedTester.timeConnectEnd - speedTester.timeConnectBegin).TotalMilliseconds);

                //Console.WriteLine("Socket connected to {0}",
                //    remote.RemoteEndPoint.ToString());

                ConnectState _state = this.State;
                if (_state == ConnectState.CONNECTING)
                {
                    this.State = ConnectState.CONNECTED;
                    StartPipe();
                }
                else if (_state == ConnectState.CONNECTED)
                {
                    //ERROR
                }
            }
            catch (Exception e)
            {
                LogSocketException(e);
                if (!Logging.LogSocketException(server.remarks, server.server, e))
                    Logging.LogUsefulException(e);
                this.Close();
            }
        }

        // do/end xxx tcp/udp Recv
        private void doConnectionTCPRecv()
        {
            if (connection != null && connectionTCPIdle)
            {
                connectionTCPIdle = false;
                connection.BeginReceive(connetionRecvBuffer, 0, RecvSize, 0,
                    new AsyncCallback(PipeConnectionReceiveCallback), null);
            }
        }

        private int endConnectionTCPRecv(IAsyncResult ar)
        {
            if (connection != null)
            {
                int bytesRead = connection.EndReceive(ar);
                connectionTCPIdle = true;
                return bytesRead;
            }
            return 0;
        }

        private void doConnectionUDPRecv()
        {
            if (connectionUDP != null && connectionUDPIdle)
            {
                IPEndPoint sender = new IPEndPoint(connectionUDP.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                EndPoint tempEP = (EndPoint)sender;
                connectionUDPIdle = false;
                connectionUDP.BeginReceiveFrom(connetionRecvBuffer, 0, RecvSize, SocketFlags.None, ref tempEP,
                    new AsyncCallback(PipeConnectionUDPReceiveCallback), null);
            }
        }

        private int endConnectionUDPRecv(IAsyncResult ar, ref EndPoint endPoint)
        {
            if (connectionUDP != null)
            {
                int bytesRead = connectionUDP.EndReceiveFrom(ar, ref endPoint);
                if (connectionUDPEndPoint == null)
                    connectionUDPEndPoint = (IPEndPoint)endPoint;
                connectionUDPIdle = true;
                return bytesRead;
            }
            return 0;
        }

        private void doRemoteTCPRecv()
        {
            if (remote != null && remoteTCPIdle)
            {
                remoteTCPIdle = false;
                remote.BeginReceive(remoteRecvBuffer, 0, RecvSize, 0,
                    new AsyncCallback(PipeRemoteReceiveCallback), null);
            }
        }

        private int endRemoteTCPRecv(IAsyncResult ar)
        {
            if (remote != null)
            {
                int bytesRead = remote.EndReceive(ar);
                remoteTCPIdle = true;
                return bytesRead;
            }
            return 0;
        }

        private void doRemoteUDPRecv()
        {
            if (remoteUDP != null && remoteUDPIdle)
            {
                IPEndPoint sender = new IPEndPoint(remoteUDP.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                EndPoint tempEP = (EndPoint)sender;
                remoteUDPIdle = false;
                remoteUDP.BeginReceiveFrom(remoteRecvBuffer, 0, RecvSize, SocketFlags.None, ref tempEP,
                    new AsyncCallback(PipeRemoteUDPReceiveCallback), null);
            }
        }

        private int endRemoteUDPRecv(IAsyncResult ar, ref EndPoint endPoint)
        {
            if (remoteUDP != null)
            {
                int bytesRead = remoteUDP.EndReceiveFrom(ar, ref endPoint);
                remoteUDPIdle = true;
                return bytesRead;
            }
            return 0;
        }

        // 2 sides connection start
        private void StartPipe()
        {
            if (closed)
            {
                return;
            }
            try
            {
                // set mark
                connectionTCPIdle = true;
                connectionUDPIdle = true;
                remoteTCPIdle = true;
                remoteUDPIdle = true;

                connectionPacketNumber = 0;
                remoteUDPRecvBufferLength = 0;

                ResetTimeout(TTL);

                // remote ready
                //if (remoteHeaderSendBuffer != null)
                {
                    if (connectionUDP == null) // TCP
                    {
                    }
                    else // UDP
                    {
                        if (
                            !server.udp_over_tcp &&
                            remoteUDP != null)
                        {
                            //doConnectionTCPRecv();
                            //doConnectionUDPRecv();
                            if (socks5RemotePort == 0)
                                CloseSocket(ref remote);
                            remoteHeaderSendBuffer = null;
                        }
                        else
                        {
                            if (remoteHeaderSendBuffer != null)
                            {
                                RemoteSend(remoteHeaderSendBuffer, remoteHeaderSendBuffer.Length, server.obfs_tcp);
                                remoteHeaderSendBuffer = null;
                            }
                        }
                    }
                    //remoteHeaderSendBuffer = null;
                }

                // remote recv first
                doRemoteTCPRecv();
                doRemoteUDPRecv();

                // connection recv last
                doConnectionTCPRecv();
                doConnectionUDPRecv();
            }
            catch (Exception e)
            {
                LogSocketException(e);
                if (!Logging.LogSocketException(server.remarks, server.server, e))
                    Logging.LogUsefulException(e);
                this.Close();
            }
        }

        private void ConnectionSend(byte[] buffer, int bytesToSend)
        {
            if (connectionUDP == null)
                connection.BeginSend(buffer, 0, bytesToSend, 0, new AsyncCallback(PipeConnectionSendCallback), null);
            else
                connectionUDP.BeginSendTo(buffer, 0, bytesToSend, SocketFlags.None, connectionUDPEndPoint, new AsyncCallback(PipeConnectionUDPSendCallback), null);
        }
        // end ReceiveCallback
        private void PipeRemoteReceiveCallback(IAsyncResult ar)
        {
            if (closed)
            {
                return;
            }
            try
            {
                int bytesRead = endRemoteTCPRecv(ar);
                ResetTimeout(TTL);

                if (bytesRead > 0)
                {
                    int bytesToSend;
                    byte[] remoteSendBuffer = new byte[RecvSize];
                    lock (decryptionLock)
                    {
                        if (closed)
                        {
                            return;
                        }
                        encryptor.Decrypt(remoteRecvBuffer, bytesRead, remoteSendBuffer, out bytesToSend);
                    }
                    if (connectionUDP == null)
                        Logging.LogBin(LogLevel.Debug, "remote recv", remoteSendBuffer, bytesToSend);
                    else
                        Logging.LogBin(LogLevel.Debug, "udp remote recv", remoteSendBuffer, bytesToSend);

                    server.ServerSpeedLog().AddDownloadBytes(bytesToSend);
                    server.ServerSpeedLog().HasData();
                    speedTester.AddDownloadSize(bytesToSend);

                    if (connectionUDP == null)
                    {
                        connection.BeginSend(remoteSendBuffer, 0, bytesToSend, 0, new AsyncCallback(PipeConnectionSendCallback), null);
                    }
                    else
                    {
                        List<byte[]> buffer_list = new List<byte[]>();
                        lock (recvUDPoverTCPLock)
                        {
                            Array.Copy(remoteSendBuffer, 0, remoteUDPRecvBuffer, remoteUDPRecvBufferLength, bytesToSend);
                            remoteUDPRecvBufferLength += bytesToSend;
                            while (remoteUDPRecvBufferLength > 6)
                            {
                                int len = ((int)remoteUDPRecvBuffer[0] << 8) + remoteUDPRecvBuffer[1];
                                if (len > remoteUDPRecvBufferLength)
                                    break;
                                byte[] buffer = new byte[len];
                                Array.Copy(remoteUDPRecvBuffer, buffer, len);
                                remoteUDPRecvBufferLength -= len;
                                Array.Copy(remoteUDPRecvBuffer, len, remoteUDPRecvBuffer, 0, remoteUDPRecvBufferLength);

                                buffer[0] = 0;
                                buffer[1] = 0;
                                buffer_list.Add(buffer);
                            }
                        }
                        if (buffer_list.Count == 0)
                        {
                            doRemoteTCPRecv();
                        }
                        else
                        {
                            foreach (byte[] buffer in buffer_list)
                            {
                                connectionUDP.BeginSendTo(buffer, 0, buffer.Length, SocketFlags.None, connectionUDPEndPoint, new AsyncCallback(PipeConnectionUDPSendCallback), null);
                                System.Diagnostics.Debug.Write("Receive " + buffer.Length + "\r\n");
                            }
                        }
                    }
                }
                else
                {
                    //Console.WriteLine("bytesRead: " + bytesRead.ToString());
                    connection.Shutdown(SocketShutdown.Send);
                    connectionShutdown = true;
                    if (lastErrCode == 0)
                    {
                        lastErrCode = 8;
                        if (speedTester.sizeDownload == 0)
                        {
                            server.ServerSpeedLog().AddNoDataTimes();
                            if (server.ServerSpeedLog().ErrorContinurousTimes >= AutoSwitchOffErrorTimes && autoSwitchOff)
                            {
                                server.setEnable(false);
                            }
                        }
                    }
                    CheckClose();
                }
            }
            catch (Exception e)
            {
                LogSocketException(e);
                if (!Logging.LogSocketException(server.remarks, server.server, e))
                    Logging.LogUsefulException(e);
                this.Close();
            }
        }

        private bool RemoveRemoteUDPRecvBufferHeader(ref int bytesRead)
        {
            if (socks5RemotePort > 0)
            {
                if (bytesRead < 7)
                {
                    return false;
                }
                int port = -1;
                if (remoteRecvBuffer[3] == 1)
                {
                    int head = 3 + 1 + 4 + 2;
                    bytesRead = bytesRead - head;
                    port = remoteRecvBuffer[head - 2] * 0x100 + remoteRecvBuffer[head - 1];
                    Array.Copy(remoteRecvBuffer, head, remoteRecvBuffer, 0, bytesRead);
                }
                else if (remoteRecvBuffer[3] == 4)
                {
                    int head = 3 + 1 + 16 + 2;
                    bytesRead = bytesRead - head;
                    port = remoteRecvBuffer[head - 2] * 0x100 + remoteRecvBuffer[head - 1];
                    Array.Copy(remoteRecvBuffer, head, remoteRecvBuffer, 0, bytesRead);
                }
                else if (remoteRecvBuffer[3] == 3)
                {
                    int head = 3 + 1 + 1 + remoteRecvBuffer[4] + 2;
                    bytesRead = bytesRead - head;
                    port = remoteRecvBuffer[head - 2] * 0x100 + remoteRecvBuffer[head - 1];
                    Array.Copy(remoteRecvBuffer, head, remoteRecvBuffer, 0, bytesRead);
                }
                else
                {
                    return false;
                }
                if (port != server.server_port)
                {
                    return false;
                }
            }
            return true;
        }

        private void AddRemoteUDPRecvBufferHeader(byte[] decryptBuffer, ref int bytesToSend)
        {
            Array.Copy(decryptBuffer, 0, remoteSendBuffer, 3, bytesToSend);
            remoteSendBuffer[0] = 0;
            remoteSendBuffer[1] = 0;
            remoteSendBuffer[2] = 0;
            bytesToSend += 3;
        }

        public static byte[] ParseUDPHeader(byte[] buffer, ref int len)
        {
            if (buffer.Length == 0)
                return buffer;
            if (buffer[0] == 0x81)
            {
                len = len - 1;
                byte[] ret = new byte[len];
                Array.Copy(buffer, 1, ret, 0, len);
                return ret;
            }
            if (buffer[0] == 0x80 && len >= 2)
            {
                int ofbs_len = buffer[1];
                if (ofbs_len + 2 < len)
                {
                    len = len - ofbs_len - 2;
                    byte[] ret = new byte[len];
                    Array.Copy(buffer, ofbs_len + 2, ret, 0, len);
                    return ret;
                }
            }
            if (buffer[0] == 0x82 && len >= 3)
            {
                int ofbs_len = (buffer[1] << 8) + buffer[2];
                if (ofbs_len + 3 < len)
                {
                    len = len - ofbs_len - 3;
                    byte[] ret = new byte[len];
                    Array.Copy(buffer, ofbs_len + 3, ret, 0, len);
                    return ret;
                }
            }
            if (len < buffer.Length)
            {
                byte[] ret = new byte[len];
                Array.Copy(buffer, ret, len);
                return ret;
            }
            return buffer;
        }

        // end ReceiveCallback
        private void PipeRemoteUDPReceiveCallback(IAsyncResult ar)
        {
            if (closed)
            {
                return;
            }
            try
            {
                IPEndPoint sender = new IPEndPoint(remoteUDP.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                EndPoint tempEP = (EndPoint)sender;
                ResetTimeout(TTL);

                int bytesRead = endRemoteUDPRecv(ar, ref tempEP);

                if (bytesRead > 0)
                {
                    int bytesToSend;
                    if (!RemoveRemoteUDPRecvBufferHeader(ref bytesRead))
                    {
                        return; // drop
                    }
                    lock (decryptionLock)
                    {
                        byte[] decryptBuffer = new byte[RecvSize];
                        if (closed)
                        {
                            return;
                        }
                        encryptorUDP.Reset();
                        encryptorUDP.Decrypt(remoteRecvBuffer, bytesRead, decryptBuffer, out bytesToSend);
                        decryptBuffer = ParseUDPHeader(decryptBuffer, ref bytesToSend);
                        AddRemoteUDPRecvBufferHeader(decryptBuffer, ref bytesToSend);
                    }
                    if (connectionUDP == null)
                        Logging.LogBin(LogLevel.Debug, "remote recv", remoteSendBuffer, bytesToSend);
                    else
                        Logging.LogBin(LogLevel.Debug, "udp remote recv", remoteSendBuffer, bytesToSend);

                    server.ServerSpeedLog().AddDownloadBytes(bytesToSend);
                    server.ServerSpeedLog().HasData();
                    speedTester.AddDownloadSize(bytesToSend);

                    if (connectionUDP == null)
                        connection.BeginSend(remoteSendBuffer, 0, bytesToSend, 0, new AsyncCallback(PipeConnectionSendCallback), null);
                    else
                        connectionUDP.BeginSendTo(remoteSendBuffer, 0, bytesToSend, SocketFlags.None, connectionUDPEndPoint, new AsyncCallback(PipeConnectionUDPSendCallback), null);
                }
                else
                {
                    //Console.WriteLine("bytesRead: " + bytesRead.ToString());
                    connection.Shutdown(SocketShutdown.Send);
                    connectionShutdown = true;
                    if (lastErrCode == 0)
                    {
                        lastErrCode = 8;
                        if (speedTester.sizeDownload == 0)
                        {
                            server.ServerSpeedLog().AddNoDataTimes();
                            if (server.ServerSpeedLog().ErrorContinurousTimes >= AutoSwitchOffErrorTimes && autoSwitchOff)
                            {
                                server.setEnable(false);
                            }
                        }
                    }
                    CheckClose();
                }
            }
            catch (Exception e)
            {
                LogSocketException(e);
                if (!Logging.LogSocketException(server.remarks, server.server, e))
                    Logging.LogUsefulException(e);
                this.Close();
            }
        }

        private void RemoteSend(byte[] bytes, int length, bool obfs = false, int obfs_max = 255)
        {
            int bytesToSend;
            if (obfs)
            {
                byte[] bytesToEncrypt = null;
                int obfs_len = random.Next(obfs_max) + 1;
                if (obfs_len == 1)
                {
                    bytesToEncrypt = new byte[length + 1];
                    Array.Copy(bytes, 0, bytesToEncrypt, 1, length);
                    bytesToEncrypt[0] = 0x81;
                    length += 1;
                }
                else
                {
                    int len = obfs_len - 2;
                    bytesToEncrypt = new byte[length + len + 2];
                    Array.Copy(bytes, 0, bytesToEncrypt, len + 2, length);
                    bytesToEncrypt[0] = 0x80;
                    bytesToEncrypt[1] = (byte)len;
                    length += len + 2;
                }
                Logging.LogBin(LogLevel.Debug, "remote send", bytesToEncrypt, length);
                lock (encryptionLock)
                {
                    if (closed)
                    {
                        return;
                    }
                    encryptor.Encrypt(bytesToEncrypt, length, connetionSendBuffer, out bytesToSend);
                }
            }
            else
            {
                Logging.LogBin(LogLevel.Debug, "remote send", bytes, length);
                lock (encryptionLock)
                {
                    if (closed)
                    {
                        return;
                    }
                    encryptor.Encrypt(bytes, length, connetionSendBuffer, out bytesToSend);
                }
            }
            server.ServerSpeedLog().AddUploadBytes(bytesToSend);
            speedTester.AddUploadSize(bytesToSend);
            remote.BeginSend(connetionSendBuffer, 0, bytesToSend, 0, new AsyncCallback(PipeRemoteSendCallback), null);
        }

        private void RemoteSendto(byte[] bytes, int length, bool obfs, int obfs_max = 40)
        {
            int bytesToSend;
            byte[] bytesToEncrypt = null;
            int bytes_beg = 3;
            length -= bytes_beg;
            if (socks5RemotePort > 0) //ignore obfs, TODO: need test
            {
                bytesToEncrypt = new byte[length];
                Array.Copy(bytes, bytes_beg, bytesToEncrypt, 0, length);
                lock (encryptionLock)
                {
                    if (closed)
                    {
                        return;
                    }
                    encryptorUDP.Reset();
                    encryptorUDP.Encrypt(bytesToEncrypt, length, connetionSendBuffer, out bytesToSend);
                }

                IPAddress ipAddress;
                string serverURI = server.server;
                int serverPort = server.server_port;
                bool parsed = IPAddress.TryParse(serverURI, out ipAddress);
                if (!parsed)
                {
                    bytesToEncrypt = new byte[bytes_beg + 1 + 1 + serverURI.Length + 2 + bytesToSend];
                    Array.Copy(connetionSendBuffer, 0, bytesToEncrypt, bytes_beg + 1 + 1 + serverURI.Length + 2, bytesToSend);
                    bytesToEncrypt[0] = 0;
                    bytesToEncrypt[1] = 0;
                    bytesToEncrypt[2] = 0;
                    bytesToEncrypt[3] = (byte)3;
                    bytesToEncrypt[4] = (byte)serverURI.Length;
                    for (int i = 0; i < serverURI.Length; ++i)
                    {
                        bytesToEncrypt[5 + i] = (byte)serverURI[i];
                    }
                    bytesToEncrypt[5 + serverURI.Length] = (byte)(serverPort / 0x100);
                    bytesToEncrypt[5 + serverURI.Length + 1] = (byte)(serverPort % 0x100);
                }
                else
                {
                    byte[] addBytes = ipAddress.GetAddressBytes();
                    bytesToEncrypt = new byte[bytes_beg + 1 + addBytes.Length + 2 + bytesToSend];
                    Array.Copy(connetionSendBuffer, 0, bytesToEncrypt, bytes_beg + 1 + addBytes.Length + 2, bytesToSend);
                    bytesToEncrypt[0] = 0;
                    bytesToEncrypt[1] = 0;
                    bytesToEncrypt[2] = 0;
                    bytesToEncrypt[3] = ipAddress.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)4 : (byte)1;
                    for (int i = 0; i < addBytes.Length; ++i)
                    {
                        bytesToEncrypt[4 + i] = addBytes[i];
                    }
                    bytesToEncrypt[4 + addBytes.Length] = (byte)(serverPort / 0x100);
                    bytesToEncrypt[4 + addBytes.Length + 1] = (byte)(serverPort % 0x100);
                }

                bytesToSend = bytesToEncrypt.Length;
                Array.Copy(bytesToEncrypt, connetionSendBuffer, bytesToSend);
            }
            else
            {
                if (obfs)
                {
                    int obfs_len = random.Next(obfs_max + 1);
                    if (length < 24 + 16 && obfs_len > 0) // DNS: 17 + URI len + 1 + port(2) + ip(4 or 16)
                    {
                        if (obfs_len == 1)
                        {
                            bytesToEncrypt = new byte[length + 1];
                            Array.Copy(bytes, bytes_beg, bytesToEncrypt, 1, length);
                            bytesToEncrypt[0] = 0x81;
                            length += 1;
                        }
                        else
                        {
                            int len = obfs_len - 2;
                            bytesToEncrypt = new byte[length + len + 2];
                            Array.Copy(bytes, bytes_beg, bytesToEncrypt, len + 2, length);
                            bytesToEncrypt[0] = 0x80;
                            bytesToEncrypt[1] = (byte)len;
                            length += len + 2;
                        }
                    }
                    else
                    {
                        bytesToEncrypt = new byte[length];
                        Array.Copy(bytes, bytes_beg, bytesToEncrypt, 0, length);
                    }
                }
                else
                {
                    bytesToEncrypt = new byte[length];
                    Array.Copy(bytes, 3, bytesToEncrypt, 0, length);
                }
                Logging.LogBin(LogLevel.Debug, "remote sendto", bytesToEncrypt, length);
                lock (encryptionLock)
                {
                    if (closed)
                    {
                        return;
                    }
                    encryptorUDP.Reset();
                    encryptorUDP.Encrypt(bytesToEncrypt, length, connetionSendBuffer, out bytesToSend);
                }
            }
            server.ServerSpeedLog().AddUploadBytes(bytesToSend);
            speedTester.AddUploadSize(bytesToSend);
            remoteUDP.BeginSendTo(connetionSendBuffer, 0, bytesToSend, 0, remoteUDPEndPoint, new AsyncCallback(PipeRemoteUDPSendCallback), null);
        }

        private void PipeConnectionReceiveCallback(IAsyncResult ar)
        {
            if (closed)
            {
                return;
            }
            try
            {
                int bytesRead = endConnectionTCPRecv(ar);
                ResetTimeout(TTL);

                if (bytesRead > 0)
                {
                    if (remoteHeaderSendBuffer != null)
                    {
                        Array.Copy(connetionRecvBuffer, 0, connetionRecvBuffer, remoteHeaderSendBuffer.Length, bytesRead);
                        Array.Copy(remoteHeaderSendBuffer, 0, connetionRecvBuffer, 0, remoteHeaderSendBuffer.Length);
                        bytesRead += remoteHeaderSendBuffer.Length;
                        remoteHeaderSendBuffer = null;
                    }
                    else
                    {
                        Logging.LogBin(LogLevel.Debug, "remote send", connetionRecvBuffer, bytesRead);
                    }
                    {
                        {
                            RemoteSend(connetionRecvBuffer, bytesRead);
                        }
                    }
                }
                else
                {
                    {
                        remote.Shutdown(SocketShutdown.Send);
                    }
                    remoteShutdown = true;
                    CheckClose();
                }
            }
            catch (Exception e)
            {
                if (!Logging.LogSocketException(server.remarks, server.server, e))
                    Logging.LogUsefulException(e);
                this.Close();
            }
        }

        private void PipeConnectionUDPReceiveCallback(IAsyncResult ar)
        {
            if (closed)
            {
                return;
            }
            try
            {
                IPEndPoint sender = new IPEndPoint(connectionUDP.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                EndPoint tempEP = (EndPoint)sender;
                ResetTimeout(TTL);

                int bytesRead = endConnectionUDPRecv(ar, ref tempEP);

                if (bytesRead > 0)
                {
                    byte[] connetionSendBuffer = new byte[bytesRead];
                    //lock (encryptionLock)
                    {
                        Array.Copy(connetionRecvBuffer, connetionSendBuffer, bytesRead);
                    }
                    Logging.LogBin(LogLevel.Debug, "udp remote send", connetionRecvBuffer, bytesRead);
                    if (!server.udp_over_tcp && remoteUDP != null)
                    {
                        RemoteSendto(connetionSendBuffer, bytesRead, server.obfs_udp);
                    }
                    else
                    {
                        //if (remoteHeaderSendBuffer != null)
                        //{
                        //    Array.Copy(connetionRecvBuffer, 0, connetionRecvBuffer, remoteHeaderSendBuffer.Length, bytesRead);
                        //    Array.Copy(remoteHeaderSendBuffer, 0, connetionRecvBuffer, 0, remoteHeaderSendBuffer.Length);
                        //    bytesRead += remoteHeaderSendBuffer.Length;
                        //    remoteHeaderSendBuffer = null;
                        //}
                        if (connetionSendBuffer[0] == 0 && connetionSendBuffer[1] == 0)
                        {
                            connetionSendBuffer[0] = (byte)(bytesRead >> 8);
                            connetionSendBuffer[1] = (byte)(bytesRead);
                            RemoteSend(connetionSendBuffer, bytesRead);
                            //System.Diagnostics.Debug.Write("Send " + bytesRead + "\r\n");
                        }
                    }
                }
                else
                {
                    remote.Shutdown(SocketShutdown.Send);
                    remoteShutdown = true;
                    CheckClose();
                }
            }
            catch (Exception e)
            {
                if (!Logging.LogSocketException(server.remarks, server.server, e))
                    Logging.LogUsefulException(e);
                this.Close();
            }
        }

        // end SendCallback
        private void PipeRemoteSendCallback(IAsyncResult ar)
        {
            if (closed)
            {
                return;
            }
            try
            {
                remote.EndSend(ar);
                doConnectionTCPRecv();
                doConnectionUDPRecv();
            }
            catch (Exception e)
            {
                LogSocketException(e);
                if (!Logging.LogSocketException(server.remarks, server.server, e))
                    Logging.LogUsefulException(e);
                this.Close();
            }
        }

        private void PipeRemoteUDPSendCallback(IAsyncResult ar)
        {
            if (closed)
            {
                return;
            }
            try
            {
                remoteUDP.EndSendTo(ar);
                doConnectionTCPRecv();
                doConnectionUDPRecv();
            }
            catch (Exception e)
            {
                LogSocketException(e);
                if (!Logging.LogSocketException(server.remarks, server.server, e))
                    Logging.LogUsefulException(e);
                this.Close();
            }
        }

        private void PipeConnectionSendCallback(IAsyncResult ar)
        {
            if (closed)
            {
                return;
            }
            try
            {
                connection.EndSend(ar);
                doRemoteTCPRecv();
                doRemoteUDPRecv();
            }
            catch (Exception e)
            {
                LogSocketException(e);
                if (!Logging.LogSocketException(server.remarks, server.server, e))
                    Logging.LogUsefulException(e);
                this.Close();
            }
        }

        private void PipeConnectionUDPSendCallback(IAsyncResult ar)
        {
            if (closed)
            {
                return;
            }
            try
            {
                connectionUDP.EndSendTo(ar);
                doRemoteTCPRecv();
                doRemoteUDPRecv();
            }
            catch (Exception e)
            {
                LogSocketException(e);
                if (!Logging.LogSocketException(server.remarks, server.server, e))
                    Logging.LogUsefulException(e);
                this.Close();
            }
        }
    }

}
