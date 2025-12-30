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
using static Common.Utility;

namespace ClientApp;

public class Client : IAsyncDisposable {
    // === Static Fields ===
    private static readonly string defaultSaveDir = KnownFolders.Downloads.Path;
    private static readonly ConcurrentDictionary<string, FileReceiveState> files = new(); // Current downloads in progress

    // === Instance Fields ===
    private int id = -1;
    private bool userExists = false;
    private string[] commands = Array.Empty<string>();
    private IPAddress? serverIp;
    private int serverPort;

    // === Public Properties ===
    public string? Name { get; private set; }
    public Stream? _stream { get; private set; }
    public IPEndPoint? LocalEndPoint { get; private set; }
    public string PlatformName { get; } = Utility.GetPlatformName();
    // === Dictionaries / State ===
    public ConcurrentDictionary<string, TaskCompletionSource<Packet>> pendingResponses { get; } = new();
    public IReadOnlyList<ClientInfo> ConnectedClients => connectedClients;
    private ClientInfo[] connectedClients = [];


    // === Events (UI / CLI) ===
    public event Action<string, string>? MessageReceived;      // (from, text)
    public event Action<string, string>? WhisperReceived;      // (from, text)
    public event Action<string[]>? CommandsReceived;
    public event Action<int>? IdAssigned;
    public event Action? Disconnected;
    public event Action<string>? Error;
    public event Action<NotificationType, string>? Notification;
    public event Action<ClientInfo[]>? ClientsUpdated;

    internal VoiceSessionManager session;
    internal Socket socket;
    internal NetworkStream net;
    internal Stream stream;

    //--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

    // === Main Client Logic ===
    public async Task ConnectAsync(string host, int port) {
        var ip = IPAddress.TryParse(host, out var ipAddr) ? ipAddr : IPAddress.Loopback;
        serverIp = ip;
        serverPort = port;
        socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        LocalEndPoint = (IPEndPoint?)socket.LocalEndPoint;

        await socket.ConnectAsync(new IPEndPoint(serverIp, serverPort));
        net = new NetworkStream(socket, ownsSocket: true);
        stream = net;
        try {
            var ssl = new SslStream(net, leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, __, ___, ____) => true // DEV ONLY
            );

            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                TargetHost = serverIp.ToString(),
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            });

            stream = ssl;
        }
        catch (Exception) {
            Notification?.Invoke(NotificationType.Warning, "SSL negotiation failed. Falling back to unencrypted connection.");

            await stream.DisposeAsync();
            socket.Dispose();

            socket = new Socket(serverIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(serverIp, serverPort));
            net = new NetworkStream(socket, ownsSocket: true);
            stream = net;
        }

        //Store it in a global so we can dispose it later
        _stream = stream;

        //Go ahead and start the receive loop so we can receive any auth error packets
        _ = Task.Run(() => ReceiveLoopAsync(stream));
    }

    public async Task LoginAsync(string name, string? password) {
        Name = name;
        Packet authPacket = new Packet {
            ClientID = name,
            Headers = new Dictionary<string, string>
            {
                { "Type", "Auth" },
                { "Platform", PlatformName }
            },
            Payload = Encoding.UTF8.GetBytes(password ?? "")
        };
        await PacketIO.SendPacketAsync(stream, authPacket);
    }
    public async Task ReceiveLoopAsync(Stream stream) {
        try {
            while (true) {
                var (status, packet) = await PacketIO.ReceivePacketAsync(stream);
                if (status == PacketStatus.Ok && packet != null) {
                    var headers = packet.Headers;
                    var text = Encoding.UTF8.GetString(packet.Payload);

var type = headers["Type"];

// First, try file-correlated pending responses (prevents collisions for concurrent transfers).
if (type == "FileStartAck"
    && headers.TryGetValue("ClientNonce", out var nonce)
    && pendingResponses.TryRemove($"FileStartAck:{nonce}", out var tcsStart)) {
    tcsStart.TrySetResult(packet);
    continue;
}
if (type == "FileEndAck"
    && headers.TryGetValue("TransferId", out var tid)
    && pendingResponses.TryRemove($"FileEndAck:{tid}", out var tcsEnd)) {
    tcsEnd.TrySetResult(packet);
    continue;
}

// Fallback: type-based correlation (AuthStatus, etc.)
if (pendingResponses.TryRemove(type, out var tcs)) {
    tcs.TrySetResult(packet);
    continue;
}

                    switch (type) {
                        case ("Message"):
                            if (text != "") {
                                MessageReceived?.Invoke(packet.ClientID, text);
                            }
                            break;
                        case ("VoiceAccepted"):
                            Notification?.Invoke(NotificationType.Info, "Voice invite accepted. Starting UDP connection.");
                            await session.StartUdpConnectionAsync();
                            break;
                        case ("VoiceInviteExpired"):
                            Notification?.Invoke(NotificationType.Warning, "Voice invite expired before it was accepted.");
                            break;
                        //Data type tells the client to update some value
                        case ("Data"):
                            var variable = headers["Var"];
                            if (variable == "id") {
                                id = int.Parse(text);
                                //Client is the default name if the user didn't input a name or there was an error
                                if (Name == "Client") {
                                    Name = $"Client {id}";
                                    IdAssigned?.Invoke(id);
                                }
                                Packet ack = new Packet {
                                    ClientID = Name,
                                    Headers = new Dictionary<string, string>
                                {
                                    { "Type", "Ack" }
                                },
                                    Payload = Array.Empty<byte>()
                                };
                                //Send an ack back to confirm we received our ID and to set our name
                                await PacketIO.SendPacketAsync(stream, ack);
                                break;
                            }


                            else if (variable == "commands") {
                                commands = JsonSerializer.Deserialize(packet.Payload, Common.CommonJsonContext.Default.StringArray) ?? Array.Empty<string>();
                                CommandsReceived?.Invoke(commands);
                                break;
                            }
                            break;

                        case ("AuthFailure"):
                            Error?.Invoke(text);
                            Disconnected?.Invoke();
                            stream.Dispose();
                            return;
                        case "AuthSuccess":
                            // Fire the notification
                            Notification?.Invoke(NotificationType.Info, "Successfully logged in.");
                            break;
                        case ("FileStart"):
                            await FileTransfer.HandleFileStartAsync(stream, packet, files, Name, defaultSaveDir);
                            break;
                        case ("FileChunk"):
                            await FileTransfer.HandleFileChunkAsync(packet, files);
                            break;
                        case ("FileEnd"):
                            await FileTransfer.HandleFileEndAsync(stream, packet, files, Name);
                            break;
                        case ("Disconnect"):
                            var message = text.Length > 0 ? text : "Remote requested UDP disconnect.";
                            Notification?.Invoke(NotificationType.Warning, message);
                            session.CloseUdpConnection();
                            break;
                        case ("ClientList"):
                            var roster = JsonSerializer.Deserialize(packet.Payload, CommonJsonContext.Default.ClientInfoArray)
                                ?? Array.Empty<ClientInfo>();
                            connectedClients = roster;
                            ClientsUpdated?.Invoke(connectedClients);
                            break;
                        case "Whisper":
                            string sender = headers.TryGetValue("Sender", out var s) ? s : packet.ClientID;
                            WhisperReceived?.Invoke(sender, text);
                            break;
                        default:
                            Error?.Invoke($"Unknown packet type received: {type}");
                            break;
                    }
                }
                else {
                    break;
                }
            }
        }
        catch (Exception ex) {
            Error?.Invoke($"Connection error: {ex.Message}");
        }
        finally {
            Disconnected?.Invoke();
            try { stream.Dispose(); } catch { }
        }
    }

    // === Client-Exclusive Messaging Functions
    public async Task SendMessageAsync(string text) {
        if (_stream is null) throw new InvalidOperationException("Not connected.");

        await PacketIO.SendPacketAsync(_stream, new Packet {
            ClientID = Name,
            Headers = new Dictionary<string, string> { { "Type", "Message" } },
            Payload = Encoding.UTF8.GetBytes(text)
        });
    }

    public async Task SendWhisperAsync(string recipientName, string message) {
        if (_stream is null) throw new InvalidOperationException("Not connected.");
        if (string.IsNullOrWhiteSpace(recipientName)) throw new ArgumentException("recipientName required.");

        var pkt = new Packet {
            ClientID = Name,
            Headers = new Dictionary<string, string>
            {
            { "Type", "Whisper" },
            { "Recipient", recipientName }
        },
            Payload = Encoding.UTF8.GetBytes(message)
        };

        await PacketIO.SendPacketAsync(_stream, pkt);
    }

    public async Task SendCommandAsync(string command) {
        if (_stream is null) throw new InvalidOperationException("Not connected.");
        await PacketIO.SendPacketAsync(_stream, new Packet {
            ClientID = Name,
            Headers = new Dictionary<string, string> { { "Type", "Command" } },
            Payload = Encoding.UTF8.GetBytes(command)
        });
    }

    public async Task StartVoiceRoom(IEnumerable<string> invitees) {
        session = new VoiceSessionManager(Notification, Name, PlatformName, stream, serverIp, serverPort);
        await session.SendVoiceInviteAsync(invitees);
    }

    public async Task LeaveVoiceRoom(string message) {
        await session.SendDisconnectAsync(message);
        session = null;
    }

    // === User Creation ===
    public async Task<bool> CreateNewUser(string username, Stream stream, string passwordHash, string userAuthCode) {
        Packet authCode = new Packet {
            ClientID = username,
            Headers = new Dictionary<string, string> { { "Type", "AuthCode" } },
            Payload = Encoding.UTF8.GetBytes(userAuthCode)
        };

        var response = await PacketIO.SendAndWaitAsync(stream, authCode, "AuthStatus", pendingResponses);
        if (response == null) return false;
        var payload = Encoding.UTF8.GetString(response.Payload);
        if (payload == "Success") {
            Packet makeNewUser = new Packet {
                ClientID = username,
                Headers = new Dictionary<string, string> { { "Type", "CreateNewUser" }, { "Name", username }, { "PasswordHash", passwordHash } },
                Payload = Array.Empty<byte>()
            };
            response = await PacketIO.SendAndWaitAsync(stream, makeNewUser, "AuthStatus", pendingResponses);
            if (response == null) return false;
            payload = Encoding.UTF8.GetString(response.Payload);

            switch (payload) {
                case "Success":
                    return true;
                case "UsernameTaken":
                    return false;
                case "Failed":
                default:
                    return false;
            }

        }
        else {
            return false;
        }
    }

    public async Task HandleClientCommandAsync(string line) {
        if (line is null) return;
        var parts = line.Split(' ');
        var cmd = parts[0];
        var args = parts.Skip(1).ToArray();
        switch (cmd) {
            case "file": {
    if (_stream is null) throw new InvalidOperationException("Not connected.");

    string? recipientName = null;
    string? localPath = null;
    string? remoteFilename = null;
    string? saveLocation = null;

    string correctUsage =
                    "Usage: --file <recipientName> <localPath> [-r remoteFilename] [-s saveLocation]\n" +
                    "   or: --file <localPath> [-r remoteFilename] [-s saveLocation]   (sends to Server)";

    if (args.Length < 1) {
        Notification?.Invoke(NotificationType.Info, correctUsage);
        return;
    }

    var positional = new List<string>();

    for (int i = 0; i < args.Length; i++) {
        try {
            if (args[i] == "-r") {
                if (i + 1 >= args.Length) {
                    Notification?.Invoke(NotificationType.Info, correctUsage);
                    return;
                }
                if (args[i + 1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || string.IsNullOrWhiteSpace(args[i + 1])) {
                    Notification?.Invoke(NotificationType.Warning, "Please enter a valid filename");
                    return;
                }
                remoteFilename = args[i + 1];
                i++;
                continue;
            }
            if (args[i] == "-s") {
                if (i + 1 >= args.Length) {
                    Notification?.Invoke(NotificationType.Info, correctUsage);
                    return;
                }
                if (args[i + 1].IndexOfAny(Path.GetInvalidPathChars()) >= 0 || string.IsNullOrWhiteSpace(args[i + 1])) {
                    Notification?.Invoke(NotificationType.Warning, "Please enter a valid path");
                    return;
                }
                saveLocation = args[i + 1];
                i++;
                continue;
            }

            positional.Add(args[i]);
        }
        catch (IndexOutOfRangeException) {
            Notification?.Invoke(NotificationType.Info, correctUsage);
            return;
        }
    }

    if (positional.Count == 1) {
        // Backward-compatible form: --file <localPath> ...
        recipientName = "Server";
        localPath = positional[0];
    }
    else if (positional.Count == 2) {
        recipientName = positional[0];
        localPath = positional[1];
    }
    else {
        Notification?.Invoke(NotificationType.Info, correctUsage);
        return;
    }

    if (string.IsNullOrWhiteSpace(Name)) {
        Notification?.Invoke(NotificationType.Error, "You must be logged in before sending files.");
        return;
    }

    await FileTransfer.SendFileAsync(_stream, localPath!, pendingResponses, Name!, recipientName!, remoteFilename, saveLocation, Notification);
    return;
}
case "voice":
                var invitees = args;
                if (invitees.Length == 0) {
                    invitees = new[] { "Server" };
                }
                await StartVoiceRoom(invitees);
                return;
            case "disconnect":
            case "dc":
                await LeaveVoiceRoom($"{Name} left the voice room");
                return;
            case "whisper":
            case "w":
                //Send the full list of args as the message instead of just the first word
                await SendWhisperAsync(args[0], string.Join(" ", args, 1, args.Length - 1));
                break;
            case "list-users":
                StringBuilder sb = new StringBuilder("Connected Users:");
                foreach (var client in connectedClients) {
                    sb.Append($"\n-{client.Name}");
                }
                Notification?.Invoke(NotificationType.Info, sb.ToString());
                break;
            default:
                Notification?.Invoke(NotificationType.Warning, "Unknown command");
                return;
        }
    }

    // === IDisposable Implementation ===
    public async ValueTask DisposeAsync() {
        try { _stream?.Dispose(); } catch { }
    }

}
