using Common;
using static Common.Utility;
using static ServerApp.ServerUtils;
using Syroot.Windows.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace ServerApp;

public class Server : IAsyncDisposable {
    // === Networking ===
    private Socket listener;
    private static string Name = "Server";

    // === Connection Handling ===
    internal sealed class Conn {
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
    private static readonly ConcurrentDictionary<string, FileReceiveState> files = new();  // fileKey -> state
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<Packet>> pendingResponses = new(); // expectedType -> TaskCompletionSource
    internal readonly ConcurrentDictionary<int, string> clientPlatforms = new(); // ID -> platform name
    private int reconnectingID = 0;

    // === File I/O ===
    private static Stream _stream = null;
    private string defaultSaveDir = "";

    // === Commands & Misc ===
    private static int nextID = 0;
    private static readonly string[] commands = { "help", "whisper", "w", "disconnect", "dc" };
    private static readonly byte[] cmdJson = JsonSerializer.SerializeToUtf8Bytes(commands, CommonJsonContext.Default.StringArray);
    public IPAddress? listeningIp { get; private set; }
    public int listeningPort { get; private set; }

    private ServerAuth auth;
    private VoiceChatManager vc;

    // === Events and Actions ===
    public event Action<string, string>? WhisperReceived;   // (from, text)
    public event Action<string>? Error;
    public event Action<NotificationType, string>? Notification;
    public event Action<string, string> MessageReceived;    // (from, text)

    public void Initialize(int port) {
        listeningPort = port;
        Console.Title = "Server";
        InitListener(GetLocalIP());
        this.vc = new VoiceChatManager(listeningIp, listeningPort, names, clients, Notification, Name);
        this.auth = new ServerAuth(Notification, names, clients, this.vc, clientPlatforms);
        auth.Load();
        try {
            defaultSaveDir = KnownFolders.Downloads.Path;
        }
        catch (Exception e) {
            defaultSaveDir = "/downloads";
        }
    }

    // === Main Server Loop ===
    public async Task Start() {
        await AcceptLoopAsync();
    }
    public void InitListener(string ip) {
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

    public async Task<int> WaitForConnectionAsync() {
        Notification?.Invoke(NotificationType.Info, "Waiting for connection...");
        Socket client = await listener.AcceptAsync();
        int id = Interlocked.Increment(ref nextID);
        Notification?.Invoke(NotificationType.Info, $"Client #{id} initiated handshake.");
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
            if (reconnectingID == id) {
                Notification?.Invoke(NotificationType.Info, "Client is reconnecting after SSL error. Using unencrypted connection.");
                stream = net;
                reconnectingID = 0;
                clients[id] = new Conn(client, stream);
                return id;
            }
            Notification?.Invoke(NotificationType.Warning,
                "WARNING: SSL certificate is not present. " +
                "Ignore this if you intend to use it unencrypted, otherwise refer to the README for instructions on setting up a dev certificate.");
            Notification?.Invoke(NotificationType.Warning, $"Client {id} disconnected during handshake due to SSL error.");
            stream = net; // Fallback to non-SSL
            reconnectingID = id;
            Interlocked.Decrement(ref nextID);
        }
        catch (Exception e) {
            Notification?.Invoke(NotificationType.Error, $"Failed to establish SSL: {e.Message}. Falling back to unencrypted connection.");
            stream = net; // Fallback to non-SSL
        }

        _stream = stream;
        clients[id] = new Conn(client, stream);
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
            auth.RemoveClient(id);
            return false;
        }
        else if (status == PacketStatus.Error) {
            Error?.Invoke("An error occured trying to receive the last packet. Closing connection.");
            auth.RemoveClient(id);
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
                MessageReceived?.Invoke(clientID, text);
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
                    includeServer = vc.serverPlatform == "Windows";
                    if (!includeServer) {
                        await vc.SendVoiceInviteWarningAsync(id, "Server cannot join voice on this platform.");
                    }
                }

                foreach (var target in requestedTargets) {
                    if (string.Equals(target, Name, StringComparison.OrdinalIgnoreCase)) {
                        if (vc.serverPlatform == "Windows") {
                            includeServer = true;
                        }
                        else {
                            await vc.SendVoiceInviteWarningAsync(id, "Server cannot join voice on this platform.");
                        }
                        continue;
                    }

                    if (!names.TryGetValue(target, out var targetId)) {
                        await vc.SendVoiceInviteWarningAsync(id, $"Client '{target}' is not connected.");
                        continue;
                    }

                    if (clientPlatforms.TryGetValue(targetId, out var platformName) && platformName != "Windows") {
                        await vc.SendVoiceInviteWarningAsync(id, $"{target} is on {platformName} and cannot join voice chat.");
                        continue;
                    }

                    participants.Add(targetId);
                }

                vc.voiceParticipants.Clear();
                foreach (var participantId in participants) {
                    vc.voiceParticipants[participantId] = 1;
                }

                if (includeServer) {
                    vc.voiceParticipants[0] = 1;
                }

                if (vc.voiceParticipants.Count == 1) {
                    Notification?.Invoke(NotificationType.Warning, $"{clientID} tried to make a voice room with only themselves. Rejecting.");
                    await vc.SendVoiceInviteWarningAsync(id, "You can't make a voice room with just yourself.");
                    vc.voiceParticipants.Clear();
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

                vc.StartUdpListenerIfNeeded();
                if (includeServer && vc.serverPlatform == "Windows") {
                    vc.disableServerMic = clients.TryGetValue(id, out var inviterConn) && IsSameMachine(inviterConn.socket);
                    if (vc.disableServerMic) {
                        Notification?.Invoke(NotificationType.Info, "Client is local; server microphone disabled.");
                    }
                    else {
                        vc.StartServerMicrophone();
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
            case "Auth":
                //Authentication packet containing the client's password
                switch (await auth.AuthenticateClient(clientID, text, id)) {
                    case ServerAuth.AuthenticationStatus.Success:
                        Notification?.Invoke(NotificationType.Info, $"Client {id} authenticated successfully as {clientID}.");
                        names[clientID] = id;
                        await SendInitialPackets(conn.io, id);
                        Notification?.Invoke(NotificationType.Info, $"Client {id} is now connected.");
                        return true;
                    case ServerAuth.AuthenticationStatus.WrongPassword:
                        Notification?.Invoke(NotificationType.Warning, $"Client {id} used the wrong password. Closing connection.");
                        reply.Headers["Type"] = "AuthFailure";
                        reply.Payload = Encoding.UTF8.GetBytes("Incorrect password.");
                        await PacketIO.SendPacketAsync(conn.io, reply);
                        auth.RemoveClient(id);
                        return false;
                    case ServerAuth.AuthenticationStatus.WrongUsername:
                        Notification?.Invoke(NotificationType.Warning, $"Client {id} tried to login as non-existent user {clientID}. Closing connection.");
                        reply.Headers["Type"] = "AuthFailure";
                        reply.Payload = Encoding.UTF8.GetBytes("No account with that username exists.");
                        await PacketIO.SendPacketAsync(conn.io, reply);
                        auth.RemoveClient(id);
                        return false;
                    case ServerAuth.AuthenticationStatus.AlreadyLoggedIn:
                        Notification?.Invoke(NotificationType.Warning, $"User {clientID} is already logged in. Rejecting client {id}.");
                        reply.Headers["Type"] = "AuthFailure";
                        reply.Payload = Encoding.UTF8.GetBytes("Another user is already logged in with that account.");
                        await PacketIO.SendPacketAsync(conn.io, reply);
                        auth.RemoveClient(id);
                        return false;
                    default:
                        break;
                }
                break;
            case "AuthCodeRequest":
                //Passes a pointer so our global variable gets updated
                await auth.ShowAuthCode();
                return true;
            case "AuthCode":
                int code = 0;
                reply.Headers["Type"] = "AuthStatus";
                reply.Payload = Encoding.UTF8.GetBytes(ServerAuth.AuthenticationStatus.WrongCode.ToString());
                if (int.TryParse(text, out code)) {
                    if (await auth.CheckAuthCode(code)) {
                        Notification?.Invoke(NotificationType.Info, $"Client {id} sent correct auth code {text}.");
                        reply.Payload = Encoding.UTF8.GetBytes(ServerAuth.AuthenticationStatus.Success.ToString());
                    }
                    else {
                        Notification?.Invoke(NotificationType.Info, $"Client {id} sent incorrect auth code {text}.");
                    }
                }
                await PacketIO.SendPacketAsync(conn.io, reply);
                return true;
            case "FileStart":
                await FileTransfer.HandleFileStartAsync(conn.io, incoming, files, Name, defaultSaveDir);
                Notification?.Invoke(NotificationType.Info, "Received FileStart packet");
                return true;
            case "FileChunk":
                await FileTransfer.HandleFileChunkAsync(incoming, files);

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
                await FileTransfer.HandleFileEndAsync(conn.io, incoming, files, Name);
                Notification?.Invoke(NotificationType.Info, "Received FileEnd packet");
                return true;
            case "Disconnect":
                Notification?.Invoke(NotificationType.Warning, $"Client {clientID} requested UDP disconnect.");
                vc.CloseUdpConnection();
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

        pkt.Headers["Type"] = "AuthSuccess";
        pkt.Headers.Remove("Var");
        pkt.Payload = Array.Empty<byte>();
        await PacketIO.SendPacketAsync(stream, pkt);
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
                if (args.Length != 1) {
                    Notification?.Invoke(NotificationType.Info, "Usage: --file <localPath> [-r remoteFilename] [-s saveLocation]");
                    return;
                }
                await FileTransfer.SendFileAsync(_stream, args[0], pendingResponses);
                return;
            case "accept":
                await vc.AcceptInvite();
                return;
            case "disconnect":
            case "dc":
                var packet = new Packet {
                    ClientID = "Server",
                    Headers = new Dictionary<string, string> { { "Type", "Disconnect" } },
                    Payload = Encoding.UTF8.GetBytes("Server closed voice room.")
                };
                await BroadcastAsync(packet);
                vc.CloseUdpConnection();
                return;
            case "create":
                if (args.Length != 2) {
                    Notification?.Invoke(NotificationType.Info, "Usage: --create <username> <password>");
                    return;
                }
                await auth.CreateUserAsync(args[0], args[1]);
                return;
            case "list-users":
                await auth.ListUsers();
                return;
            case "remove":
                if (args.Length != 1) {
                    Notification?.Invoke(NotificationType.Info, "Usage: --remove <username> or <id>");
                    return;
                }
                var name = args[0];
                await auth.RemoveClient(name);
                return;
            default:
                Notification?.Invoke(NotificationType.Info, "Unknown command.");
                return;
        }
    }

    // === IDisposable Implementation ===
    public async ValueTask DisposeAsync() {
        try { _stream?.Dispose(); } catch { }
        vc.StopServerMicrophone();
    }
}
