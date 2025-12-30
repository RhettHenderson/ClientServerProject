using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Common;

public static class PacketIO {
    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) {
        WriteIndented = false,
        TypeInfoResolver = null
    };

    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<Packet> PacketInfo = CommonJsonContext.Default.Packet;
    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<Dictionary<string, string>> DictInfo = CommonJsonContext.Default.DictionaryStringString;
    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<string[]> StringArrayInfo = CommonJsonContext.Default.StringArray;

    // Serialize all writes per Stream to prevent "another write operation is in progress" exceptions.
    private static readonly ConditionalWeakTable<Stream, SemaphoreSlim> _streamWriteLocks = new();

    public static byte[] Serialize(Packet packet) => JsonSerializer.SerializeToUtf8Bytes(packet, PacketInfo);
    public static byte[] SerializeForUdp(Packet packet) {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        bw.Write((byte)0x55); //Magic byte 'U' for UDP

        bw.Write((ushort)packet.ClientID.Length);
        bw.Write(Encoding.UTF8.GetBytes(packet.ClientID));

        bw.Write((ushort)packet.Headers.Count);

        foreach (var kv in packet.Headers) {
            bw.Write((ushort)kv.Key.Length);
            bw.Write(Encoding.UTF8.GetBytes(kv.Key));
            bw.Write((ushort)kv.Value.Length);
            bw.Write(Encoding.UTF8.GetBytes(kv.Value));
        }

        bw.Write((ushort)packet.Payload.Length);
        bw.Write(packet.Payload);

        bw.Flush();
        return ms.ToArray();
    }
    public static Packet Deserialize(ReadOnlySpan<byte> data) => JsonSerializer.Deserialize<Packet>(data, PacketInfo)!;
    public static Packet DeserializeForUdp(ReadOnlySpan<byte> data) {
        var packet = new Packet();
        var br = new BinaryReader(new MemoryStream(data.ToArray()));

        byte marker = br.ReadByte();
        if (marker != 0x55) {
            throw new InvalidDataException("Packet missing magic byte header. This packet is either corrupted or came from somewhere that is not the client.");
        }

        ushort idLen = br.ReadUInt16();
        packet.ClientID = Encoding.UTF8.GetString(br.ReadBytes(idLen));

        ushort headerCount = br.ReadUInt16();
        for (int i = 0; i < headerCount; i++) {
            ushort keyLen = br.ReadUInt16();
            string key = Encoding.UTF8.GetString(br.ReadBytes(keyLen));

            ushort valueLen = br.ReadUInt16();
            string value = Encoding.UTF8.GetString(br.ReadBytes(valueLen));

            packet.Headers[key] = value;
        }

        ushort payloadLen = br.ReadUInt16();
        packet.Payload = br.ReadBytes(payloadLen);

        return packet;

    }
    public static void SendPacket(Socket socket, Packet packet) {
        var body = Serialize(packet);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, body.Length);

        socket.Send(len);

        int sent = 0;
        while (sent < body.Length) {
            sent += socket.Send(body, sent, body.Length - sent, SocketFlags.None);
        }
    }

    public static async Task SendPacketToAsyncUdp(Socket socket, Packet packet, EndPoint destination) {
        byte[] dgram = SerializeForUdp(packet);
        await socket.SendToAsync(dgram, SocketFlags.None, destination);
    }

    public static async Task SendPacketAsyncUdp(Socket socket, Packet packet) {
        if (!socket.Connected) {
            throw new InvalidOperationException("Socket must be connected to an endpoint to use SendPacketAsyncUdp. " +
                "Call socket.Connect(endpoint) first or use SendPacketToAsyncUdp() instead");
        }
        byte[] dgram = SerializeForUdp(packet);
        await socket.SendAsync(dgram, SocketFlags.None);
    }
    public static async Task SendPacketAsync(Socket socket, Packet packet) {
        byte[] body = Serialize(packet);
        byte[] len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, body.Length);

        int sent = 0;
        while (sent < len.Length) {
            int n = await socket.SendAsync(new ReadOnlyMemory<byte>(len, sent, len.Length - sent), SocketFlags.None);
            if (n == 0) throw new IOException("Socket closed");
            sent += n;
        }
        sent = 0;
        while (sent < body.Length) {
            int n = await socket.SendAsync(new ReadOnlyMemory<byte>(body, sent, body.Length - sent), SocketFlags.None);
            if (n == 0) throw new IOException("Socket closed");
            sent += n;
        }
    }

    public static async Task SendPacketAsync(Stream stream, Packet packet) {
        var gate = await GetWriteLock(stream);
        await gate.WaitAsync().ConfigureAwait(false);
        try {
            byte[] body = Serialize(packet);
            byte[] len = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, body.Length);

            await stream.WriteAsync(len);
            await stream.WriteAsync(body);
            try { await stream.FlushAsync(); } catch { /* ignore */}
        }
        finally {
            gate.Release();
        }

    }
    public static async Task<(PacketStatus status, Packet packet)> ReceivePacketAsync(Stream stream) {
        byte[] lenBuf = new byte[4];
        var received = await ReceiveExactlyAsync(stream, lenBuf);
        if (received == PacketStatus.Disconnected) {
            return (PacketStatus.Disconnected, null);
        }
        int len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (len < 0 || len > 16000000) {
            return (PacketStatus.Error, null);
        }

        //read body
        byte[] body = new byte[len];
        received = await ReceiveExactlyAsync(stream, body);
        if (received == PacketStatus.Disconnected) {
            return (PacketStatus.Disconnected, null);
        }
        var packet = Deserialize(body);
        return (PacketStatus.Ok, packet);
    }

    public static async Task<PacketStatus> ReceiveExactlyAsync(Stream stream, Memory<byte> buffer) {
        int received = 0;
        int r;
        while (received < buffer.Length) {
            try {
                r = await stream.ReadAsync(buffer.Slice(received));
            }
            catch {
                return PacketStatus.Disconnected;
            }
            if (r == 0) return PacketStatus.Disconnected;
            received += r;
        }
        return 0;
    }

    public static async Task<Packet?> SendAndWaitAsync(Stream stream, Packet packet, string expectedType, ConcurrentDictionary<string, TaskCompletionSource<Packet>> pendingResponses) {
        // Backward-compatible overload: expectedType is used as the pendingResponses key.
        return await SendAndWaitAsync(stream, packet, expectedKey: expectedType, pendingResponses: pendingResponses, timeout: TimeSpan.FromSeconds(10));
    }

    public static async Task<Packet?> SendAndWaitAsync(
        Stream stream,
        Packet packet,
        string expectedKey,
        ConcurrentDictionary<string, TaskCompletionSource<Packet>> pendingResponses,
        TimeSpan? timeout
    ) {
        var tcs = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingResponses[expectedKey] = tcs;

        await SendPacketAsync(stream, packet);

        using var cts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (cts.Token.Register(() => tcs.TrySetCanceled())) {
            try {
                return await tcs.Task.ConfigureAwait(false);
            }
            catch (TaskCanceledException) {
                pendingResponses.TryRemove(expectedKey, out _);
                return null;
            }
        }
    }

    private static async Task<SemaphoreSlim> GetWriteLock(Stream stream) => _streamWriteLocks.GetValue(stream, _ => new SemaphoreSlim(1, 1));

}
