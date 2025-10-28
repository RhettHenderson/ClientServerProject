using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Common;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;

namespace Client_Server;
public class Server : IAsyncDisposable
{
    // === Networking ===
    private static Socket listener;
    private static string Name = "Server";

    // === Connection Handling ===
    private sealed class Conn
    {
        public Socket socket { get; }
        public Stream io { get; }

        public Conn(Socket s, Stream i)
        {
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
    private static readonly ConcurrentDictionary<string, string> passwords = new();        // username -> password hash
    private static int currentAuthCode = 111111;
    private static string passwordsFile = "passwords.txt";

    // === File I/O ===
    private static Stream _stream = null;
    private static string defaultSaveDir = @"C:\Users\rhett\Documents\uploads";

    // === Commands & Misc ===
    private static int nextID = 0;
    private static readonly string[] commands = { "help", "whisper", "w" };
    private static readonly byte[] cmdJson = JsonSerializer.SerializeToUtf8Bytes(commands, CommonJsonContext.Default.StringArray);

    // === Events and Actions ===
    public event Action<string, string>? MessageReceived;   // (from, text)
    public event Action<string, string>? WhisperReceived;   // (from, text)
    public event Action<string[]>? CommandsReceived;
    public event Action<int>? IdAssigned;
    public event Action? Disconnected;
    public event Action<string>? Error;
    public event Action<NotificationType, string>? Notification;

    //--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

    // === Main Server Loop ===
    public async Task ExecuteServerAsync(int port, string ip)
    {
        Console.Title = "Server";
        await InitListener(ip);

        //await AcceptLoopAsync();
        var acceptTask = AcceptLoopAsync();
        var consoleTask = Task.Run(() => RunServerConsoleAsync());

        await Task.WhenAny(acceptTask, consoleTask);

        try { await Task.WhenAll(acceptTask, consoleTask); } catch { }
    }
    public async Task InitListener(string ip)
    {
        await ReadPasswords(passwordsFile);
        IPAddress ipAddr;
        if (ip == "")
        {
            ipAddr = IPAddress.Loopback; //127.0.0.1
        }
        else
        {
            try
            {
                ipAddr = IPAddress.Parse(ip);
            }
            catch (FormatException)
            {
                Error?.Invoke("Invalid IP address format.");
                throw;
            }
        }
        IPEndPoint localEndPoint = new IPEndPoint(ipAddr, 11111);
        //Create TCP Socket
        listener = new Socket(ipAddr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(localEndPoint);
        listener.Listen(10);
    }

    
    public async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                int id = await WaitForConnectionAsync();
                _ = HandleClientAsync(id);
            }
        }
        catch (OperationCanceledException)
        {
            Error?.Invoke("Server is shutting down.");
        }
        finally
        {
            try { listener.Close(); } catch { }
        }
    }
    public async Task<int> WaitForConnectionAsync()
    {
        Notification?.Invoke(NotificationType.Info, "Waiting for connection...");
        Socket client = await listener.AcceptAsync();
        //Uses the Nagle algorithm (google for more info)
        client.NoDelay = true;

        var net = new NetworkStream(client, ownsSocket: true);
        var ssl = new SslStream(net, leaveInnerStreamOpen: false);
        //TODO: load certificate
        X509Certificate2 cert = LoadServerCertificate();

        await ssl.AuthenticateAsServerAsync(
            serverCertificate: cert,
            clientCertificateRequired: false,
            enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
            checkCertificateRevocation: true
        );
        _stream = ssl;

        int id = Interlocked.Increment(ref nextID);
        clients[id] = new Conn(client, ssl);
        Notification?.Invoke(NotificationType.Info, $"Client #{id} connected.");
        return id;
    }


    public async Task HandleClientAsync(int id)
    {
        Notification?.Invoke(NotificationType.Info, $"Client #{id} handler started.");
        //Sends the packet to tell the client what its id is
        while (true)
        {
            bool keepAlive = await ProcessPacketAsync(id);
            if (!keepAlive) break;
        }
    }

    public async Task<bool> ProcessPacketAsync(int id)
    {
        var conn = clients[id];
        PacketStatus status;
        Packet incoming = null;
        try
        {
            var (s, i) = await PacketIO.ReceivePacketAsync(conn.io);
            status = s;
            incoming = i;
        }
        catch
        {
            status = PacketStatus.Disconnected;
        }
        Notification?.Invoke(NotificationType.Info, status.ToString());
        if (status == PacketStatus.Disconnected)
        {
            Notification?.Invoke(NotificationType.Warning, $"Client {id} forcibly disconnected");
            if (clients.TryRemove(id, out conn))
            {
                try { conn.io.Dispose(); } catch { }
                try { conn.socket.Dispose(); } catch { }
            }
            clients.TryRemove(new KeyValuePair<int, Conn>(id, clients[id]));
            return false;
        }
        else if (status == PacketStatus.Error)
        {
            Error?.Invoke("An error occured trying to receive the last packet. Closing connection.");
            if (clients.TryRemove(id, out conn))
            {
                try { conn.io.Dispose(); } catch { }
                try { conn.socket.Dispose(); } catch { }
            }
            clients.TryRemove(new KeyValuePair<int, Conn>(id, clients[id]));
            return false;
        }
        //if we reach here status is Ok
        var clientID = incoming.ClientID;
        var headers = incoming.Headers;
        var text = Encoding.UTF8.GetString(incoming.Payload);
        //Default values for the packet
        Packet reply = new Packet
        {
            ClientID = "Server",
            Headers = new Dictionary<string, string> { { "Type", "Message"} },
            Payload = Encoding.UTF8.GetBytes("")
        };

        //Step 1: Read headers to determine packet type
        //Types so far are "Message", "Command", "Ack", "Data"
        var type = headers["Type"];

        //Before actually processing the packet, check pendingResponses
        if (pendingResponses.TryRemove(type, out var tcs))
        {
            tcs.SetResult(incoming);
            return true;
        }

        switch (type)
        {
            case ("Message"):
                Notification?.Invoke(NotificationType.Info, $"{clientID} sent chat {text}");
                await BroadcastAsync(incoming, id);
                return true;
            case ("Command"):
                switch (text.Split(" ")[0])
                {
                    case "help":
                        StringBuilder sb = new StringBuilder("Available Commands: ");
                        foreach (string cmd in commands)
                        {
                            sb.Append($"--{cmd} ");
                        }
                        reply.Payload = Encoding.ASCII.GetBytes(sb.ToString());
                        await PacketIO.SendPacketAsync(conn.io, reply);
                        return true;

                    case "whisper":
                    case "w":
                        string[] args = text.Split(" ");
                        if (args.Length < 3)
                        {
                            reply.Payload = Encoding.ASCII.GetBytes("Usage: --whisper <ID> <message>");
                            await PacketIO.SendPacketAsync(conn.io, reply);
                            return true;
                        }
                        //Step 1: Check if the user used ID or name
                        //Step 2: If using ID, no changes made. If using name, look up ID
                        //Step 3: Check if ID exists
                        //Step 4: Send message if it does, error if it doesn't
                        if (!int.TryParse(args[1], out int targetID))
                        {
                            //User used a name instead of an ID
                            if (!names.ContainsKey(args[1]))
                            {
                                reply.Payload = Encoding.ASCII.GetBytes($"User with name {args[1]} not found.");
                                await PacketIO.SendPacketAsync(conn.io, reply);
                                return true;
                            }
                            //Name exists, get ID
                            targetID = names[args[1]];
                        }
                        if (!clients.ContainsKey(targetID))
                        {
                            reply.Payload = Encoding.ASCII.GetBytes($"User with ID {targetID} not found.");
                            await PacketIO.SendPacketAsync(conn.io, reply);
                            return true;
                        }
                        string msg = string.Join(" ", args, 2, args.Length - 2);
                        Packet whisper = new Packet
                        {
                            ClientID = clientID,
                            Headers = new Dictionary<string, string> { { "Type", "Whisper" } },
                            Payload = Encoding.ASCII.GetBytes($"{msg}")
                        };
                        await PacketIO.SendPacketAsync(clients[targetID].io, whisper);
                        return true;
                    case "create":
                        return true;
                    default:
                        reply = new Packet
                        {
                            ClientID = "Server",
                            Headers = new Dictionary<string, string> { { "Type", "Message" } },
                            Payload = Encoding.ASCII.GetBytes($"Unknown command: {text}. Type --help for a list of commands.")
                        };
                        await PacketIO.SendPacketAsync(conn.io, reply);
                        return true;
                }
            case "Ack":
                Notification?.Invoke(NotificationType.Info, $"Received ACK from client {id}.");
                //Sets the client's name
                names[clientID] = id; 
                return true;
            case "Pos":
                //Position update packet
                positions[id] = PositionCodec.Decode(incoming.Payload);
                //Just broadcast it to everyone else
                await BroadcastAsync(incoming, id);
                return true;
            case "Auth":
                //Authentication packet containing the client's password
                switch (await AuthenticateClient(clientID, text))
                {
                    case AuthenticationStatus.Success:
                        Notification?.Invoke(NotificationType.Info, $"Client {id} authenticated successfully as {clientID}.");
                        await SendInitialPackets(conn.io, id);
                        return true;
                    case AuthenticationStatus.WrongPassword:
                        Notification?.Invoke(NotificationType.Warning, $"Client {id} used the wrong password. Closing connection.");
                        reply.Headers["Type"] = "AuthFailure";
                        await PacketIO.SendPacketAsync(conn.io, reply);
                        if (clients.TryRemove(id, out conn))
                        {
                            try { conn.io.Dispose(); } catch { }
                            try { conn.socket.Dispose(); } catch { }
                        }
                        clients.TryRemove(new KeyValuePair<int, Conn>(id, clients[id]));
                        return false;
                    case AuthenticationStatus.WrongUsername:
                        Notification?.Invoke(NotificationType.Warning, $"Client {id} tried to login as non-existent user {clientID}. Closing connection.");
                        reply.Headers["Type"] = "AuthFailure";
                        await PacketIO.SendPacketAsync(conn.io, reply);
                        if (clients.TryRemove(id, out conn))
                        {
                            try { conn.io.Dispose(); } catch { }
                            try { conn.socket.Dispose(); } catch { }
                        }
                        clients.TryRemove(new KeyValuePair<int, Conn>(id, clients[id]));
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
                if (int.TryParse(text, out code))
                {
                    if (code == currentAuthCode)
                    {
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
                if (!passwords.ContainsKey(clientID))
                {
                    File.AppendAllText(passwordsFile, $"\n{clientID}, {text}");
                    Notification?.Invoke(NotificationType.Info, $"Registered new user {clientID}.");

                }
                passwords[clientID] = text;
                Notification?.Invoke(NotificationType.Info, $"Password for {clientID} updated.");
                return true;
            case "CreateNewUser":
                var name = headers["Name"];
                reply.Headers["Type"] = "AuthStatus";
                if (name != clientID)
                {
                    Notification?.Invoke(NotificationType.Warning, "Mismatched username and clientID. Closing connection.");
                    reply.Payload = Encoding.UTF8.GetBytes(AuthenticationStatus.Failed.ToString());
                    await PacketIO.SendPacketAsync(conn.io, reply);
                    return false;
                }
                if (passwords.ContainsKey(clientID))
                {
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
                Notification?.Invoke(NotificationType.Info, "Received FileChunk packet");
                return true;
            case "FileEnd":
                await PacketIO.HandleFileEndAsync(conn.io, incoming, files, Name);
                Notification?.Invoke(NotificationType.Info, "Received FileEnd packet");
                return true;
            default:
                Notification?.Invoke(NotificationType.Warning, $"Invalid packet header: {type}.");
                break;
        }
        
        return false;
    }


    // === Server-Exclusive Messaging Functions ===
    private async Task BroadcastAsync(Packet packet, int? excludeID = null)
    {
        foreach (var currClient in clients)
        {
            if (currClient.Key == excludeID) continue; //Skip sending to the original sender
            Notification?.Invoke(NotificationType.Info, $"Sending reply to client {currClient.Key}.");
            await PacketIO.SendPacketAsync(currClient.Value.io, packet);
        }
    }

    private async Task SendInitialPackets(Stream stream, int id)
    {
        Packet pkt = new Packet
        {
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
        Notification?.Invoke(NotificationType.Info, $"Sent ID packet to client {id}");

        pkt.Headers = new Dictionary<string, string>
        {
            { "Type", "Data" },
            { "Var", "commands" }
        };
        pkt.Payload = cmdJson;
        await PacketIO.SendPacketAsync(stream, pkt);
        //Send a file upon connection (DEV PURPOSES)
        Notification?.Invoke(NotificationType.Info, $"Sent commands packet to client {id}");
    }

    // === Client Authentication ===
    private async Task<AuthenticationStatus> AuthenticateClient(string username, string passwordHash)
    {
        if (!passwords.ContainsKey(username))
        {
            return AuthenticationStatus.WrongUsername;
        }
        if (passwords[username] != passwordHash)
        {
            return AuthenticationStatus.WrongPassword;
        }
        return AuthenticationStatus.Success;
    }

    private async Task ReadPasswords(string filename)
    {
        using (var fileReader = File.ReadLines(filename).GetEnumerator())
        {
            while (fileReader.MoveNext())
            {
                var line = fileReader.Current;
                var parts = line.Split(", ");
                if (parts.Length != 2)
                {
                    Notification?.Invoke(NotificationType.Info, $"Invalid line in password file: {line}");
                    continue;
                }
                passwords[parts[0]] = parts[1];
            }
        }
    }

    private enum AuthenticationStatus
    {
        Success,
        Failed,
        WrongPassword,
        WrongUsername,
        WrongCode,
        UsernameTaken
    }


    // === Certificate Loading ===
    private X509Certificate2 LoadServerCertificate()
    {
        var thumb = (Environment.GetEnvironmentVariable("SERVER_CERT_THUMBPRINT") ?? "");
        if (string.IsNullOrEmpty(thumb))
        {
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
        if (cert == null)
        {
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


    // === Server Console Handling ===
    private async Task RunServerConsoleAsync()
    {
        while (true)
        {
            string? line = Console.ReadLine();
            if (line is null) break;
            if (string.IsNullOrEmpty(line)) continue;

            try
            {
                await HandleServerCommandAsync(line.Trim());
            }
            catch (Exception ex)
            {
                Notification?.Invoke(NotificationType.Info, $"Error processing command: {ex.Message}");
            }
        }
    }

    private async Task HandleServerCommandAsync(string line)
    {
        if (line is null) return;

        if (line.StartsWith("--"))
        {
           var parts = line[2..].Split(' ');
           var cmd = parts[0];
           var args = parts.Skip(1).ToArray();
           switch (cmd)
            {
                case "setSaveDir":
                    if (args.Length != 1)
                    {
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
                default:
                    Notification?.Invoke(NotificationType.Info, "Unknown command.");
                    return;
            }
        }
    }

    // === IDisposable Implementation ===
    public async ValueTask DisposeAsync()
    {
        try { _stream?.Dispose(); } catch { }
    }
}