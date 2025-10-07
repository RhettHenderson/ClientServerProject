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
using Common;

class Client
{
    private static int id = -1;
    private static string[] commands = Array.Empty<string>();
    private static string name;
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
        Console.Write("Enter a username to go by: ");
        name = Console.ReadLine() ?? "Client";
        Socket socket = new Socket(ipAddr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        IPEndPoint localEndPoint = new IPEndPoint(ipAddr, 11111);

        socket.Connect(localEndPoint);
        Console.WriteLine("Socket connected to -> {0} < -", ipAddr.ToString());

        var recvTask = Task.Run(() => ReceiveLoopAsync(socket));
        Packet packet;

        while (true)
        {
            string? message = Console.ReadLine();
            if (message == null | message == "\\q")
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
                        Console.WriteLine($"{packet.ClientID}: {text}");
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
                        }


                        else if (variable == "commands")
                        {
                            commands = JsonSerializer.Deserialize<string[]>(packet.Payload) ?? Array.Empty<string>();
                            Console.WriteLine("Received commands list from server.");
                        }
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

}