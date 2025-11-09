using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Common;

public class Packet
{
    public string ClientID { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public byte[] Payload { get; set; } = Array.Empty<byte>();
}

public static class PositionCodec
{
    //Payload is 12 bytes: x, y, z
    public static byte[] Encode(float x, float y, float z)
    {
        byte[] buf = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(0, 4), x);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(4, 4), y);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(8, 4), z);
        return buf;
    }

    public static (float x, float y, float z) Decode(ReadOnlySpan<byte> buf)
    {
        float x = BinaryPrimitives.ReadSingleLittleEndian(buf.Slice(0, 4));
        float y = BinaryPrimitives.ReadSingleLittleEndian(buf.Slice(4, 4));
        float z = BinaryPrimitives.ReadSingleLittleEndian(buf.Slice(8, 4));
        return (x, y, z);
    }
}

public static class PacketIO
{
    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        TypeInfoResolver = null
    };

    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<Packet> PacketInfo = CommonJsonContext.Default.Packet;
    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<Dictionary<string, string>> DictInfo = CommonJsonContext.Default.DictionaryStringString;
    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<string[]> StringArrayInfo = CommonJsonContext.Default.StringArray;

    public static byte[] Serialize(Packet packet) => JsonSerializer.SerializeToUtf8Bytes(packet, PacketInfo);
    public static byte[] SerializeForUdp(Packet packet)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        bw.Write((byte)0x55); //Magic byte 'U' for UDP

        bw.Write((ushort)packet.ClientID.Length);
        bw.Write(Encoding.UTF8.GetBytes(packet.ClientID));

        bw.Write((ushort)packet.Headers.Count);

        foreach (var kv in packet.Headers)
        {
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
    public static Packet DeserializeForUdp(ReadOnlySpan<byte> data)
    {
        var packet = new Packet();
        var br = new BinaryReader(new MemoryStream(data.ToArray()));

        byte marker = br.ReadByte();
        if (marker != 0x55)
        {
            throw new InvalidDataException("Packet missing magic byte header. This packet is either corrupted or came from somewhere that is not the client.");
        }

        ushort idLen = br.ReadUInt16();
        packet.ClientID = Encoding.UTF8.GetString(br.ReadBytes(idLen));

        ushort headerCount = br.ReadUInt16();
        for (int i = 0; i < headerCount; i++)
        {
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

    public static async Task SendPacketToAsyncUdp(Socket socket, Packet packet, EndPoint destination)
    {
        byte[] dgram = SerializeForUdp(packet);
        await socket.SendToAsync(dgram, SocketFlags.None, destination);
    }

    public static async Task SendPacketAsyncUdp(Socket socket, Packet packet)
    {
        byte[] dgram = SerializeForUdp(packet);
        await socket.SendAsync(dgram, SocketFlags.None);
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

    public static async Task SendPacketAsync(Stream stream, Packet packet)
    {
        byte[] body = Serialize(packet);
        byte[] len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, body.Length);

        await stream.WriteAsync(len);
        await stream.WriteAsync(body);
    }
    public static async Task<(PacketStatus status, Packet packet)> ReceivePacketAsync(Stream stream)
    {
        byte[] lenBuf = new byte[4];
        var received = await ReceiveExactlyAsync(stream, lenBuf);
        if (received == PacketStatus.Disconnected)
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
        received = await ReceiveExactlyAsync(stream, body);
        if (received == PacketStatus.Disconnected)
        {
            return (PacketStatus.Disconnected, null);
        }
        var packet = Deserialize(body);
        return (PacketStatus.Ok, packet);
    }

    public static async Task<PacketStatus> ReceiveExactlyAsync(Stream stream, Memory<byte> buffer)
    {
        int received = 0;
        int r;
        while (received < buffer.Length)
        {
            try
            {
                r = await stream.ReadAsync(buffer.Slice(received));
            }
            catch
            {
                return PacketStatus.Disconnected;
            }
            if (r == 0) return PacketStatus.Error;
            received += r;
        }
        return 0;
    }

    public static async Task<Packet> SendAndWaitAsync(Stream stream, Packet packet, string expectedType, ConcurrentDictionary<string, TaskCompletionSource<Packet>> pendingResponses)
    {
        var tcs = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingResponses[expectedType] = tcs;

        await SendPacketAsync(stream, packet);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (cts.Token.Register(() => tcs.TrySetCanceled()))
        {
            return await tcs.Task;
        }
    }

    public static async Task<bool> SendFileAsync(
        Stream stream, 
        string localPath,
        ConcurrentDictionary<string, TaskCompletionSource<Packet>> pendingResponses,
        string? remoteFilename = null, 
        string? saveLocation = null, 
        int chunkSize = 64 * 1024, 
        Action<NotificationType, string>? Notification = null, 
        Action<string>? Error = null)
    {
        if (!System.IO.File.Exists(localPath))
        {
            return false;
        }

        //Instead of hardcoding the default location here, we let the serve choose it
        //saveLocation ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "uploads");
        remoteFilename ??= Path.GetFileName(localPath);
        saveLocation ??= "=default";

        long length = new FileInfo(localPath).Length;

        //Order of file transfer:
        //File start packet
        //File chunk packet (1 or more)
        //File end packet

        var start = new Packet
        {
            ClientID = "Server",
            Headers = new Dictionary<string, string> {
                    { "Type", "FileStart" },
                    { "Name", remoteFilename },
                    { "Length", length.ToString() },
                    { "ChunkSize", chunkSize.ToString() },
                    { "SaveLocation", saveLocation}
                },
            Payload = Array.Empty<byte>()
        };

        Notification?.Invoke(NotificationType.Info, $"Starting file transfer: {remoteFilename} ({length} bytes)");
        Console.WriteLine("Starting file transfer");
        var startAck = await PacketIO.SendAndWaitAsync(stream, start, "FileStartAck", pendingResponses);
        if (startAck == null)
        {
            return false;
        }
        if (startAck.Headers["Status"] == "Exists")
        {
            return false;
        }

        startAck.Headers.TryGetValue("FileKey", out var key);

        //Send file chunks
        int index = 0;
        int totalChunks = (int)(length + chunkSize - 1) / chunkSize;

        await using (var fs = System.IO.File.OpenRead(localPath))
        {
            byte[] buffer = new byte[chunkSize];
            int read;
            Notification?.Invoke(NotificationType.Info, "Beginning file chunk transfers...");
            Console.WriteLine("Beginning file chunk transfers");
            while ((read = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                var payload = (read == buffer.Length) ? buffer : buffer.AsSpan(0, read).ToArray();
                var chunk = new Packet
                {
                    ClientID = "Server",
                    Headers = new Dictionary<string, string>
                    {
                        { "Type", "FileChunk" },
                        { "Name", remoteFilename },
                        { "Index", index.ToString() },
                        { "SaveLocation", saveLocation },
                        { "FileKey", key },
                    },
                    Payload = payload
                };

                await PacketIO.SendPacketAsync(stream, chunk);
                index++;
            }
        }

        //Send file end
        var end = new Packet
        {
            ClientID = "Server",
            Headers = new Dictionary<string, string>
            {
                { "Type", "FileEnd" },
                { "Name", remoteFilename },
                { "TotalChunks", totalChunks.ToString() },
                { "FileKey", key }
            },
            Payload = Array.Empty<byte>()
        };
        Notification?.Invoke(NotificationType.Info, "Sending file end packet...");
        Console.WriteLine("Sending file end packet");
        var endAck = await PacketIO.SendAndWaitAsync(stream, end, "FileEndAck", pendingResponses);
        if (endAck != null)
        {
            if (endAck.Headers["Status"] == "Error")
            {
                Error?.Invoke("File end ack error. File transfer failed.");
                return false;
            }
            Notification?.Invoke(NotificationType.Info, "File transfer complete.");
            return true;
        }
        else
        {
            Error?.Invoke("File end ack never received. File transfer failed.");
            return false;
        }


    }

    public static async Task HandleFileStartAsync(Stream stream, Packet packet, ConcurrentDictionary<string, FileReceiveState> files, string Name, string defaultSaveDir)
    {
        var headers = packet.Headers;
        var name = headers["Name"];
        var length = long.Parse(headers["Length"]);
        var chunkSize = int.Parse(headers["ChunkSize"]);
        var saveLocation = headers["SaveLocation"];

        var saveDir = Utility.ResolveSaveDir(saveLocation, defaultSaveDir);
        Directory.CreateDirectory(saveDir);
        var fullPath = Path.Combine(saveDir, name);

        var reply = new Packet
        {
            ClientID = Name,
            Headers = new Dictionary<string, string> { { "Type", "FileStartAck" }, { "Status", "Ok" }, { "FileKey", fullPath } },
            Payload = Array.Empty<byte>()
        };
        if (File.Exists(fullPath) || files.ContainsKey(fullPath))
        {
            Console.WriteLine("File exists.");
            reply.Headers["Status"] = "Exists";
            await PacketIO.SendPacketAsync(stream, reply);
            return;
        }
        var state = new FileReceiveState
        {
            Name = fullPath,
            ExpectedLength = length,
            ExpectedChunks = (int)((length + chunkSize - 1) / chunkSize),
            Stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true),
        };
        files[fullPath] = state;
        state.Stream.Position = 0;
        reply.Headers["Status"] = "OK";
        Console.WriteLine("Sending FileStartAck");
        await PacketIO.SendPacketAsync(stream, reply);
    }

    public static async Task HandleFileChunkAsync(Packet packet, ConcurrentDictionary<string, FileReceiveState> files)
    {
        var headers = packet.Headers;
        var name = headers["Name"];
        var index = int.Parse(headers["Index"]);
        var payload = packet.Payload ?? Array.Empty<byte>();

        headers.TryGetValue("FileKey", out var key);

        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        //Ignore if name doesn't match the value we have in our dictionary
        if (!files.TryGetValue(key, out var state) || state.Stream == null)
        {
            return;
        }

        //Append the payload
        await state.Stream.WriteAsync(payload, 0, payload.Length);
        state.Received += payload.Length;
    }

    public static async Task HandleFileEndAsync(Stream stream, Packet packet, ConcurrentDictionary<string, FileReceiveState> files, string Name)
    {
        Console.WriteLine("Beginning file end processing.");
        var headers = packet.Headers;
        var name = headers["Name"];
        var totalChunks = int.Parse(headers["TotalChunks"]);

        headers.TryGetValue("FileKey", out var key);
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        var reply = new Packet


        {
            ClientID = Name,
            Headers = new Dictionary<string, string> { { "Type", "FileEndAck" } },
            Payload = Array.Empty<byte>()
        };

        if (!files.TryGetValue(key, out var state) || state.Stream == null)
        {
            reply.Headers["Status"] = "Error";
        }
        else
        {
            try
            {
                await state.Stream.FlushAsync();
                state.Stream.Close();
                state.Stream.Dispose();


                if (state.Received != state.ExpectedLength)
                {
                    reply.Headers["Status"] = "LengthMismatch";
                }
                else if (totalChunks != state.ExpectedChunks)
                {
                    reply.Headers["Status"] = "LengthMismatch";
                }
                else
                {
                    reply.Headers["Status"] = "Success";
                }
            }
            catch
            {
                reply.Headers["Status"] = "Error";
            }
            finally
            {
                files.TryRemove(key, out _);

            }
        }
        Console.WriteLine("Sending FileEndAck");
        await PacketIO.SendPacketAsync(stream, reply);
    }
}

public class FileReceiveState
{
    public string Name = "";
    public long ExpectedLength;
    public long Received;
    public int ExpectedChunks;
    public FileStream? Stream;
}

public enum PacketStatus
{
    Ok,
    Disconnected,
    Error
}

public enum NotificationType { Info, Warning, Error }

public static class Utility
{
    public static string SHA256Hash(string input)
    {
        SHA256 hasher = SHA256.Create();
        byte[] hashValue = hasher.ComputeHash(Encoding.UTF8.GetBytes(input));
        StringBuilder sb = new StringBuilder();
        foreach (byte b in hashValue)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }
    public static int GenerateAuthCode(ref int outCode, int digits = 6)
    {
        //min is 10^(digits-1), max is 10^digits
        //since rand.Next is exclusive on the upper bound, we let max go up to 10^digits without subtracting the 1
        int min = (int)Math.Pow(10, digits - 1);
        int max = (int)Math.Pow(10, digits);
        byte[] bytes = new byte[4];
        int value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = BitConverter.ToInt32(bytes, 0) & int.MaxValue; // makes it non-negative
            value = (value % (max - min + 1)) + min; // scale to desired range
        }
        while (value > max || value < min);

        outCode = value;
        return value;
    }
    public static string ResolveSaveDir(string input, string defaultSaveDir)
    {
        // 1) Treat null/blank or "=default" (case-insensitive) as the default
        if (string.IsNullOrWhiteSpace(input) ||
            string.Equals(input.Trim(), "=default", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(defaultSaveDir);
        }

        // 2) Trim whitespace and expand %ENV% (Windows) / $ENV (PowerShell won't expand here, but %ENV% will)
        var path = Environment.ExpandEnvironmentVariables(input.Trim());

        // 3) Expand "~" to the user home (useful on Unix/macOS; harmless on Windows if present)
        if (path.StartsWith("~"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var tail = path.TrimStart('~').TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            path = string.IsNullOrEmpty(tail) ? home : Path.Combine(home, tail);
        }

        // 4) If not fully qualified, make it relative to defaultSaveDir
        if (!Path.IsPathFullyQualified(path))
        {
            path = Path.Combine(defaultSaveDir, path);
        }

        // 5) Normalize (removes ., .., mixed separators) and return
        return Path.GetFullPath(path);
    }
}