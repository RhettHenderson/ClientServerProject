using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using Common;
using System.Data;
using System.Reflection.Metadata.Ecma335;
using System.Diagnostics;

namespace Client_Server;
class Server
{
    private static Socket listener;
    //This maps ID numbers to sockets
    private static ConcurrentDictionary<int, Socket> clients = new();
    //This maps names to ID numbers
    private static ConcurrentDictionary<string, int> names = new();
    //This maps ID to positions
    private static ConcurrentDictionary<int, (float, float, float)> positions = new();
    private static int nextID = 0;
    private static string[] commands = { "help", "whisper", "w" };
    private static byte[] cmdJson = JsonSerializer.SerializeToUtf8Bytes(commands);

    public static async Task Main(string[] args)
    {
        await ExecuteServerAsync(11111);
    }

    static void InitListener()
    {
        Console.WriteLine("Initializing server...");
        Console.Write("Enter IP address to listen on or press Enter for localhost: ");
        string serverIP = Console.ReadLine();
        IPAddress ipAddr;
        if (serverIP == "")
        {
            IPHostEntry ipHost = Dns.GetHostEntry(Dns.GetHostName());
            ipAddr = ipHost.AddressList[0];
        }
        else
        {
            try
            {
                ipAddr = IPAddress.Parse(serverIP);
            }
            catch (FormatException)
            {
                Console.WriteLine("Invalid IP address format. Please enter a valid IP address.");
                throw;
            }
        }
        IPEndPoint localEndPoint = new IPEndPoint(ipAddr, 11111);
        //Create TCP Socket
        listener = new Socket(ipAddr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(localEndPoint);
        listener.Listen(10);
    }

    static async Task<int> WaitForConnectionAsync()
    {
        Console.WriteLine("Awaiting Connection...");
        Socket client = await listener.AcceptAsync();
        //Uses the Nagle algorithm (google for more info)
        client.NoDelay = true;
        int id = Interlocked.Increment(ref nextID);
        clients[id] = client;
        Console.WriteLine($"Client with ID {id} connected.");
        await SendInitialPackets(client, id);
        return id;
    }
    

    public static async Task ExecuteServerAsync(int port)
    {
        Console.Title = "Server";
        InitListener();
        _ = Task.Run(() =>
        {
            Console.ReadLine();
            try { listener.Close(); } catch { }
        });

        while (true)
        {
            Socket client = null;
            try
            {
                int id = await WaitForConnectionAsync();
                HandleClientAsync(id);
            }
            catch (OperationCanceledException e)
            {
                Console.WriteLine(e.ToString());
                break;
            }

        }
    }

    private static async Task HandleClientAsync(int id)
    {
        Console.WriteLine($"Client #{id} handler started.");
        //Sends the packet to tell the client what its id is
        while (true)
        {
            bool keepAlive = await ProcessPacketAsync(id);
            if (!keepAlive) break;
        }
    }

    private static async Task<bool> ProcessPacketAsync(int id)
    {
        Socket client = clients[id];
        var (status, incoming) = await PacketIO.ReceivePacketAsync(client);
        Console.WriteLine(status.ToString());
        if (status == PacketStatus.Disconnected)
        {
            Console.WriteLine($"Client {id} forcibly disconnected");
            client.Shutdown(SocketShutdown.Both);
            client.Close();
            clients.TryRemove(new KeyValuePair<int, Socket>(id, clients[id]));
            return false;
        }
        else if (status == PacketStatus.Error)
        {
            Console.WriteLine("An error occured trying to receive the last packet. Closing connection.");
            client.Shutdown(SocketShutdown.Both);
            client.Close();
            clients.TryRemove(new KeyValuePair<int, Socket>(id, clients[id]));
            return false;
        }
        //if we reach here status is Ok
        var clientID = incoming.ClientID;
        var headers = incoming.Headers;
        var text = Encoding.UTF8.GetString(incoming.Payload);
        //Default values for the packet
        Packet reply = new Packet
        {
            ClientID = "Server",
            Headers = new Dictionary<string, string> { { "Type", "Message" } },
            Payload = Encoding.ASCII.GetBytes("")
        };

        //Step 1: Read headers to determine packet type
        //Types so far are "Message", "Command", "Ack", "Data"
        var type = headers["Type"];
        switch (type)
        {
            case ("Message"):
                Console.WriteLine($"Client {id} with name {clientID} sent chat {text}");
                await BroadcastAsync(incoming, id);
                return true;
            case ("Command"):
                switch (text.Split(" ")[0])
                {
                    case "help":
                        StringBuilder sb = new StringBuilder("Available Commands: ");
                        foreach (string cmd in commands)
                        {
                            sb.Append($"--{cmd} ");
                        }
                        reply.Payload = Encoding.ASCII.GetBytes(sb.ToString());
                        await PacketIO.SendPacketAsync(client, reply);
                        return true;

                    case "whisper":
                    case "w":
                        string[] args = text.Split(" ");
                        if (args.Length < 3)
                        {
                            reply.Payload = Encoding.ASCII.GetBytes("Usage: --whisper <ID> <message>");
                            await PacketIO.SendPacketAsync(client, reply);
                            return true;
                        }
                        //Step 1: Check if the user used ID or name
                        //Step 2: If using ID, no changes made. If using name, look up ID
                        //Step 3: Check if ID exists
                        //Step 4: Send message if it does, error if it doesn't
                        if (!int.TryParse(args[1], out int targetID))
                        {
                            //User used a name instead of an ID
                            if (!names.ContainsKey(args[1]))
                            {
                                reply.Payload = Encoding.ASCII.GetBytes($"User with name {args[1]} not found.");
                                await PacketIO.SendPacketAsync(client, reply);
                                return true;
                            }
                            //Name exists, get ID
                            targetID = names[args[1]];
                        }
                        if (!clients.ContainsKey(targetID))
                        {
                            reply.Payload = Encoding.ASCII.GetBytes($"User with ID {targetID} not found.");
                            await PacketIO.SendPacketAsync(client, reply);
                            return true;
                        }
                        string msg = string.Join(" ", args, 2, args.Length - 2);
                        Packet whisper = new Packet
                        {
                            ClientID = clientID,
                            Headers = new Dictionary<string, string> { { "Type", "Whisper" } },
                            Payload = Encoding.ASCII.GetBytes($"{msg}")
                        };
                        await PacketIO.SendPacketAsync(clients[targetID], whisper);
                        return true;
                    default:
                        reply = new Packet
                        {
                            ClientID = "Server",
                            Headers = new Dictionary<string, string> { { "Type", "Message" } },
                            Payload = Encoding.ASCII.GetBytes($"Unknown command: {text}. Type --help for a list of commands.")
                        };
                        await PacketIO.SendPacketAsync(client, reply);
                        return true;
                }
            case "Ack":
                Console.WriteLine($"Received ACK from client {id}.");
                //Sets the client's name
                names[clientID] = id;
                return true;
            case "Pos":
                //Position update packet
                positions[id] = PositionCodec.Decode(incoming.Payload);
                //Just broadcast it to everyone else
                await BroadcastAsync(incoming, id);
                return true;
            default:
                Console.WriteLine($"Invalid packet header: {type}.");
                break;
        }
        
        return false;
    }


    public static async Task BroadcastAsync(Packet packet, int? excludeID = null)
    {
        foreach (var currClient in clients)
        {
            if (currClient.Key == excludeID) continue; //Skip sending to the original sender
            Console.WriteLine($"Sending reply to client {currClient.Key}.");
            await PacketIO.SendPacketAsync(currClient.Value, packet);
        }
    }

    public static async Task SendInitialPackets(Socket client, int id)
    {
        Packet pkt = new Packet
        {
            ClientID = "Server",
            Headers = new Dictionary<string, string>
            {
                { "Type", "Data" },
                { "Var", "id" }
            },
            //Tells the client what its id is
            Payload = Encoding.UTF8.GetBytes(id.ToString())
        };
        await PacketIO.SendPacketAsync(client, pkt);
        Console.WriteLine($"Sent ID packet to client {id}");

        pkt.Headers = new Dictionary<string, string>
        {
            { "Type", "Data" },
            { "Var", "commands" }
        };
        pkt.Payload = cmdJson;
        await PacketIO.SendPacketAsync(client, pkt);
        Console.WriteLine($"Sent commands packet to client {id}");
    }
}