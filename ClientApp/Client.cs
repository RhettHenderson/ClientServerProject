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

class Client
{
    private static int id = -1;
    private static string[] commands = Array.Empty<string>();
    private static string name;
    private static bool authenticated = false;
    private static bool userExists = false;
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
            CreateNewUser(name.Split(" ")[1], socket);
            Thread.Sleep(200);
            //Authenticated is updated when we receive the AuthSuccess packet
            if (authenticated)
            {
                Console.Write("Your account has been successfully registered. \nPlease enter a password, then you will be returned to login: ");
                StringBuilder stringbuilder = new StringBuilder();
                while (true)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Enter)
                    {
                        break;
                    }
                    else if (key.Key == ConsoleKey.Backspace)
                    {
                        if (stringbuilder.Length > 0)
                        {
                            stringbuilder.Remove(stringbuilder.Length - 1, 1);
                            Console.Write("\b \b");
                        }
                    }
                    else
                    {
                        stringbuilder.Append(key.KeyChar);
                        Console.Write("*");
                    }
                }

                Packet newPassword = new Packet
                {
                    ClientID = name.Split(" ")[1],
                    Headers = new Dictionary<string, string> { { "Type", "SetPassword" } },
                    Payload = Encoding.UTF8.GetBytes(SHA256Hash(stringbuilder.ToString()))
                };
                await PacketIO.SendPacketAsync(socket, newPassword);
                Console.Write("\nEnter your username: ");
                name = Console.ReadLine();
            }
            else
            {
                Console.WriteLine("Incorrect authentication code. Closing connection.");
                socket.Shutdown(SocketShutdown.Both);
                socket.Close();
                return;
            }
        }
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

        //Only write this if the server accepts our auth
        Thread.Sleep(200);
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
                        else if (variable == "userExists")
                        {
                            userExists = true;
                        }
                        break;

                    case ("AuthFailure"):
                        Console.WriteLine("Authentication failed. Please try again.");
                        socket.Shutdown(SocketShutdown.Both);
                        socket.Close();
                        Environment.Exit(1);
                        break;
                    case ("AuthSuccess"):
                        Console.WriteLine("Authentication complete.");
                        authenticated = true;
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

    static async Task CreateNewUser(string username, Socket socket)
    {
        Packet checkUsernameTaken = new Packet
        {
            ClientID = username,
            Headers = new Dictionary<string, string> { { "Type", "CheckUserExists" } },
            Payload = Array.Empty<byte>()
        };
        await PacketIO.SendPacketAsync(socket, checkUsernameTaken);
        Thread.Sleep(200);
        if (userExists)
        {
            Console.WriteLine($"{username} is already taken. Please try again.");
            socket.Shutdown(SocketShutdown.Both);
            socket.Close();
            Environment.Exit(1);
        }

        Console.Write("Account creation is currently invite only. Please enter the authorization code generated by the server: ");
        Packet authCodeRequest = new Packet
        {
            ClientID = name,
            Headers = new Dictionary<string, string> { { "Type", "AuthCodeRequest" } },
            Payload = Encoding.UTF8.GetBytes("")
        };
        await PacketIO.SendPacketAsync(socket, authCodeRequest);
        string userAuthCode = Console.ReadLine();

        Packet authCode = new Packet
        {
            ClientID = name,
            Headers = new Dictionary<string, string> { { "Type", "AuthCode" } },
            Payload = Encoding.UTF8.GetBytes(userAuthCode)
        };
        await PacketIO.SendPacketAsync(socket, authCode);
    }

}