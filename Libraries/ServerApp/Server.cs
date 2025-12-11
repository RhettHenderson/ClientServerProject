using Common;
using Syroot.Windows.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Client_Server;

public class Server : IAsyncDisposable {
    // === Networking ===
    private Socket listener;
    private static string Name = "Server";

    // === Connection Handling ===
    private sealed class Conn {
        public Socket socket { get; }
        public Stream io { get; }

        public Conn(Socket s, Stream i) {
            socket = s;
            io = i;
        }
    }

    // === Active Clients & State ===
    private static readonly ConcurrentDictionary<int, Conn> clients = new();               // ID -> connection
    private static readonly ConcurrentDictionary<string, int> names = new();               // username -> ID
    private static readonly ConcurrentDictionary<int, (float, float, float)> positions = new(); // ID -> position
    private static readonly ConcurrentDictionary<string, FileReceiveState> files = new();  // fileKey -> state
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<Packet>> pendingResponses = new(); // expectedType -> TaskCompletionSource

    // === Authentication ===
    private static ConcurrentDictionary<string, string> passwords = new();        // username -> password hash
    private static int currentAuthCode = 111111;
    private static string passwordsFile = "passwords.txt";

    // === File I/O ===
    private static Stream _stream = null;
    private string defaultSaveDir = "";

    // === Commands & Misc ===
    private static int nextID = 0;
    private static readonly string[] commands = { "help", "whisper", "w", "disconnect", "dc" };
    private static readonly byte[] cmdJson = JsonSerializer.SerializeToUtf8Bytes(commands, CommonJsonContext.Default.StringArray);
    public IPAddress? listeningIp { get; private set; }
    public int listeningPort { get; private set; }
    private Socket? udp;
    private readonly object udpLock = new();
    private int? pendingVoiceClientId;
    private CancellationTokenSource? voiceInviteCts;
    private readonly ConcurrentDictionary<int, IPEndPoint> udpClients = new();
    private readonly ConcurrentDictionary<int, string> clientPlatforms = new();
    private readonly ConcurrentDictionary<int, byte> voiceParticipants = new();
    private readonly string serverPlatform = Utility.GetPlatformName();

    // === Events and Actions ===
    public event Action<string, string>? MessageReceived;   // (from, text)
    public event Action<string, string>? WhisperReceived;   // (from, text)
    public event Action<string[]>? CommandsReceived;
    public event Action<int>? IdAssigned;
    public event Action? Disconnected;
    public event Action<string>? Error;
    public event Action<NotificationType, string>? Notification;

    // === Audio Playback ===
    private static Pcm16Player? _player;
    private MicRecorder? _mic;
    private Thread? micSenderThread;
    private bool disableServerMic;

    //--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

    // === Main Server Loop ===
    public async Task ExecuteServerAsync(int port) {
        string ip = Utility.GetLocalIP();
        listeningPort = port;
        Console.Title = "Server";
        try {
            defaultSaveDir = KnownFolders.Downloads.Path;
        }
        catch (Exception e) {
            defaultSaveDir = "/downloads";
        }
        await InitListener(ip);

        //var acceptTask = AcceptLoopAsync();
        //var consoleTask = Task.Run(() => RunServerConsoleAsync());

        //await Task.WhenAny(acceptTask, consoleTask);
        await AcceptLoopAsync();

        //try { await Task.WhenAll(acceptTask, consoleTask); } catch { }
    }
    public async Task InitListener(string ip) {
        await ReadPasswords(passwordsFile);
        IPAddress ipAddr;
        if (ip == "") {
            ipAddr = IPAddress.Loopback; //127.0.0.1
        }
        else {
            try {
                ipAddr = IPAddress.Parse(ip);
            }
            catch (FormatException) {
                Error?.Invoke("Invalid IP address format.");
                throw;
            }
        }
        listeningIp = ipAddr;
        IPEndPoint localEndPoint = new IPEndPoint(ipAddr, listeningPort);
        //Create TCP Socket
        listener = new Socket(ipAddr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(localEndPoint);
        listener.Listen(10);
    }


    public async Task AcceptLoopAsync() {
        try {
            while (true) {
                int id = await WaitForConnectionAsync();
                _ = HandleClientAsync(id);
            }
        }
        catch (OperationCanceledException) {
            Error?.Invoke("Server is shutting down.");
        }
        finally {
            try { listener.Close(); } catch { }
        }
    }

    private void StartUdpListenerIfNeeded() {
        if (udp != null || listeningIp is null) {
            return;
        }

        udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(listeningIp, listeningPort));
        _ = Task.Run(() => UdpReceiveLoopAsync(udp));
    }

    public async Task UdpReceiveLoopAsync(Socket udp) {
        MessageReceived?.Invoke("UDP Listener", "Started UDP receive loop.");
        var buf = new byte[4096];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (true) {
            try {
                var result = await udp.ReceiveFromAsync(buf, SocketFlags.None, remote);
                Packet packet = PacketIO.DeserializeForUdp(buf.AsSpan(0, result.ReceivedBytes));
                var from = (IPEndPoint)result.RemoteEndPoint;

                var senderId = ResolveClientId(packet.ClientID);
                if (senderId >= 0) {
                    udpClients[senderId] = from;
                }

                if (!packet.Headers.TryGetValue("Type", out var type) || type != "Audio") {
                    Notification?.Invoke(NotificationType.Info, $"Ignoring non-audio UDP packet from {from}");
                    continue;
                }

                if (senderId >= 0 && voiceParticipants.ContainsKey(senderId)) {
                    await ForwardAudioAsync(udp, senderId, packet);
                }

                if (voiceParticipants.ContainsKey(0) && serverPlatform == "Windows") {
                    EnsureServerPlayer();
                    _player?.AddFrame(packet.Payload, 0, packet.Payload.Length);
                }
            }
            catch (ObjectDisposedException) {
                Notification?.Invoke(NotificationType.Info, "UDP listener stopped.");
                break;
            }
            catch (SocketException) {
                Notification?.Invoke(NotificationType.Info, "UDP listener stopped due to socket closure.");
                break;
            }
        }
    }

    public async Task UdpSendAsync(Socket udp, string ip, int port, string message) {
        IPAddress ipAddr = IPAddress.Parse(ip);
        IPEndPoint remoteEndPoint = new IPEndPoint(ipAddr, port);
        var buf = Encoding.UTF8.GetBytes(message);
        await udp.SendToAsync(buf, SocketFlags.None, remoteEndPoint);
    }

    private int ResolveClientId(string clientId) {
        if (string.Equals(clientId, Name, StringComparison.OrdinalIgnoreCase)) {
            return 0;
        }

        if (names.TryGetValue(clientId, out var id)) {
            return id;
        }

        return -1;
    }

    private async Task ForwardAudioAsync(Socket udp, int senderId, Packet packet) {
        var recipients = udpClients
            .Where(kv => kv.Key != senderId && voiceParticipants.ContainsKey(kv.Key))
            .Select(kv => kv.Value)
            .ToArray();

        foreach (var endpoint in recipients) {
            await PacketIO.SendPacketToAsyncUdp(udp, packet, endpoint);
        }
    }

    private void StartServerMicrophone() {
        if (disableServerMic || udp is null || _mic != null || serverPlatform != "Windows") {
            return;
        }

        try {
            _mic = new MicRecorder(frameMs: 10);
            _mic.Start();
        }
        catch (Exception ex) {
            Notification?.Invoke(NotificationType.Warning, $"Unable to start server microphone: {ex.Message}");
            _mic = null;
            return;
        }

        micSenderThread = new Thread(() => {
            const int bytesPerSample = 2;
            const int samplesPer10ms = 480;
            const int maxFrameBytes = samplesPer10ms * bytesPerSample;

            uint seq = 0;
            int timestampSamples = 0;

            while (_mic != null && udp != null && !disableServerMic) {
                if (!_mic.TryDequeue(out var frame) || frame is null) {
                    Thread.Sleep(1);
                    continue;
                }

                int offset = 0;
                while (offset < frame.Length) {
                    int take = Math.Min(maxFrameBytes, frame.Length - offset);
                    var slice = new byte[take];
                    Buffer.BlockCopy(frame, offset, slice, 0, take);

                    var packet = new Packet {
                        ClientID = Name,
                        Headers = new Dictionary<string, string>
                        {
                            { "Type", "Audio" },
                            { "Protocol", "UDP" },
                            { "Seq", seq.ToString() },
                            { "Ts", timestampSamples.ToString() },
                            { "Fmt", "PCM16_48k_Mono" }
                        },
                        Payload = slice
                    };

                    var endpoints = udpClients
                        .Where(kv => voiceParticipants.ContainsKey(kv.Key))
                        .Select(kv => kv.Value)
                        .ToArray();

                    foreach (var endpoint in endpoints) {
                        PacketIO.SendPacketToAsyncUdp(udp, packet, endpoint);
                    }

                    seq++;
                    timestampSamples += take / bytesPerSample;
                    offset += take;
                }
            }
        }) { IsBackground = true, Name = "ServerMicSender" };

        micSenderThread.Start();
        Notification?.Invoke(NotificationType.Info, "Server microphone capture started.");
    }

    private void StopServerMicrophone() {
        disableServerMic = false;
        _mic?.Dispose();
        _mic = null;
        try { micSenderThread?.Join(200); } catch { }
        micSenderThread = null;
    }

    private void EnsureServerPlayer() {
        if (_player != null || serverPlatform != "Windows") {
            return;
        }

        _player = new Pcm16Player(latencyMs: 100, jitterMs: 600);
    }

    private void CloseUdpConnection() {
        Socket? socketToClose = null;
        lock (udpLock) {
            if (udp != null) {
                socketToClose = udp;
                udp = null;
            }
        }

        if (socketToClose != null) {
            StopServerMicrophone();
            try { socketToClose.Close(); } catch { }
            Notification?.Invoke(NotificationType.Info, "Closed local UDP connection.");
        }

        udpClients.Clear();
        voiceParticipants.Clear();
    }
    public async Task<int> WaitForConnectionAsync() {
        Notification?.Invoke(NotificationType.Info, "Waiting for connection...");
        Socket client = await listener.AcceptAsync();
        //Uses the Nagle algorithm (google for more info)
        client.NoDelay = true;

        var net = new NetworkStream(client, ownsSocket: true);
        Stream stream = net;

        try {
            var cert = LoadServerCertificate();
            var ssl = new SslStream(net, leaveInnerStreamOpen: false);

            await ssl.AuthenticateAsServerAsync(
                serverCertificate: cert,
                clientCertificateRequired: false,
                enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
                checkCertificateRevocation: true
            );

            stream = ssl;
            Notification?.Invoke(NotificationType.Info, "SSL certificate loaded successfully. Using encrypted connection.");
        }
        catch (InvalidOperationException) {
            Notification?.Invoke(NotificationType.Warning,
                "WARNING: SSL certificate is not present. " +
                "Ignore this if you intend to use it unencrypted, otherwise refer to the README for instructions on setting up a dev certificate.");
            stream = net; // Fallback to non-SSL
        }
        catch (Exception e) {
            Notification?.Invoke(NotificationType.Error, $"Failed to establish SSL: {e.Message}. Falling back to unencrypted connection.");
            stream = net; // Fallback to non-SSL
        }

        _stream = stream;

        int id = Interlocked.Increment(ref nextID);
        clients[id] = new Conn(client, stream);
        Notification?.Invoke(NotificationType.Info, $"Client #{id} connected.");
        return id;
    }


    public async Task HandleClientAsync(int id) {
        //Sends the packet to tell the client what its id is
        while (true) {
            bool keepAlive = await ProcessPacketAsync(id);
            if (!keepAlive) break;
        }
    }

    public async Task<bool> ProcessPacketAsync(int id) {
        var conn = clients[id];
        PacketStatus status;
        Packet incoming = null;
        try {
            var (s, i) = await PacketIO.ReceivePacketAsync(conn.io);
            status = s;
            incoming = i;
        }
        catch {
            status = PacketStatus.Disconnected;
        }
        if (status == PacketStatus.Disconnected) {
            Notification?.Invoke(NotificationType.Warning, $"Client {id} forcibly disconnected");
            RemoveClient(id);
            return false;
        }
        else if (status == PacketStatus.Error) {
            Error?.Invoke("An error occured trying to receive the last packet. Closing connection.");
            RemoveClient(id);
            return false;
        }
        //if we reach here status is Ok
        var clientID = incoming.ClientID;
        var headers = incoming.Headers;
        var text = Encoding.UTF8.GetString(incoming.Payload);
        //Default values for the packet
        Packet reply = new Packet {
            ClientID = "Server",
            Headers = new Dictionary<string, string> { { "Type", "Message" } },
            Payload = Encoding.UTF8.GetBytes("")
        };

        //Step 1: Read headers to determine packet type
        //Types so far are "Message", "Command", "Ack", "Data"
        var type = headers["Type"];

        //Before actually processing the packet, check pendingResponses
        if (pendingResponses.TryRemove(type, out var tcs)) {
            tcs.SetResult(incoming);
            return true;
        }

        switch (type) {
            case ("Message"):
                Notification?.Invoke(NotificationType.Info, $"{clientID} sent chat {text}");
                await BroadcastAsync(incoming, id);
                return true;
            case ("Command"):
                switch (text.Split(" ")[0]) {
                    case "help":
                        StringBuilder sb = new StringBuilder("Available Commands: ");
                        foreach (string cmd in commands) {
                            sb.Append($"--{cmd} ");
                        }
                        reply.Payload = Encoding.ASCII.GetBytes(sb.ToString());
                        await PacketIO.SendPacketAsync(conn.io, reply);
                        return true;

                    case "whisper":
                    case "w":
                        string[] args = text.Split(" ");
                        if (args.Length < 3) {
                            reply.Payload = Encoding.ASCII.GetBytes("Usage: --whisper <ID> <message>");
                            await PacketIO.SendPacketAsync(conn.io, reply);
                            return true;
                        }
                        //Step 1: Check if the user used ID or name
                        //Step 2: If using ID, no changes made. If using name, look up ID
                        //Step 3: Check if ID exists
                        //Step 4: Send message if it does, error if it doesn't
                        if (!int.TryParse(args[1], out int targetID)) {
                            //User used a name instead of an ID
                            if (!names.ContainsKey(args[1])) {
                                reply.Payload = Encoding.ASCII.GetBytes($"User with name {args[1]} not found.");
                                await PacketIO.SendPacketAsync(conn.io, reply);
                                return true;
                            }
                            //Name exists, get ID
                            targetID = names[args[1]];
                        }
                        if (!clients.ContainsKey(targetID)) {
                            reply.Payload = Encoding.ASCII.GetBytes($"User with ID {targetID} not found.");
                            await PacketIO.SendPacketAsync(conn.io, reply);
                            return true;
                        }
                        string msg = string.Join(" ", args, 2, args.Length - 2);
                        Packet whisper = new Packet {
                            ClientID = clientID,
                            Headers = new Dictionary<string, string> { { "Type", "Whisper" } },
                            Payload = Encoding.ASCII.GetBytes($"{msg}")
                        };
                        await PacketIO.SendPacketAsync(clients[targetID].io, whisper);
                        return true;
                    case "create":
                        return true;
                    default:
                        reply = new Packet {
                            ClientID = "Server",
                            Headers = new Dictionary<string, string> { { "Type", "Message" } },
                            Payload = Encoding.ASCII.GetBytes($"Unknown command: {text}. Type --help for a list of commands.")
                        };
                        await PacketIO.SendPacketAsync(conn.io, reply);
                        return true;
                }
            case "VoiceInvite":
                Notification?.Invoke(NotificationType.Info, $"Received voice invite from client {id}.");
                var requestedTargets = Array.Empty<string>();
                try {
                    requestedTargets = JsonSerializer.Deserialize(incoming.Payload, Common.CommonJsonContext.Default.StringArray) ?? Array.Empty<string>();
                }
                catch (Exception) { }

                var participants = new HashSet<int> { id };
                bool includeServer = false;

                if (requestedTargets.Length == 0) {
                    includeServer = serverPlatform == "Windows";
                    if (!includeServer) {
                        await SendVoiceInviteWarningAsync(id, "Server cannot join voice on this platform.");
                    }
                }

                foreach (var target in requestedTargets) {
                    if (string.Equals(target, Name, StringComparison.OrdinalIgnoreCase)) {
                        if (serverPlatform == "Windows") {
                            includeServer = true;
                        }
                        else {
                            await SendVoiceInviteWarningAsync(id, "Server cannot join voice on this platform.");
                        }
                        continue;
                    }

                    if (!names.TryGetValue(target, out var targetId)) {
                        await SendVoiceInviteWarningAsync(id, $"Client '{target}' is not connected.");
                        continue;
                    }

                    if (clientPlatforms.TryGetValue(targetId, out var platformName) && platformName != "Windows") {
                        await SendVoiceInviteWarningAsync(id, $"{target} is on {platformName} and cannot join voice chat.");
                        continue;
                    }

                    participants.Add(targetId);
                }

                voiceParticipants.Clear();
                foreach (var participantId in participants) {
                    voiceParticipants[participantId] = 1;
                }

                if (includeServer) {
                    voiceParticipants[0] = 1;
                }

                if (voiceParticipants.Count == 1) {
                    Notification?.Invoke(NotificationType.Warning, $"{clientID} tried to make a voice room with only themselves. Rejecting.");
                    await SendVoiceInviteWarningAsync(id, "You can't make a voice room with just yourself.");
                    voiceParticipants.Clear();
                    return true;
                }

                foreach (var participantId in participants) {
                    if (clients.TryGetValue(participantId, out var participantConn)) {
                        var acceptPacket = new Packet {
                            ClientID = "Server",
                            Headers = new Dictionary<string, string> { { "Type", "VoiceAccepted" } },
                            Payload = Array.Empty<byte>()
                        };
                        await PacketIO.SendPacketAsync(participantConn.io, acceptPacket);
                    }
                }

                StartUdpListenerIfNeeded();
                if (includeServer && serverPlatform == "Windows") {
                    disableServerMic = clients.TryGetValue(id, out var inviterConn) && IsSameMachine(inviterConn.socket);
                    if (disableServerMic) {
                        Notification?.Invoke(NotificationType.Info, "Client is local; server microphone disabled.");
                    }
                    else {
                        StartServerMicrophone();
                    }
                }

                Notification?.Invoke(NotificationType.Info, $"Voice room created with {participants.Count + (includeServer ? 1 : 0)} participant(s).");
                return true;
            case "Ack":
                Notification?.Invoke(NotificationType.Info, $"Received ACK from client {id}.");
                //Sets the client's name
                names[clientID] = id;
                if (headers.TryGetValue("Platform", out var platform)) {
                    clientPlatforms[id] = platform;
                }
                return true;
            case "Pos":
                //Position update packet
                positions[id] = PositionCodec.Decode(incoming.Payload);
                //Just broadcast it to everyone else
                await BroadcastAsync(incoming, id);
                return true;
            case "Auth":
                //Authentication packet containing the client's password
                switch (await AuthenticateClient(clientID, text, id)) {
                    case AuthenticationStatus.Success:
                        Notification?.Invoke(NotificationType.Info, $"Client {id} authenticated successfully as {clientID}.");
                        names[clientID] = id;
                        await SendInitialPackets(conn.io, id);
                        return true;
                    case AuthenticationStatus.WrongPassword:
                        Notification?.Invoke(NotificationType.Warning, $"Client {id} used the wrong password. Closing connection.");
                        reply.Headers["Type"] = "AuthFailure";
                        reply.Payload = Encoding.UTF8.GetBytes("Incorrect password.");
                        await PacketIO.SendPacketAsync(conn.io, reply);
                        RemoveClient(id);
                        return false;
                    case AuthenticationStatus.WrongUsername:
                        Notification?.Invoke(NotificationType.Warning, $"Client {id} tried to login as non-existent user {clientID}. Closing connection.");
                        reply.Headers["Type"] = "AuthFailure";
                        reply.Payload = Encoding.UTF8.GetBytes("No account with that username exists.");
                        await PacketIO.SendPacketAsync(conn.io, reply);
                        RemoveClient(id);
                        return false;
                    case AuthenticationStatus.AlreadyLoggedIn:
                        Notification?.Invoke(NotificationType.Warning, $"User {clientID} is already logged in. Rejecting client {id}.");
                        reply.Headers["Type"] = "AuthFailure";
                        reply.Payload = Encoding.UTF8.GetBytes("Another user is already logged in with that account.");
                        await PacketIO.SendPacketAsync(conn.io, reply);
                        RemoveClient(id);
                        return false;
                    default:
                        break;
                }
                break;
            case "AuthCodeRequest":
                //Passes a pointer so our global variable gets updated
                Utility.GenerateAuthCode(ref currentAuthCode);
                Notification?.Invoke(NotificationType.Info, $"Authentication Code: {currentAuthCode}");
                return true;
            case "AuthCode":
                int code = 0;
                if (int.TryParse(text, out code)) {
                    if (code == currentAuthCode) {
                        Notification?.Invoke(NotificationType.Info, $"Client {id} sent correct auth code {text}.");
                        reply.Headers["Type"] = "AuthStatus";
                        reply.Payload = Encoding.UTF8.GetBytes(AuthenticationStatus.Success.ToString());
                        await PacketIO.SendPacketAsync(conn.io, reply);
                        return true;
                    }
                    Notification?.Invoke(NotificationType.Info, $"Client {id} sent incorrect auth code {text}.");
                    reply.Headers["Type"] = "AuthStatus";
                    reply.Payload = Encoding.UTF8.GetBytes(AuthenticationStatus.WrongCode.ToString());
                    await PacketIO.SendPacketAsync(conn.io, reply);
                    return false;
                }
                return true;
            case "SetPassword":
                if (!passwords.ContainsKey(clientID)) {
                    File.AppendAllText(passwordsFile, $"\n{clientID}, {text}");
                    Notification?.Invoke(NotificationType.Info, $"Registered new user {clientID}.");

                }
                passwords[clientID] = text;
                Notification?.Invoke(NotificationType.Info, $"Password for {clientID} updated.");
                return true;
            case "CreateNewUser":
                var name = headers["Name"];
                reply.Headers["Type"] = "AuthStatus";
                if (name != clientID) {
                    Notification?.Invoke(NotificationType.Warning, "Mismatched username and clientID. Closing connection.");
                    reply.Payload = Encoding.UTF8.GetBytes(AuthenticationStatus.Failed.ToString());
                    await PacketIO.SendPacketAsync(conn.io, reply);
                    return false;
                }
                if (passwords.ContainsKey(clientID)) {
                    Notification?.Invoke(NotificationType.Warning, $"Client {id} tried to register using an existing username. Closing connection.");
                    reply.Payload = Encoding.UTF8.GetBytes(AuthenticationStatus.UsernameTaken.ToString());
                    await PacketIO.SendPacketAsync(conn.io, reply);
                    return false;
                }
                //Otherwise we register the user
                //Map their password hash to their name
                passwords[name] = headers["PasswordHash"];
                //Write new password and username to the file
                File.AppendAllText(passwordsFile, $"\n{clientID}, {headers["PasswordHash"]}");
                Notification?.Invoke(NotificationType.Info, $"User {name} registered.");
                reply.Payload = Encoding.UTF8.GetBytes(AuthenticationStatus.Success.ToString());
                await PacketIO.SendPacketAsync(conn.io, reply);
                await SendInitialPackets(conn.io, id);

                return true;
            case "FileStart":
                await PacketIO.HandleFileStartAsync(conn.io, incoming, files, Name, defaultSaveDir);
                Notification?.Invoke(NotificationType.Info, "Received FileStart packet");
                return true;
            case "FileChunk":
                await PacketIO.HandleFileChunkAsync(incoming, files);

                //Progress logging
                if (incoming.Headers.TryGetValue("FileKey", out var key) && files.TryGetValue(key, out var state)) {
                    int index = int.Parse(incoming.Headers["Index"]);
                    int currentChunk = index + 1;
                    int totalChunks = state.ExpectedChunks;

                    double percent = (double)currentChunk / totalChunks * 100.0;
                    string bar = BuildProgessBar(percent, width: 40);
                    Notification?.Invoke(NotificationType.Info, $"[PROGRESS]{bar}, chunk {currentChunk}/{totalChunks}");
                }
                return true;
            case "FileEnd":
                await PacketIO.HandleFileEndAsync(conn.io, incoming, files, Name);
                Notification?.Invoke(NotificationType.Info, "Received FileEnd packet");
                return true;
            case "Disconnect":
                Notification?.Invoke(NotificationType.Warning, $"Client {clientID} requested UDP disconnect.");
                CloseUdpConnection();
                return true;
            default:
                Notification?.Invoke(NotificationType.Warning, $"Invalid packet header: {type}.");
                break;
        }

        return false;
    }


    // === Server-Exclusive Messaging Functions ===
    private async Task BroadcastAsync(Packet packet, int? excludeID = null) {
        foreach (var currClient in clients) {
            if (currClient.Key == excludeID) continue; //Skip sending to the original sender
            Notification?.Invoke(NotificationType.Info, $"Sending reply to client {currClient.Key}.");
            await PacketIO.SendPacketAsync(currClient.Value.io, packet);
        }
    }

    private async Task SendVoiceInviteWarningAsync(int clientId, string message) {
        if (clients.TryGetValue(clientId, out var conn)) {
            var packet = new Packet {
                ClientID = "Server",
                Headers = new Dictionary<string, string> { { "Type", "Message" } },
                Payload = Encoding.UTF8.GetBytes(message)
            };

            await PacketIO.SendPacketAsync(conn.io, packet);
        }
    }

    private async Task BroadcastDisconnectAsync(string reason) {
        var packet = new Packet {
            ClientID = "Server",
            Headers = new Dictionary<string, string> { { "Type", "Disconnect" } },
            Payload = Encoding.UTF8.GetBytes(reason)
        };

        await BroadcastAsync(packet);
        CloseUdpConnection();
    }

    private async Task SendInitialPackets(Stream stream, int id) {
        Packet pkt = new Packet {
            ClientID = "Server",
            Headers = new Dictionary<string, string>
            {
                { "Type", "Data" },
                { "Var", "id" }
            },
            //Tells the client what its id is
            Payload = Encoding.UTF8.GetBytes(id.ToString())
        };
        await PacketIO.SendPacketAsync(stream, pkt);

        pkt.Headers = new Dictionary<string, string>
        {
            { "Type", "Data" },
            { "Var", "commands" }
        };
        pkt.Payload = cmdJson;
        await PacketIO.SendPacketAsync(stream, pkt);
        //Send a file upon connection (DEV PURPOSES)
    }

    // === Client Authentication ===
    private async Task<AuthenticationStatus> AuthenticateClient(string username, string passwordHash, int clientId) {
        if (names.TryGetValue(username, out var existingId)) {
            if (!clients.ContainsKey(existingId)) {
                names.TryRemove(username, out _);
            }
            else if (existingId != clientId) {
                return AuthenticationStatus.AlreadyLoggedIn;
            }
        }
        if (!passwords.ContainsKey(username)) {
            return AuthenticationStatus.WrongUsername;
        }
        if (passwords[username] != passwordHash) {
            return AuthenticationStatus.WrongPassword;
        }
        return AuthenticationStatus.Success;
    }

    private async Task ReadPasswords(string filename) {
        try {
            using (var fileReader = File.ReadLines(filename).GetEnumerator()) {
                while (fileReader.MoveNext()) {
                    var line = fileReader.Current;
                    var parts = line.Split(", ");
                    if (parts.Length != 2) {
                        Notification?.Invoke(NotificationType.Info, $"Invalid line in password file: {line}");
                        continue;
                    }
                    passwords[parts[0]] = parts[1];
                }
            }
        }
        catch (FileNotFoundException) {
            Notification?.Invoke(NotificationType.Warning, "Password file not found. Users cannot connect until one is made. " +
                "\nRun --create <name> <password> to create the file and the first user.");
        }
        catch (Exception e) {
            Notification?.Invoke(NotificationType.Error, $"Unexepected exception: {e.Message}");
        }
    }

    private void RemoveClient(int id) {
        if (clients.TryRemove(id, out var conn)) {
            try { conn.io.Dispose(); } catch { }
            try { conn.socket.Dispose(); } catch { }
        }

        positions.TryRemove(id, out _);

        var username = names.FirstOrDefault(kvp => kvp.Value == id).Key;
        if (!string.IsNullOrEmpty(username)) {
            names.TryRemove(username, out _);
        }

        if (pendingVoiceClientId == id) {
            pendingVoiceClientId = null;
            voiceInviteCts?.Cancel();
            voiceInviteCts = null;
        }

        clientPlatforms.TryRemove(id, out _);
        udpClients.TryRemove(id, out _);
        voiceParticipants.TryRemove(id, out _);
    }

    private static bool IsSameMachine(Socket socket) {
        if (socket.RemoteEndPoint is not IPEndPoint remote) {
            return false;
        }

        if (IPAddress.IsLoopback(remote.Address)) {
            return true;
        }

        try {
            var localAddresses = Dns.GetHostAddresses(Dns.GetHostName());
            return localAddresses.Any(addr => addr.Equals(remote.Address));
        }
        catch {
            return false;
        }
    }

    private static string BuildProgessBar(double percent, int width = 40) {
        percent = Math.Clamp(percent, 0.0, 100.0);
        int filled = (int)Math.Round(percent / 100.0 * width);
        if (filled > width) filled = width;

        string filledPart = new string('\u2588', filled);
        string emptyPart = new string('-', width - filled);
        return $"[{filledPart}{emptyPart}] {percent,5:0.0}%";
    }

    private enum AuthenticationStatus {
        Success,
        Failed,
        WrongPassword,
        WrongUsername,
        WrongCode,
        UsernameTaken,
        AlreadyLoggedIn
    }


    // === Certificate Loading ===
    private X509Certificate2 LoadServerCertificate() {
        var thumb = (Environment.GetEnvironmentVariable("SERVER_CERT_THUMBPRINT") ?? "");
        if (string.IsNullOrEmpty(thumb)) {
            throw new InvalidOperationException("SERVER_CERT_THUMBPRINT environment variable not set.");
        }

        //First check local machine store
        var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var cert = store.Certificates
            .Find(X509FindType.FindByThumbprint, thumb, validOnly: false)
            .OfType<X509Certificate2>()
            .FirstOrDefault(c => c.HasPrivateKey);

        //If it's not in local machine, check current user store
        if (cert == null) {
            store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            cert = store.Certificates
            .Find(X509FindType.FindByThumbprint, thumb, validOnly: false)
            .OfType<X509Certificate2>()
            .FirstOrDefault(c => c.HasPrivateKey)
            //if it's still null, it means it genuinely doesn't exist
            ?? throw new InvalidOperationException("Certificate with specified thumbprint not found.");
        }
        return cert;
    }

    public async Task SendMessageAsync(string message) {
        if (message is null) return;
        Packet pkt = new Packet {
            ClientID = "Server",
            Headers = new Dictionary<string, string>
            {
                { "Type", "Message" },
            },
            Payload = Encoding.UTF8.GetBytes(message)
        };
        await BroadcastAsync(pkt);
    }

    public async Task HandleServerCommandAsync(string line) {
        if (line is null) return;

        var parts = line.Split(' ');
        var cmd = parts[0];
        var args = parts.Skip(1).ToArray();
        switch (cmd) {
            case "setSaveDir":
                if (args.Length != 1) {
                    Notification?.Invoke(NotificationType.Info, "Usage: --setSaveDir <directory>");
                    return;
                }
                var dir = args[0];
                defaultSaveDir = dir;
                Notification?.Invoke(NotificationType.Info, $"Default save directory set to {defaultSaveDir}");
                return;
            case "file":
                //skip verification for now
                await PacketIO.SendFileAsync(_stream, args[0], pendingResponses);
                return;
            case "accept":
                if (pendingVoiceClientId is null) {
                    Notification?.Invoke(NotificationType.Info, "No pending voice invite to accept.");
                    return;
                }

                var inviteId = pendingVoiceClientId.Value;
                pendingVoiceClientId = null;
                voiceInviteCts?.Cancel();
                voiceInviteCts = null;

                if (clients.TryGetValue(inviteId, out var inviteConn)) {
                    var acceptPacket = new Packet {
                        ClientID = "Server",
                        Headers = new Dictionary<string, string> { { "Type", "VoiceAccepted" } },
                        Payload = Array.Empty<byte>()
                    };

                    await PacketIO.SendPacketAsync(inviteConn.io, acceptPacket);
                    StartUdpListenerIfNeeded();
                    disableServerMic = IsSameMachine(inviteConn.socket);
                    if (disableServerMic) {
                        Notification?.Invoke(NotificationType.Info, "Client is local; server microphone disabled.");
                    }
                    else {
                        StartServerMicrophone();
                    }
                    Notification?.Invoke(NotificationType.Info, "Voice invite accepted. UDP listener started.");
                }
                else {
                    Notification?.Invoke(NotificationType.Warning, "Client disconnected before invite could be accepted.");
                }
                return;
            case "disconnect":
            case "dc":
                await BroadcastDisconnectAsync("Server requested UDP disconnect.");
                return;
            case "create":
                if (args.Length != 2) {
                    Notification?.Invoke(NotificationType.Info, "Usage: --create <username> <password>");
                    return;
                }
                await CreateUserAsync(args[0], args[1]);
                return;
            case "list-users":
                if (passwords.Count == 0) {
                    Notification?.Invoke(NotificationType.Info, "No users registered.");
                    return;
                }
                StringBuilder sb = new StringBuilder("Registered Users:");
                foreach (var user in passwords.Keys) {
                    sb.Append($"\n- {user}");
                }
                Notification?.Invoke(NotificationType.Info, sb.ToString());
                return;
            case "remove":
                if (args.Length != 1) {
                    Notification?.Invoke(NotificationType.Info, "Usage: --delete-user <username>");
                    return;
                }
                var usernameToDelete = args[0];
                if (!passwords.ContainsKey(usernameToDelete)) {
                    Notification?.Invoke(NotificationType.Warning, $"User {usernameToDelete} does not exist.");
                    return;
                }
                passwords.Remove(usernameToDelete, out var _);
                //Rewrite the passwords file
                try {
                    using (var writer = new StreamWriter(passwordsFile, append: false)) {
                        foreach (var kvp in passwords) {
                            await writer.WriteLineAsync($"{kvp.Key}, {kvp.Value}");
                        }
                    }
                    Notification?.Invoke(NotificationType.Info, $"User {usernameToDelete} deleted successfully.");
                }
                catch (Exception e) {
                    Notification?.Invoke(NotificationType.Error, $"Failed to delete user from file: {e.Message}");
                }
                return;
            default:
                Notification?.Invoke(NotificationType.Info, "Unknown command.");
                return;
        }
    }

    private async Task CreateUserAsync(string name, string password) {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(password)) {
            Notification?.Invoke(NotificationType.Warning, "Name or password cannot be empty. Aborting.");
            return;
        }
        if (passwords.ContainsKey(name)) {
            Notification?.Invoke(NotificationType.Warning, "User with that name already exists. Aborting.");
            return;
        }
        var passwordHash = Utility.SHA256Hash(password);
        passwords[name] = passwordHash;
        if (!File.Exists(passwordsFile)) {
            Notification?.Invoke(NotificationType.Info, "Created passwords.txt file.");
        }
        //AppendAllText creates file if it doesn't exist, we just fire the notification first
        await File.AppendAllTextAsync(passwordsFile, $"{name}, {passwordHash}\n");
        Notification?.Invoke(NotificationType.Info, $"Created user {name} successfully.");
    }

    // === IDisposable Implementation ===
    public async ValueTask DisposeAsync() {
        try { _stream?.Dispose(); } catch { }
        StopServerMicrophone();
    }
}
