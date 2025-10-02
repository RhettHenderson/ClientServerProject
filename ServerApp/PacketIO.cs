using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Buffers.Binary;

namespace Client_Server;

public class Packet
{
    public string ClientID { get;  set; }
    public Dictionary<string, string> Headers { get;  set; } = new();
    public byte[] Payload { get; set; } = Array.Empty<byte>();
}

public static class PacketIO
{
    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        TypeInfoResolver = null
    };
    static byte[] Serialize(Packet packet) => JsonSerializer.SerializeToUtf8Bytes(packet, JsonOpts);
    static Packet Deserialize(ReadOnlySpan<byte> data) => JsonSerializer.Deserialize<Packet>(data, JsonOpts)!;

    public static void SendPacket(Socket socket, Packet packet)
    {
        var body = Serialize(packet);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, body.Length);

        socket.Send(len);

        int sent = 0;
        while (sent < body.Length)
        {
            sent += socket.Send(body, sent, body.Length - sent, SocketFlags.None);
        }
    }

    public static Packet ReceivePacket(Socket socket)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        ReceiveExactly(socket, lenBuf);
        int len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (len < 0 || len > 16000000)
        {
            throw new InvalidOperationException($"Invalid packet length: {len}");
        }

        //read body
        byte[] body = new byte[len];
        ReceiveExactly(socket, body);
        return Deserialize(body);
    }

    static void ReceiveExactly(Socket socket, Span<byte> buffer)
    {
        int received = 0;
        while (received < buffer.Length)
        {
            int r = socket.Receive(buffer.Slice(received));
            if (r == 0) throw new EndOfStreamException();
            received += r;
        }
    }
}
