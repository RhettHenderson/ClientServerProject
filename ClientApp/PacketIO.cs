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
using System.Runtime.CompilerServices;

namespace Client_Server;

public class Packet
{
    public string ClientID { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public byte[] Payload { get; set; } = Array.Empty<byte>();
}

public static class PacketIO
{
    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        TypeInfoResolver = null
    };
    public static byte[] Serialize(Packet packet) => JsonSerializer.SerializeToUtf8Bytes(packet, JsonOpts);
    public static Packet Deserialize(ReadOnlySpan<byte> data) => JsonSerializer.Deserialize<Packet>(data, JsonOpts)!;

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

    public static async Task SendPacketAsync(Socket socket, Packet packet)
    {
        byte[] body = Serialize(packet);
        byte[] len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, body.Length);

        int sent = 0;
        while (sent < len.Length)
        {
            int n = await socket.SendAsync(new ReadOnlyMemory<byte>(len));
            if (n == 0) throw new IOException("Socket closed");
            sent += n;
        }
        sent = 0;
        while (sent < body.Length)
        {
            int n = await socket.SendAsync(new ReadOnlyMemory<byte>(body));
            if (n == 0) throw new IOException("Socket closed");
            sent += n;
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

    public static async Task<(PacketStatus Status, Packet Packet)> ReceivePacketAsync(Socket socket)
    {
        byte[] lenBuf = new byte[4];
        int received = await ReceiveExactlyAsync(socket, lenBuf);
        if (received == 1)
        {
            return (PacketStatus.Disconnected, null);
        }
        int len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (len < 0 || len > 16000000)
        {
            return (PacketStatus.Error, null);
        }

        //read body
        byte[] body = new byte[len];
        received = await ReceiveExactlyAsync(socket, body);
        if (received == 1)
        {
            return (PacketStatus.Disconnected, null);
        }
        var packet = Deserialize(body);
        return (PacketStatus.Ok, packet);
    }

    //Returns 1 for error
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
    //Returns 1 for error
    public static async Task<int> ReceiveExactlyAsync(Socket socket, Memory<byte> buffer)
    {
        int received = 0;
        int r;
        while (received < buffer.Length)
        {
            try
            {
                r = await socket.ReceiveAsync(buffer.Slice(received));
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
