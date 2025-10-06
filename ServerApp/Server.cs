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

namespace Client_Server;
class Server
{
    private static Socket listener;
    private static ConcurrentDictionary<int, Socket> clients = new();
    private static int nextID = 0;

    //static void Main(string[] args)
    //{
    //    ExecuteServer();
    //}

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
        await SendInitialPacket(client, id);
        return id;
    }

    //Returns 0 if no command, 1 if command exists and nothing needs to be done, 2 if command exists and connection should be closed
    static int ReadCommand(string text)
    {
        switch (text)
        {
            case "\\q":
                Console.WriteLine("Client requested to end connection");
                return 2;
            case "\\info":
                Console.WriteLine("Client attempted the 'info' command, which hasn't been implemented yet");
                return 1;
            case "\\help":
                Console.WriteLine("Client attempted the 'help' command, which hasn't been implemented yet");
                return 1;
            default:
                return 0;
        }
    }

    public static async Task ExecuteServerAsync(int port)

    {
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
        int commandStatus = ReadCommand(text);

        //Step 1: Read headers to determine packet type
        var type = headers["Type"];
        switch (type)
        {
            case ("Message"):
                Console.WriteLine($"Client {id} with name {clientID} sent chat {text}");
                await BroadcastAsync(incoming, id);
                break;
            default:
                Console.WriteLine($"Invalid packet header: {type}.");
                break;
        }

        if (commandStatus == 2)
        {
            client.Shutdown(SocketShutdown.Both);
            client.Close();
            clients.TryRemove(new KeyValuePair<int, Socket>(id, clients[id]));
        }
        else if (commandStatus == 1)
        {
            return true;
        }
        else if (commandStatus == 0)
        {
            Console.WriteLine($"Received Message from {clientID}: {text}");
            //Broadcasts the message to all other clients
            //Commented out for now because it's causing issues
            //BroadcastAsync(incoming, int.Parse(clientID));
            Packet reply = new Packet
            {
                ClientID = "Server",
                Headers = new Dictionary<string, string> { { "Type", "Ack" } },
                Payload = Encoding.ASCII.GetBytes($"Ack: {text}")
            };

            //PacketIO.SendPacketAsync(client, reply);
            return true;
        }
        else
        {
            Console.WriteLine($"Unexpected return value from ReadCommand: {commandStatus}");
            return false;
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

    public static async Task SendInitialPacket(Socket client, int id)
    {
        Packet pkt = new Packet
        {
            ClientID = "Server",
            Headers = new Dictionary<string, string>
            {
                { "Type", "Init" }
            },
            //Tells the client what its id is
            Payload = Encoding.UTF8.GetBytes(id.ToString())
        };
        await PacketIO.SendPacketAsync(client, pkt);
        Console.WriteLine($"Sent initial packet to client {id}");
    }
}