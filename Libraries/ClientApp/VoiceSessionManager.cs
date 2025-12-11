using Common;
using Common.Audio;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using static Common.Utility;

namespace ClientApp;

internal class VoiceSessionManager {
    internal Socket? udp;
    internal readonly object udpLock = new();
    internal bool playbackWarningSent = false;
    internal AudioPlayer? _player;
    internal event Action<NotificationType, string> Notification;
    internal string Name;
    internal string PlatformName;
    internal Stream stream;
    internal IPAddress serverIp;
    internal int serverPort;

    public VoiceSessionManager(Action<NotificationType, string> notification, string name,
        string platform, Stream stream, IPAddress serverIp, int serverPort) {

        this.Notification = notification;
        this.Name = name;
        this.PlatformName = platform;
        this.stream = stream;
        this.serverIp = serverIp;
        this.serverPort = serverPort;
    }

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

    public async Task UdpReceiveLoopAsync(Socket udp) {
        Notification?.Invoke(NotificationType.Info, "UDP listener started.");
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
                        if (packet.ClientID == Name) {
                            continue; // Ignore our own audio packets
                        }
                        if (PlatformName == "Windows") {
                            EnsurePlayer();
                            _player?.AddFrame(packet.Payload, 0, packet.Payload.Length);
                        }
                        else if (!playbackWarningSent) {
                            playbackWarningSent = true;
                            Notification?.Invoke(NotificationType.Warning, "Audio playback is only available on Windows.");
                        }
                        continue;
                    }

                    var payloadText = Encoding.UTF8.GetString(packet.Payload, 0, packet.Payload.Length);
                }
                catch (Exception) {
                    var message = Encoding.UTF8.GetString(buf, 0, n);
                }
            }
            catch (ObjectDisposedException) {
                Notification?.Invoke(NotificationType.Info, "UDP listener stopped.");
                break;
            }
            catch (SocketException) {
                Notification?.Invoke(NotificationType.Warning, "UDP listener stopped due to socket closure.");
                break;
            }
        }
    }

    public async Task UdpSendAsync(Socket udp, IPAddress ip, int port, Packet packet) {
        IPEndPoint remoteEndPoint = new IPEndPoint(ip, port);
        await PacketIO.SendPacketToAsyncUdp(udp, packet, remoteEndPoint);
    }

    public async Task SendVoiceInviteAsync(IEnumerable<string> invitees) {
        if (stream is null) throw new InvalidOperationException("Not connected.");

        await PacketIO.SendPacketAsync(stream, new Packet {
            ClientID = Name,
            Headers = new Dictionary<string, string> { { "Type", "VoiceInvite" } },
            Payload = JsonSerializer.SerializeToUtf8Bytes(invitees.ToArray(), Common.CommonJsonContext.Default.StringArray)
        });
    }

    internal async Task StartUdpConnectionAsync() {
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

        if (stream is null) {
            return;
        }

        await PacketIO.SendPacketAsync(stream, new Packet {
            ClientID = Name,
            Headers = new Dictionary<string, string> { { "Type", "Disconnect" } },
            Payload = Encoding.UTF8.GetBytes(reason)
        });
    }

    internal void EnsurePlayer() {
        if (_player != null || PlatformName != "Windows") {
            return;
        }

        _player = new AudioPlayer(latencyMs: 100, jitterMs: 600);
    }
}
