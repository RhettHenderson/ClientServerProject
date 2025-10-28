using Common;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Client_Server;
public class Client : IAsyncDisposable
{
    // === Static Fields ===
    private static readonly string defaultSaveDir = @"C:\Users\rhett\Documents\downloads";
    private static readonly ConcurrentDictionary<string, FileReceiveState> files = new(); // Current downloads in progress

    // === Instance Fields ===
    private int id = -1;
    private bool userExists = false;
    private string[] commands = Array.Empty<string>();

    // === Public Properties ===
    public string Name { get; private set; }
    public string authFile { get; private set; }
    public Stream? _stream { get; set; }
    // === Dictionaries / State ===
    public ConcurrentDictionary<string, TaskCompletionSource<Packet>> pendingResponses { get; } = new();

    // === Events (UI / CLI) ===
    public event Action<string, string>? MessageReceived;      // (from, text)
    public event Action<string, string>? WhisperReceived;      // (from, text)
    public event Action<string[]>? CommandsReceived;
    public event Action<int>? IdAssigned;
    public event Action? Disconnected;
    public event Action<string>? Error;
    public event Action<NotificationType, string>? Notification;

    //--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

    // === Main Client Logic ===
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
                            await PacketIO.HandleFileStartAsync(stream, packet, files, Name, defaultSaveDir);
                            //Event for file received
                            break;
                        case ("FileChunk"):
                            await PacketIO.HandleFileChunkAsync(packet, files);
                            //Event for file chunk
                            break;
                        case ("FileEnd"):
                            await PacketIO.HandleFileEndAsync(stream, packet, files, Name);
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

    // === Client-Exclusive Messaging Functions
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

    // === User Creation ===
    public async Task<bool> CreateNewUser(string username, Stream stream, string passwordHash, string userAuthCode)
    { 
        Packet authCode = new Packet
        {
            ClientID = username,
            Headers = new Dictionary<string, string> { { "Type", "AuthCode" } },
            Payload = Encoding.UTF8.GetBytes(userAuthCode)
        };

        Packet response = await PacketIO.SendAndWaitAsync(stream, authCode, "AuthStatus", pendingResponses);
        var payload = Encoding.UTF8.GetString(response.Payload);
        if (payload == "Success")
        {
            Packet makeNewUser = new Packet
            {
                ClientID = username,
                Headers = new Dictionary<string, string> { { "Type", "CreateNewUser" }, { "Name", username }, { "PasswordHash", passwordHash } },
                Payload = Array.Empty<byte>()
            };
            response = await PacketIO.SendAndWaitAsync(stream, makeNewUser, "AuthStatus", pendingResponses);
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

    // === IDisposable Implementation ===
    public async ValueTask DisposeAsync()
    {
        try { _stream?.Dispose(); } catch { }
    }
}