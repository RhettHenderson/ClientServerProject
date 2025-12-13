using System.Collections.Concurrent;
using static Common.PacketIO;
using static Common.Utility;

namespace Common;
public static class FileTransfer {
    public static async Task<bool> SendFileAsync(Stream stream, string localPath, ConcurrentDictionary<string, TaskCompletionSource<Packet>> pendingResponses,
    string? remoteFilename = null, string? saveLocation = null, Action<NotificationType, string>? Notification = null) {

        if (!File.Exists(localPath)) {
            Notification?.Invoke(NotificationType.Error, $"File not found: {localPath}");
            return false;
        }

        //Instead of hardcoding the default location here, we let the serve choose it
        remoteFilename ??= Path.GetFileName(localPath);
        saveLocation ??= "=default";
        long length = new FileInfo(localPath).Length;
        int chunkSize = 256 * 1024;

        //Order of file transfer:
        //File start packet
        //File chunk packet (1 or more)
        //File end packet

        var start = new Packet {
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
        var startAck = await SendAndWaitAsync(stream, start, "FileStartAck", pendingResponses);
        if (startAck == null) {
            return false;
        }
        if (startAck.Headers["Status"] == "Exists") {
            return false;
        }

        startAck.Headers.TryGetValue("FileKey", out var key);

        //Send file chunks
        int index = 0;
        int totalChunks = (int)(length + chunkSize - 1) / chunkSize;

        await using (var fs = System.IO.File.OpenRead(localPath)) {
            byte[] buffer = new byte[chunkSize];
            int read;
            Notification?.Invoke(NotificationType.Info, "Beginning file chunk transfers...");
            Console.WriteLine("Beginning file chunk transfers");
            while ((read = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0) {
                var payload = (read == buffer.Length) ? buffer : buffer.AsSpan(0, read).ToArray();
                var chunk = new Packet {
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

                await SendPacketAsync(stream, chunk);
                index++;
            }
        }

        //Send file end
        var end = new Packet {
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
        var endAck = await SendAndWaitAsync(stream, end, "FileEndAck", pendingResponses);
        if (endAck != null) {
            if (endAck.Headers["Status"] == "Error") {
                Notification?.Invoke(NotificationType.Error, "File end ack error. File transfer failed.");
                return false;
            }
            Notification?.Invoke(NotificationType.Info, "File transfer complete.");
            return true;
        }
        else {
            Notification?.Invoke(NotificationType.Error, "File end ack never received. File transfer failed.");
            return false;
        }


    }

    public static async Task HandleFileStartAsync(Stream stream, Packet packet, ConcurrentDictionary<string, FileReceiveState> files, string Name, string defaultSaveDir) {
        var headers = packet.Headers;
        var name = headers["Name"];
        var length = long.Parse(headers["Length"]);
        var chunkSize = int.Parse(headers["ChunkSize"]);
        var saveLocation = headers["SaveLocation"];

        var saveDir = Utility.ResolveSaveDir(saveLocation, defaultSaveDir);
        Directory.CreateDirectory(saveDir);
        var fullPath = Path.Combine(saveDir, name);

        var reply = new Packet {
            ClientID = Name,
            Headers = new Dictionary<string, string> { { "Type", "FileStartAck" }, { "Status", "Ok" }, { "FileKey", fullPath } },
            Payload = Array.Empty<byte>()
        };
        if (File.Exists(fullPath) || files.ContainsKey(fullPath)) {
            Console.WriteLine("File exists.");
            reply.Headers["Status"] = "Exists";
            await PacketIO.SendPacketAsync(stream, reply);
            return;
        }
        var state = new FileReceiveState {
            Name = fullPath,
            ExpectedLength = length,
            ExpectedChunks = (int)((length + chunkSize - 1) / chunkSize),
            Stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true),
        };
        files[fullPath] = state;
        state.Stream.Position = 0;
        reply.Headers["Status"] = "OK";
        Console.WriteLine("Sending FileStartAck");
        await SendPacketAsync(stream, reply);
    }

    public static async Task HandleFileChunkAsync(Packet packet, ConcurrentDictionary<string, FileReceiveState> files) {
        var headers = packet.Headers;
        var payload = packet.Payload ?? Array.Empty<byte>();

        headers.TryGetValue("FileKey", out var key);

        if (string.IsNullOrEmpty(key)) {
            return;
        }

        //Ignore if name doesn't match the value we have in our dictionary
        if (!files.TryGetValue(key, out var state) || state.Stream == null) {
            return;
        }

        //Append the payload
        await state.Stream.WriteAsync(payload, 0, payload.Length);
        state.Received += payload.Length;
    }

    public static async Task HandleFileEndAsync(Stream stream, Packet packet, ConcurrentDictionary<string, FileReceiveState> files, string Name) {
        Console.WriteLine("\nBeginning file end processing.");
        var headers = packet.Headers;
        var totalChunks = int.Parse(headers["TotalChunks"]);

        headers.TryGetValue("FileKey", out var key);
        if (string.IsNullOrEmpty(key)) {
            return;
        }

        var reply = new Packet {
            ClientID = Name,
            Headers = new Dictionary<string, string> { { "Type", "FileEndAck" } },
            Payload = Array.Empty<byte>()
        };

        if (!files.TryGetValue(key, out var state) || state.Stream == null) {
            reply.Headers["Status"] = "Error";
        }
        else {
            try {
                await state.Stream.FlushAsync();
                state.Stream.Close();
                await state.Stream.DisposeAsync();


                if (state.Received != state.ExpectedLength) {
                    reply.Headers["Status"] = "LengthMismatch";
                }
                else if (totalChunks != state.ExpectedChunks) {
                    reply.Headers["Status"] = "LengthMismatch";
                }
                else {
                    reply.Headers["Status"] = "Success";
                }
            }
            catch {
                reply.Headers["Status"] = "Error";
            }
            finally {
                files.TryRemove(key, out _);

            }
        }
        Console.WriteLine("Sending FileEndAck");
        await SendPacketAsync(stream, reply);
    }

}
public class FileReceiveState {
    public string Name = "";
    public long ExpectedLength;
    public long Received;
    public int ExpectedChunks;
    public FileStream? Stream;
}
