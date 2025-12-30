using System.Collections.Concurrent;
using static Common.PacketIO;
using static Common.Utility;

namespace Common;

public static class FileTransfer {

    /// <summary>
    /// Sends a file to <paramref name="recipientName"/> via the server (client -> server -> recipient).
    /// The server generates a TransferId and relays all file packets.
    /// </summary>
    public static async Task<bool> SendFileAsync(
        Stream stream,
        string localPath,
        ConcurrentDictionary<string, TaskCompletionSource<Packet>> pendingResponses,
        string senderName,
        string recipientName,
        string? remoteFilename = null,
        string? saveLocation = null,
        Action<NotificationType, string>? Notification = null
    ) {
        if (string.IsNullOrWhiteSpace(senderName)) throw new ArgumentException("senderName required.", nameof(senderName));
        if (string.IsNullOrWhiteSpace(recipientName)) throw new ArgumentException("recipientName required.", nameof(recipientName));

        if (!File.Exists(localPath)) {
            Notification?.Invoke(NotificationType.Error, $"File not found: {localPath}");
            return false;
        }

        remoteFilename ??= Path.GetFileName(localPath);
        saveLocation ??= "=default";

        long length = new FileInfo(localPath).Length;
        int chunkSize = 256 * 1024;
        int totalChunks = (int)((length + chunkSize - 1) / chunkSize);

        // Used to correlate FileStartAck back to the sender without collisions.
        string clientNonce = Guid.NewGuid().ToString("N");

        var start = new Packet {
            ClientID = senderName,
            Headers = new Dictionary<string, string> {
                { "Type", "FileStart" },
                { "Sender", senderName },
                { "Recipient", recipientName },
                { "Name", remoteFilename },
                { "Length", length.ToString() },
                { "ChunkSize", chunkSize.ToString() },
                { "SaveLocation", saveLocation },
                { "ClientNonce", clientNonce }
            },
            Payload = Array.Empty<byte>()
        };

        Notification?.Invoke(NotificationType.Info, $"Starting file transfer to {recipientName}: {remoteFilename} ({length} bytes)");
        var startAck = await SendAndWaitAsync(
            stream,
            start,
            expectedKey: $"FileStartAck:{clientNonce}",
            pendingResponses: pendingResponses,
            timeout: TimeSpan.FromSeconds(20)
        );

        if (startAck == null) {
            Notification?.Invoke(NotificationType.Error, "FileStartAck not received (timeout).");
            return false;
        }

        if (!startAck.Headers.TryGetValue("Status", out var startStatus)) {
            Notification?.Invoke(NotificationType.Error, "FileStartAck missing Status.");
            return false;
        }

        if (!startStatus.Equals("Ok", StringComparison.OrdinalIgnoreCase)) {
            Notification?.Invoke(NotificationType.Error, $"FileStart rejected: {startStatus}");
            return false;
        }

        if (!startAck.Headers.TryGetValue("TransferId", out var transferId) || string.IsNullOrWhiteSpace(transferId)) {
            Notification?.Invoke(NotificationType.Error, "FileStartAck missing TransferId.");
            return false;
        }

        // Send file chunks
        int index = 0;
        await using (var fs = System.IO.File.OpenRead(localPath)) {
            byte[] buffer = new byte[chunkSize];
            int read;
            Notification?.Invoke(NotificationType.Info, "Beginning file chunk transfers...");
            while ((read = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0) {
                var payload = (read == buffer.Length) ? buffer : buffer.AsSpan(0, read).ToArray();

                var chunk = new Packet {
                    ClientID = senderName,
                    Headers = new Dictionary<string, string> {
                        { "Type", "FileChunk" },
                        { "TransferId", transferId },
                        { "Index", index.ToString() },
                        { "Name", remoteFilename }
                    },
                    Payload = payload
                };

                await SendPacketAsync(stream, chunk);
                index++;
            }
        }

        // Send file end
        var end = new Packet {
            ClientID = senderName,
            Headers = new Dictionary<string, string> {
                { "Type", "FileEnd" },
                { "TransferId", transferId },
                { "TotalChunks", totalChunks.ToString() },
                { "Name", remoteFilename }
            },
            Payload = Array.Empty<byte>()
        };

        Notification?.Invoke(NotificationType.Info, "Sending file end packet...");
        var endAck = await SendAndWaitAsync(
            stream,
            end,
            expectedKey: $"FileEndAck:{transferId}",
            pendingResponses: pendingResponses,
            timeout: TimeSpan.FromSeconds(30)
        );

        if (endAck == null) {
            Notification?.Invoke(NotificationType.Error, "FileEndAck not received (timeout). File transfer failed.");
            return false;
        }

        if (!endAck.Headers.TryGetValue("Status", out var endStatus)) {
            Notification?.Invoke(NotificationType.Error, "FileEndAck missing Status.");
            return false;
        }

        if (endStatus.Equals("Success", StringComparison.OrdinalIgnoreCase)) {
            Notification?.Invoke(NotificationType.Info, "File transfer complete.");
            return true;
        }

        Notification?.Invoke(NotificationType.Error, $"File transfer failed: {endStatus}");
        return false;
    }

    public static async Task HandleFileStartAsync(Stream stream, Packet packet, ConcurrentDictionary<string, FileReceiveState> files, string Name, string defaultSaveDir) {
        var headers = packet.Headers;

        headers.TryGetValue("TransferId", out var transferId);
        transferId = string.IsNullOrWhiteSpace(transferId) ? Guid.NewGuid().ToString("N") : transferId;

        var name = headers["Name"];
        var length = long.Parse(headers["Length"]);
        var chunkSize = int.Parse(headers["ChunkSize"]);
        var saveLocation = headers.TryGetValue("SaveLocation", out var sl) ? sl : "=default";

        var saveDir = Utility.ResolveSaveDir(saveLocation, defaultSaveDir);
        Directory.CreateDirectory(saveDir);
        var fullPath = Path.Combine(saveDir, name);

        var reply = new Packet {
            ClientID = Name,
            Headers = new Dictionary<string, string> {
                { "Type", "FileStartAck" },
                { "Status", "Ok" },
                { "TransferId", transferId }
            },
            Payload = Array.Empty<byte>()
        };

        // Echo nonce if present so the sender can correlate.
        if (headers.TryGetValue("ClientNonce", out var nonce) && !string.IsNullOrWhiteSpace(nonce)) {
            reply.Headers["ClientNonce"] = nonce;
        }

        if (File.Exists(fullPath)) {
            reply.Headers["Status"] = "Exists";
            await PacketIO.SendPacketAsync(stream, reply);
            return;
        }

        try {
            var state = new FileReceiveState {
                Name = fullPath,
                ExpectedLength = length,
                ExpectedChunks = (int)((length + chunkSize - 1) / chunkSize),
                Stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true),
            };
            files[transferId] = state;
            state.Stream.Position = 0;

            await SendPacketAsync(stream, reply);
        }
        catch (IOException) {
            // File already exists or cannot be created.
            reply.Headers["Status"] = "Exists";
            await SendPacketAsync(stream, reply);
        }
        catch {
            reply.Headers["Status"] = "Error";
            await SendPacketAsync(stream, reply);
        }
    }

    public static async Task HandleFileChunkAsync(Packet packet, ConcurrentDictionary<string, FileReceiveState> files) {
        var headers = packet.Headers;
        var payload = packet.Payload ?? Array.Empty<byte>();

        if (!headers.TryGetValue("TransferId", out var transferId) || string.IsNullOrWhiteSpace(transferId)) {
            return;
        }

        if (!files.TryGetValue(transferId, out var state) || state.Stream == null) {
            return;
        }

        await state.Stream.WriteAsync(payload, 0, payload.Length);
        state.Received += payload.Length;
    }

    public static async Task HandleFileEndAsync(Stream stream, Packet packet, ConcurrentDictionary<string, FileReceiveState> files, string Name) {
        var headers = packet.Headers;

        if (!headers.TryGetValue("TransferId", out var transferId) || string.IsNullOrWhiteSpace(transferId)) {
            return;
        }

        var totalChunks = int.Parse(headers["TotalChunks"]);

        var reply = new Packet {
            ClientID = Name,
            Headers = new Dictionary<string, string> {
                { "Type", "FileEndAck" },
                { "TransferId", transferId }
            },
            Payload = Array.Empty<byte>()
        };

        if (!files.TryGetValue(transferId, out var state) || state.Stream == null) {
            reply.Headers["Status"] = "Error";
            await SendPacketAsync(stream, reply);
            return;
        }

        try {
            await state.Stream.FlushAsync();
            state.Stream.Close();
            await state.Stream.DisposeAsync();

            if (state.Received != state.ExpectedLength) {
                reply.Headers["Status"] = "LengthMismatch";
            }
            else if (totalChunks != state.ExpectedChunks) {
                reply.Headers["Status"] = "ChunkMismatch";
            }
            else {
                reply.Headers["Status"] = "Success";
            }
        }
        catch {
            reply.Headers["Status"] = "Error";
        }
        finally {
            files.TryRemove(transferId, out _);
        }

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
