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
using System.Diagnostics;

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

    public static PacketStatus ReceivePacket(Socket socket, ref Packet packet)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        int received = ReceiveExactly(socket, lenBuf);
        int len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (received == 1)
        {
            return PacketStatus.Disconnected;
        }
        else if (len < 0 || len > 16000000)
        {
            return PacketStatus.Error;
        }
        //read body
        byte[] body = new byte[len];
        received = ReceiveExactly(socket, body);
        if (received == 1)
        {
            return PacketStatus.Disconnected;
        }
        packet = Deserialize(body);
        return PacketStatus.Ok;

    }

    static int ReceiveExactly(Socket socket, Span<byte> buffer)
    {
        int received = 0;
        int r;
        while (received < buffer.Length)
        {
            try
            {
                r = socket.Receive(buffer.Slice(received));
            }
            catch (SocketException)
            {
                return 1;
            }
            if (r == 0) return 1;
            received += r;
        }
        return 0;
    }
}

public enum PacketStatus
{
    Ok,
    Disconnected,
    Error
}
