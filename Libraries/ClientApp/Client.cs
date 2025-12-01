using Common;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Client_Server;

public class Client : IAsyncDisposable {
    // === Static Fields ===
    private static readonly string defaultSaveDir = @"C:\Users\rhett\Documents\downloads";
    private static readonly ConcurrentDictionary<string, FileReceiveState> files = new(); // Current downloads in progress

    // === Microphone Fields ===
    private readonly Pcm16Player _player = new Pcm16Player(latencyMs: 100, jitterMs: 600);

    // === Instance Fields ===
    private int id = -1;
    private bool userExists = false;
    private string[] commands = Array.Empty<string>();
    private IPAddress? serverIp;
    private int serverPort;
    private Socket? udp;
    private readonly object udpLock = new();

    // === Public Properties ===
    public string? Name { get; private set; }
    public Stream? _stream { get; set; }
    public IPEndPoint? LocalEndPoint { get; private set; }
    // === Dictionaries / State ===
    public ConcurrentDictionary<string, TaskCompletionSource<Packet>> pendingResponses { get; } = new();

    // === Events (UI / CLI) ===
    public event Action<string, string>? MessageReceived;      // (from, text)
    public event Action<string, string>? WhisperReceived;      // (from, text)
    public event Action<string[]>? CommandsReceived;
    public event Action<int>? IdAssigned;
    public event Action? Disconnected;
    public event Action<string>? Error;
    public event Action<NotificationType, string>? Notification;

    //--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

    // === Main Client Logic ===
    public async Task ConnectAsync(string host, int port, string name, string? passwordHash, bool createUser = false, Func<Task<string?>>? requestAuthCode = null, string authCode = "") {
        var ip = IPAddress.TryParse(host, out var ipAddr) ? ipAddr : IPAddress.Loopback;
        serverIp = ip;
        serverPort = port;
        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(ip, port));
        LocalEndPoint = (IPEndPoint?)socket.LocalEndPoint;
        var net = new NetworkStream(socket, ownsSocket: true);
        Stream stream = net;

        try {
            var ssl = new SslStream(net, leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, __, ___, ____) => true // DEV ONLY
            );

            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            });

            stream = ssl;
        }
        catch (AuthenticationException) {
            Notification?.Invoke(NotificationType.Warning, "SSL negotiation failed. Falling back to unencrypted connection.");

            try { stream.Dispose(); } catch { }
            try { socket.Dispose(); } catch { }

            socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(ip, port));
            net = new NetworkStream(socket, ownsSocket: true);
            stream = net;
        }
        catch (IOException) {
            Notification?.Invoke(NotificationType.Warning, "SSL negotiation failed due to IO error. Falling back to unencrypted connection.");

            try { stream.Dispose(); } catch { }
            try { socket.Dispose(); } catch { }

            socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(ip, port));
            net = new NetworkStream(socket, ownsSocket: true);
            stream = net;
        }

        //Store it in a global so we can dispose it later
        _stream = stream;
        Name = name;

        _ = Task.Run(() => ReceiveLoopAsync(stream));

        if (createUser) {
            Packet authCodeRequest = new Packet {
                ClientID = name,
                Headers = new Dictionary<string, string> { { "Type", "AuthCodeRequest" } },
                Payload = Array.Empty<byte>()
            };
            await PacketIO.SendPacketAsync(stream, authCodeRequest);

            //Add an if statement here and a way to show an error in the UI later
            await CreateNewUser(name, stream, passwordHash, authCode);
        }
        else {
            Packet authPacket = new Packet {
                ClientID = name,
                Headers = new Dictionary<string, string>
                {
                    { "Type", "Auth" }
                },
                Payload = Encoding.UTF8.GetBytes(passwordHash ?? "")
            };
            await PacketIO.SendPacketAsync(stream, authPacket);
        }
    }
    public async Task ReceiveLoopAsync(Stream stream) {
        try {
            while (true) {
                var (status, packet) = await PacketIO.ReceivePacketAsync(stream);
                if (status == PacketStatus.Ok && packet != null) {
                    var headers = packet.Headers;
                    var text = Encoding.UTF8.GetString(packet.Payload);
                    var type = headers["Type"];

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
                            await StartUdpConnectionAsync();
                            break;
                        case ("VoiceInviteExpired"):
                            Notification?.Invoke(NotificationType.Warning, "Voice invite expired before it was accepted.");
                            break;
                        case ("Whisper"):
                            WhisperReceived?.Invoke(packet.ClientID, text);
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
                        case ("FileStart"):
                            await PacketIO.HandleFileStartAsync(stream, packet, files, Name, defaultSaveDir);
                            //Event for file received
                            break;
                        case ("FileChunk"):
                            await PacketIO.HandleFileChunkAsync(packet, files);
                            //Event for file chunk
                            break;
                        case ("FileEnd"):
                            await PacketIO.HandleFileEndAsync(stream, packet, files, Name);
                            //Event for file end
                            break;
                        case ("Disconnect"):
                            var message = text.Length > 0 ? text : "Remote requested UDP disconnect.";
                            Notification?.Invoke(NotificationType.Warning, message);
                            CloseUdpConnection();
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

    public async Task UdpReceiveLoopAsync(Socket udp) {
        MessageReceived?.Invoke("System", "UDP listener started.");
        var buf = new byte[4096];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (true) {
            try {
                var result = await udp.ReceiveFromAsync(buf, SocketFlags.None, remote);
                var n = result.ReceivedBytes;
                var from = result.RemoteEndPoint?.ToString() ?? "Unknown";

                try {
                    var packet = PacketIO.DeserializeForUdp(buf.AsSpan(0, n));
                    if (packet.Headers.TryGetValue("Type", out var type) && type == "Audio") {
                        _player.AddFrame(packet.Payload, 0, packet.Payload.Length);
                        continue;
                    }

                    var payloadText = Encoding.UTF8.GetString(packet.Payload, 0, packet.Payload.Length);
                    MessageReceived?.Invoke($"UDP Packet from {from}: ", payloadText);
                }
                catch (Exception) {
                    var message = Encoding.UTF8.GetString(buf, 0, n);
                    MessageReceived?.Invoke($"UDP Message from {from}: ", message);
                }
            }
            catch (ObjectDisposedException) {
                MessageReceived?.Invoke("System", "UDP listener stopped.");
                break;
            }
            catch (SocketException) {
                MessageReceived?.Invoke("System", "UDP listener stopped due to socket closure.");
                break;
            }
        }
    }

    public async Task UdpSendAsync(Socket udp, IPAddress ip, int port, Packet packet) {
        MessageReceived?.Invoke("System", "Sending UDP packet.");
        IPEndPoint remoteEndPoint = new IPEndPoint(ip, port);
        await PacketIO.SendPacketToAsyncUdp(udp, packet, remoteEndPoint);
    }

    public async Task SendVoiceInviteAsync() {
        if (_stream is null) throw new InvalidOperationException("Not connected.");

        await PacketIO.SendPacketAsync(_stream, new Packet {
            ClientID = Name,
            Headers = new Dictionary<string, string> { { "Type", "VoiceInvite" } },
            Payload = Array.Empty<byte>()
        });
    }

    private async Task StartUdpConnectionAsync() {
        if (udp != null || serverIp is null) {
            return;
        }

        udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Any, 0));

        _ = Task.Run(() => UdpReceiveLoopAsync(udp));

        Packet udpPacket = new Packet {
            ClientID = Name,
            Headers = new Dictionary<string, string>
            {
                {"Protocol", "UDP" },
                {"Type", "Message" }
            },
            Payload = Encoding.UTF8.GetBytes("Hello via UDP!")
        };

        await StartListening(udp, serverIp, serverPort);
        await UdpSendAsync(udp, serverIp, serverPort, udpPacket);
    }

    public void CloseUdpConnection() {
        Socket? socketToClose = null;
        lock (udpLock) {
            if (udp != null) {
                socketToClose = udp;
                udp = null;
            }
        }

        if (socketToClose != null) {
            try { socketToClose.Close(); } catch { }
            Notification?.Invoke(NotificationType.Info, "Closed local UDP connection.");
        }
    }

    public async Task SendDisconnectAsync(string reason) {
        CloseUdpConnection();

        if (_stream is null) {
            return;
        }

        await PacketIO.SendPacketAsync(_stream, new Packet {
            ClientID = Name,
            Headers = new Dictionary<string, string> { { "Type", "Disconnect" } },
            Payload = Encoding.UTF8.GetBytes(reason)
        });
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

    public async Task SendCommandAsync(string command) {
        if (_stream is null) throw new InvalidOperationException("Not connected.");
        await PacketIO.SendPacketAsync(_stream, new Packet {
            ClientID = Name,
            Headers = new Dictionary<string, string> { { "Type", "Command" } },
            Payload = Encoding.UTF8.GetBytes(command)
        });
    }

    // === User Creation ===
    public async Task<bool> CreateNewUser(string username, Stream stream, string passwordHash, string userAuthCode) {
        Packet authCode = new Packet {
            ClientID = username,
            Headers = new Dictionary<string, string> { { "Type", "AuthCode" } },
            Payload = Encoding.UTF8.GetBytes(userAuthCode)
        };

        Packet response = await PacketIO.SendAndWaitAsync(stream, authCode, "AuthStatus", pendingResponses);
        var payload = Encoding.UTF8.GetString(response.Payload);
        if (payload == "Success") {
            Packet makeNewUser = new Packet {
                ClientID = username,
                Headers = new Dictionary<string, string> { { "Type", "CreateNewUser" }, { "Name", username }, { "PasswordHash", passwordHash } },
                Payload = Array.Empty<byte>()
            };
            response = await PacketIO.SendAndWaitAsync(stream, makeNewUser, "AuthStatus", pendingResponses);
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

    // === Microphone Capturing ===
    public async Task StartListening(Socket udp, IPAddress ip, int port) {
        Notification?.Invoke(NotificationType.Info, "Starting microphone capture...");
        var rec = new MicRecorder(frameMs: 10);
        rec.Start();
        var remote = new IPEndPoint(ip, port);

        // === Sequencing ===
        uint seq = 0;
        int timestampSamples = 0;
        const int bytesPerSample = 2;
        const int samplesPer10ms = 480;
        const int maxFrameBytes = samplesPer10ms * bytesPerSample;

        var senderThread = new Thread(() => {
            while (true) {
                if (!rec.TryDequeue(out var frame) || frame is null) {
                    Thread.Sleep(1);
                    continue;
                }

                // 2) Split any frame larger than 960 bytes into 10 ms slices
                int offset = 0;
                while (offset < frame.Length) {
                    int take = Math.Min(maxFrameBytes, frame.Length - offset);
                    var slice = new byte[take];
                    Buffer.BlockCopy(frame, offset, slice, 0, take);

                    // 3) Add minimal sequencing metadata (headers) for VoIP
                    var audio = new Packet {
                        ClientID = Name, // your ID string
                        Headers = new Dictionary<string, string>
                        {
                            { "Type", "Audio" },
                            { "Protocol", "UDP" },
                            { "Seq", seq.ToString() },
                            { "Ts",  timestampSamples.ToString() }, // samples @ 48k
                            { "Fmt", "PCM16_48k_Mono" }
                        },
                        Payload = slice
                    };
                    PacketIO.SendPacketToAsyncUdp(udp, audio, remote);

                    // advance counters
                    seq++;
                    timestampSamples += take / bytesPerSample;

                    offset += take;
                }
            }
        }) { IsBackground = true, Name = "VoIP MicSender" };
        senderThread.Start();
    }

    // === IDisposable Implementation ===
    public async ValueTask DisposeAsync() {
        try { _stream?.Dispose(); } catch { }
        try { _player?.Dispose(); } catch { }
    }
}
