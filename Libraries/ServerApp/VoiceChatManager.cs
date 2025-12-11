using Common;
using Common.Audio;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using static Common.Utility;

namespace Client_Server;

internal class VoiceChatManager {
    internal Socket? udp;
    internal readonly object udpLock = new();
    internal int? pendingVoiceClientId;
    internal CancellationTokenSource? voiceInviteCts;
    internal readonly ConcurrentDictionary<int, IPEndPoint> udpClients = new();
    internal readonly ConcurrentDictionary<int, byte> voiceParticipants = new();
    internal readonly string serverPlatform = Utility.GetPlatformName();
    internal AudioPlayer? _player;
    internal MicRecorder? _mic;
    internal Thread? micSenderThread;
    internal bool disableServerMic;
    internal readonly IPAddress? listeningIp;
    internal readonly int listeningPort;
    internal readonly ConcurrentDictionary<string, int> names;
    internal readonly ConcurrentDictionary<int, Server.Conn> clients;
    internal readonly Action<NotificationType, string>? Notification;
    internal readonly string Name;

    public VoiceChatManager(IPAddress listeningIP, int listeningPort,
        ConcurrentDictionary<string, int> names, ConcurrentDictionary<int, Server.Conn> clients,
        Action<NotificationType, string> notification, string name) {

        this.listeningIp = listeningIP;
        this.listeningPort = listeningPort;
        this.names = names;
        this.clients = clients;
        Notification = notification;
        Name = name;
    }

    internal void StartUdpListenerIfNeeded() {
        if (udp != null || listeningIp is null) {
            return;
        }

        udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(listeningIp, listeningPort));
        _ = Task.Run(() => UdpReceiveLoopAsync(udp));
    }

    public async Task UdpReceiveLoopAsync(Socket udp) {
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

    internal int ResolveClientId(string clientId) {
        if (string.Equals(clientId, Name, StringComparison.OrdinalIgnoreCase)) {
            return 0;
        }

        if (names.TryGetValue(clientId, out var id)) {
            return id;
        }

        return -1;
    }

    internal async Task ForwardAudioAsync(Socket udp, int senderId, Packet packet) {
        var recipients = udpClients
            .Where(kv => kv.Key != senderId && voiceParticipants.ContainsKey(kv.Key))
            .Select(kv => kv.Value)
            .ToArray();

        foreach (var endpoint in recipients) {
            await PacketIO.SendPacketToAsyncUdp(udp, packet, endpoint);
        }
    }

    internal void StartServerMicrophone() {
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

    internal void StopServerMicrophone() {
        disableServerMic = false;
        _mic?.Dispose();
        _mic = null;
        try { micSenderThread?.Join(200); } catch { }
        micSenderThread = null;
    }

    internal void EnsureServerPlayer() {
        if (_player != null || serverPlatform != "Windows") {
            return;
        }

        _player = new AudioPlayer(latencyMs: 100, jitterMs: 600);
    }

    internal void CloseUdpConnection() {
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

    internal async Task SendVoiceInviteWarningAsync(int clientId, string message) {
        if (clients.TryGetValue(clientId, out var conn)) {
            var packet = new Packet {
                ClientID = "Server",
                Headers = new Dictionary<string, string> { { "Type", "Message" } },
                Payload = Encoding.UTF8.GetBytes(message)
            };

            await PacketIO.SendPacketAsync(conn.io, packet);
        }
    }

    internal async Task AcceptInvite() {
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
            disableServerMic = Server.IsSameMachine(inviteConn.socket);
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
    }
}
