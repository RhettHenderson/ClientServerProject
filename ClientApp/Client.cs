using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using System.Buffers.Binary;
using System.ComponentModel.Design;
using System.Runtime.CompilerServices;
using System.Numerics;
using System.Security.Cryptography;
using Common;
using System.Collections.Concurrent;

class Client
{
    private static int id = -1;
    private static string[] commands = Array.Empty<string>();
    private static string name;
    private static bool userExists = false;
    private static ConcurrentDictionary<string, TaskCompletionSource<Packet>> pendingResponses = new();
    public static async Task Main(string[] args)
    {
        await ExecuteClientAsync("localhost", 11111);
    }

    static async Task ExecuteClientAsync(string host, int port)
    {
        Console.Title = "Client";
        IPAddress ipAddr;
        IPHostEntry ipHostEntry = Dns.GetHostEntry(Dns.GetHostName());
        ipAddr = ipHostEntry.AddressList[0];
        Console.Write("Enter server IP or press enter for localhost: ");
        string input = Console.ReadLine();
        if (input != "")
        {
            try
            {
                ipAddr = IPAddress.Parse(input);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
        }
        Socket socket = new Socket(ipAddr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        IPEndPoint localEndPoint = new IPEndPoint(ipAddr, 11111);
        socket.Connect(localEndPoint);
        //Start listening so we can receive the DC if it's sent
        var recvTask = Task.Run(() => ReceiveLoopAsync(socket));

        Console.Write("Enter your username or type --create <username> if you're a new user: ");
        name = Console.ReadLine() ?? "Client";
        if (name.Split(" ")[0] == "--create")
        {
            //Reassign name to just the name without the --create
            name = name.Split(" ")[1];
            if (await CreateNewUser(name, socket))
            {
                Console.WriteLine($"You are now logged in as {name}.");
            }
            else
            {
                socket.Shutdown(SocketShutdown.Both);
                socket.Close();
                Environment.Exit(1);
            }
        }
        else
        {
            Console.Write("Enter your password: ");
            StringBuilder sb = new StringBuilder();
            //Loop to read user input without showing it
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                {
                    break;
                }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0)
                    {
                        sb.Remove(sb.Length - 1, 1);
                        Console.Write("\b \b");
                    }
                }
                else
                {
                    sb.Append(key.KeyChar);
                    Console.Write("*");
                }
            }
            Console.Write("\n");
            string passwordHash = SHA256Hash(sb.ToString());
            var authPacket = new Packet
            {
                ClientID = name,
                Headers = new Dictionary<string, string>
                {
                    { "Type", "Auth" }
                },
                Payload = Encoding.UTF8.GetBytes(passwordHash)
            };
            await PacketIO.SendPacketAsync(socket, authPacket);
        }

        Console.WriteLine("Socket connected to -> {0} < -", ipAddr.MapToIPv4().ToString());
        Packet packet;

        while (true)
        {
            string? message = Console.ReadLine();
            if (message == null || message == "\\q")
            {
                break;
            }
            else if (message.StartsWith("--"))
            {
                var command = message[2..];
                if (!commands.Contains(command.Split(" ")[0]))
                {
                    Console.WriteLine("Invalid command. Please try again");
                    continue;
                }
                packet = new Packet
                {
                    ClientID = name,
                    Headers = new Dictionary<string, string>
                    {
                        { "Type", "Command" }
                    },
                    Payload = Encoding.UTF8.GetBytes(command)
                };
            }
            else
            {
                //The message we're sending to the server
                packet = new Packet
                {
                    ClientID = name,
                    Headers = new Dictionary<string, string>
                {
                    { "Type", "Message" }
                },
                    Payload = Encoding.UTF8.GetBytes(message)
                };
            }
            await PacketIO.SendPacketAsync(socket, packet);
        }
        socket.Shutdown(SocketShutdown.Both);
        socket.Close();

        await recvTask;
    }

    static async Task ReceiveLoopAsync(Socket socket)
    {
        while (true)
        {
            var (status, packet) = await PacketIO.ReceivePacketAsync(socket);
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
                            Console.WriteLine($"{packet.ClientID}: {text}");
                        }
                        break;
                    case ("Whisper"):
                        Console.WriteLine($"(Whisper) {packet.ClientID}: {text}");
                        break;
                    //Data type tells the client to update some value
                    case ("Data"):
                        var variable = headers["Var"];
                        if (variable == "id")
                        {
                            id = int.Parse(text);
                            Console.Title = $"Client {id}";
                            //Client is the default name if the user didn't input a name or there was an error
                            if (name == "Client")
                            {
                                name = $"Client {id}";
                            }
                            Packet ack = new Packet
                            {
                                ClientID = name,
                                Headers = new Dictionary<string, string>
                                {
                                    { "Type", "Ack" }
                                },
                                Payload = Array.Empty<byte>()
                            };
                            //Send an ack back to confirm we received our ID and to set our name
                            await PacketIO.SendPacketAsync(socket, ack);
                            break;
                        }


                        else if (variable == "commands")
                        {
                            commands = JsonSerializer.Deserialize(packet.Payload, Common.CommonJsonContext.Default.StringArray) ?? Array.Empty<string>();
                            Console.WriteLine("Received commands list from server.");
                            break;
                        }
                        break;

                    case ("AuthStatus"):
                        Console.WriteLine("Authentication failed. Please try again.");
                        socket.Shutdown(SocketShutdown.Both);
                        socket.Close();
                        Environment.Exit(1);
                        break;
    
                    default:
                        Console.WriteLine("Invalid packet headers.");
                        break;
                }
            }
            else
            {
                Console.WriteLine("Server disconnected");
                break;
            }
        }
    }

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

    /*
     *Better structure: CreateNewUser asks for a username and password. After it asks for password, it sends a CreateNewUser packet
     * containing the username and password to the server. If success, it returns a Success packet. Otherwise, it indicates why
     * 
    */
    static async Task<bool> CreateNewUser(string username, Socket socket)
    {
        Console.Write("Create a password: ");
        StringBuilder passwordSB = new StringBuilder();
        StringBuilder passwordSB2 = new StringBuilder();
        //Function for hidden typing
        HiddenInput(ref passwordSB);
        Console.Write("\nType your password again: ");
        HiddenInput(ref passwordSB2);

        if (passwordSB.ToString() != passwordSB2.ToString())
        {
            Console.WriteLine("\nPasswords do not match. Try again.");
            return false;
        }

        string passwordHash = SHA256Hash(passwordSB.ToString());

        Console.Write("\nPlease enter the authorization code generated by the server: ");
        Packet authCodeRequest = new Packet
        {
            ClientID = username,
            Headers = new Dictionary<string, string> { { "Type", "AuthCodeRequest" } },
            Payload = Array.Empty<byte>()
        };
        await PacketIO.SendPacketAsync(socket, authCodeRequest);
        string userAuthCode = Console.ReadLine();

        Packet authCode = new Packet
        {
            ClientID = username,
            Headers = new Dictionary<string, string> { { "Type", "AuthCode" } },
            Payload = Encoding.UTF8.GetBytes(userAuthCode)
        };

        Packet response = await SendAndWaitAsync(socket, authCode, "AuthStatus");
        var payload = Encoding.UTF8.GetString(response.Payload);
        if (payload == "Success")
        {
            Packet makeNewUser = new Packet
            {
                ClientID = username,
                Headers = new Dictionary<string, string> { { "Type", "CreateNewUser" }, { "Name", username }, { "PasswordHash", passwordHash } },
                Payload = Array.Empty<byte>()
            };
            response = await SendAndWaitAsync(socket, makeNewUser, "AuthStatus");
            payload = Encoding.UTF8.GetString(response.Payload);

            switch (payload)
            {
                case "Success":
                    Console.WriteLine("Account created.");
                    return true;
                case "UsernameTaken":
                    Console.WriteLine("That username is already taken. Account creation failed.");
                    return false;
                case "Failed":
                default:
                    Console.WriteLine("Unknown response code from server. Account creation failed.");
                    return false;
            }

        }
        else
        {
            Console.WriteLine("Incorrect authentication code. Closing connection.");
            return false;
        }
    }

    public static async Task<Packet> SendAndWaitAsync(Socket socket, Packet packet, string expectedType)
    {
        var tcs = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingResponses[expectedType] = tcs;

        await PacketIO.SendPacketAsync(socket, packet);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using (cts.Token.Register(() => tcs.TrySetCanceled()))
        {
            return await tcs.Task;
        }
    }

    private static void HiddenInput(ref StringBuilder sb, string shownChar = "*")
    {
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }
            else if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Remove(sb.Length - 1, 1);
                    Console.Write("\b \b");
                }
            }
            else
            {
                sb.Append(key.KeyChar);
                Console.Write(shownChar);
            }
        }
    }

}