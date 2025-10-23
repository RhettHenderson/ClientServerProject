using Common;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using static System.Net.Mime.MediaTypeNames;
using static System.Net.WebRequestMethods;

public class Client : IAsyncDisposable
{
    private int id = -1;
    public Stream? _stream = null;
    private string[] commands = Array.Empty<string>();
    public string Name { get; private set; }
    private bool userExists = false;
    private ConcurrentDictionary<string, TaskCompletionSource<Packet>> pendingResponses = new();
    public string authFile { get; private set; }
    private static string defaultSaveDir = @"C:\Users\rhett\Documents\downloads";
    //Current downloads in progress
    private static ConcurrentDictionary<string, FileReceiveState> files = new();

    //Events for the UI/CLI
    public event Action<string, string>? MessageReceived;   // (from, text)
    public event Action<string, string>? WhisperReceived;   // (from, text)
    public event Action<string[]>? CommandsReceived;
    public event Action<int>? IdAssigned;
    public event Action? Disconnected;
    public event Action<string>? Error;
    public event Action<NotificationType, string>? Notification;
    
    public async Task ConnectAsync(string host, int port, string name, string? passwordHash, bool createUser = false, Func<Task<string?>>? requestAuthCode = null, string authCode = "")
    {
        var ip = IPAddress.TryParse(host, out var ipAddr) ? ipAddr : IPAddress.Loopback;
        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        authFile = "auth.txt";
        await socket.ConnectAsync(new IPEndPoint(ip, port));

        var net = new NetworkStream(socket, ownsSocket: true);
        var ssl = new SslStream(net, leaveInnerStreamOpen: false,
            userCertificateValidationCallback: (_, __, ___, ____) => true // DEV ONLY
        );
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        });
        //Store it in a global so we can dispose it later
        _stream = ssl;
        Name = name;

        var recvTask = Task.Run(() => ReceiveLoopAsync(ssl));

        if (createUser)
        {
            Packet authCodeRequest = new Packet
            {
                ClientID = name,
                Headers = new Dictionary<string, string> { { "Type", "AuthCodeRequest" } },
                Payload = Array.Empty<byte>()
            };
            await PacketIO.SendPacketAsync(ssl, authCodeRequest);

            //Add an if statement here and a way to show an error in the UI later
            await CreateNewUser(name, ssl, passwordHash, authCode);
        }
        else
        {
            Packet authPacket = new Packet
            {
                ClientID = name,
                Headers = new Dictionary<string, string>
                {
                    { "Type", "Auth" }
                },
                Payload = Encoding.UTF8.GetBytes(passwordHash ?? "")
            };
            await PacketIO.SendPacketAsync(ssl, authPacket);
        }
    }

    public async Task SendMessageAsync(string text)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");

        await PacketIO.SendPacketAsync(_stream, new Packet
        {
            ClientID = Name,
            Headers = new Dictionary<string, string> { { "Type", "Message" } },
            Payload = Encoding.UTF8.GetBytes(text)
        });
    }

    public async Task SendCommandAsync(string command)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");
        await PacketIO.SendPacketAsync(_stream, new Packet
        {
            ClientID = Name,
            Headers = new Dictionary<string, string> { { "Type", "Command" } },
            Payload = Encoding.UTF8.GetBytes(command)
        });
    }

    public async Task ReceiveLoopAsync(Stream stream)
    {
        try
        {
            while (true)
            {
                var (status, packet) = await PacketIO.ReceivePacketAsync(stream);
                var headers = packet.Headers;
                var text = Encoding.UTF8.GetString(packet.Payload);
                if (status == PacketStatus.Ok && packet != null)
                {
                    var type = headers["Type"];

                    if (pendingResponses.TryRemove(type, out var tcs))
                    {
                        tcs.TrySetResult(packet);
                        continue;
                    }

                    switch (type)
                    {
                        case ("Message"):
                            if (text != "")
                            {
                                MessageReceived?.Invoke(packet.ClientID, text);
                            }
                            break;
                        case ("Whisper"):
                            WhisperReceived?.Invoke(packet.ClientID, text);
                            break;
                        //Data type tells the client to update some value
                        case ("Data"):
                            var variable = headers["Var"];
                            if (variable == "id")
                            {
                                id = int.Parse(text);
                                //Client is the default name if the user didn't input a name or there was an error
                                if (Name == "Client")
                                {
                                    Name = $"Client {id}";
                                    IdAssigned?.Invoke(id);
                                }
                                Packet ack = new Packet
                                {
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


                            else if (variable == "commands")
                            {
                                commands = JsonSerializer.Deserialize(packet.Payload, Common.CommonJsonContext.Default.StringArray) ?? Array.Empty<string>();
                                CommandsReceived?.Invoke(commands);
                                break;
                            }
                            break;

                        case ("AuthFailure"):
                            Error?.Invoke("Incorrect username or password. Connection closed.");
                            Disconnected?.Invoke();
                            stream.Dispose();
                            return;
                        case ("FileStart"):
                            await HandleFileStartAsync(stream, packet);
                            //Event for file received
                            break;
                        case ("FileChunk"):
                            await HandleFileChunkAsync(packet);
                            //Event for file chunk
                            break;
                        case ("FileEnd"):
                            await HandleFileEndAsync(stream, packet);
                            //Event for file end
                            break;
                        default:
                            Error?.Invoke($"Unknown packet type received: {type}");
                            break;
                    }
                }
                else
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke($"Connection error: {ex.Message}");
        }
        finally
        {
            Disconnected?.Invoke();
            try { stream.Dispose(); } catch { }
        }
    }
    async Task<bool> CreateNewUser(string username, Stream stream, string passwordHash, string userAuthCode)
    { 
        Packet authCode = new Packet
        {
            ClientID = username,
            Headers = new Dictionary<string, string> { { "Type", "AuthCode" } },
            Payload = Encoding.UTF8.GetBytes(userAuthCode)
        };

        Packet response = await SendAndWaitAsync(stream, authCode, "AuthStatus");
        var payload = Encoding.UTF8.GetString(response.Payload);
        if (payload == "Success")
        {
            Packet makeNewUser = new Packet
            {
                ClientID = username,
                Headers = new Dictionary<string, string> { { "Type", "CreateNewUser" }, { "Name", username }, { "PasswordHash", passwordHash } },
                Payload = Array.Empty<byte>()
            };
            response = await SendAndWaitAsync(stream, makeNewUser, "AuthStatus");
            payload = Encoding.UTF8.GetString(response.Payload);

            switch (payload)
            {
                case "Success":
                    return true;
                case "UsernameTaken":
                    return false;
                case "Failed":
                default:
                    return false;
            }

        }
        else
        {
            return false;
        }
    }

    public async Task<Packet> SendAndWaitAsync(Stream stream, Packet packet, string expectedType)
    {
        var tcs = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingResponses[expectedType] = tcs;

        await PacketIO.SendPacketAsync(stream, packet);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using (cts.Token.Register(() => tcs.TrySetCanceled()))
        {
            return await tcs.Task;
        }
    }

    public async Task<bool> SendFileAsync(string localPath, string? remoteFilename = null, string? saveLocation = null, int chunkSize = 64 * 1024)
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
            ClientID = Name,
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
        var startAck = await SendAndWaitAsync(_stream, start, "FileStartAck");
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
            while ((read = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                var payload = (read == buffer.Length) ? buffer : buffer.AsSpan(0, read).ToArray();
                var chunk = new Packet
                {
                    ClientID = Name,
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

                await PacketIO.SendPacketAsync(_stream, chunk);
                index++;
            }
        }

        //Send file end
        var end = new Packet
        {
            ClientID = Name,
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
        var endAck = await SendAndWaitAsync(_stream, end, "FileEndAck");

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

    public string SHA256Hash(string input)
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

    private static async Task HandleFileStartAsync(Stream stream, Packet packet)
    {
        var headers = packet.Headers;
        var name = headers["Name"];
        var length = long.Parse(headers["Length"]);
        var chunkSize = int.Parse(headers["ChunkSize"]);
        var saveLocation = headers["SaveLocation"];

        if (saveLocation.Contains("=default"))
        {
            saveLocation = defaultSaveDir;
        }
        //If it doesn't start with C or / treat it like a local directory (flawed  but i'll fix later)
        else if (!saveLocation.StartsWith("C") && !saveLocation.StartsWith("/"))
        {
            saveLocation = Path.Combine(defaultSaveDir, saveLocation);
        }

        Directory.CreateDirectory(saveLocation);
        var fullPath = Path.Combine(saveLocation, name);

        var reply = new Packet
        {
            ClientID = "Server",
            Headers = new Dictionary<string, string> { { "Type", "FileStartAck" }, { "Status", "Ok" }, { "FileKey", fullPath } },
            Payload = Array.Empty<byte>()
        };
        if (System.IO.File.Exists(fullPath) || files.ContainsKey(fullPath))
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

    private static async Task HandleFileChunkAsync(Packet packet)
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

    private static async Task HandleFileEndAsync(Stream stream, Packet packet)
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
            ClientID = "Server",
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

    public async ValueTask DisposeAsync()
    {
        try { _stream?.Dispose(); } catch { }
    }

    public enum NotificationType { Info, Warning, Error }
}