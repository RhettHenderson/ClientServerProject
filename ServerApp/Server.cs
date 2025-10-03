using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.WebSockets;

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

    static void ExecuteServer()
    {
        Console.Title = "Server";
        try
        {
            InitListener();
            //StartConsoleListener();
            Socket clientSocket = null;
            bool connected = false;
            while (true)
            {
                if (!connected)
                {
                    clientSocket = WaitForConnection();
                    connected = true;
                }

                //Processes the packet and modifies the original values of clientSocket and connected
                ProcessPacket(ref clientSocket, ref connected);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
        }
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

    static Socket WaitForConnection()
    {
        Console.WriteLine("Waiting for connection ... ");
        Socket clientSocket = listener.Accept();
        Console.WriteLine("Connection accepted from -> {0} < -", clientSocket.RemoteEndPoint.ToString());
        return clientSocket;
    }

    static void ProcessPacket(ref Socket clientSocket, ref bool connected)
    {
        Packet incoming = new Packet();
        var status = PacketIO.ReceivePacket(clientSocket, ref incoming);

        if (status == PacketStatus.Disconnected)
        {
            Console.WriteLine("Client forcibly disconnected");
            clientSocket.Shutdown(SocketShutdown.Both);
            clientSocket.Close();
            connected = false;
            return;
        }
        else if (status == PacketStatus.Error)
        {
            Console.WriteLine("An error occured trying to receive the last packet. Closing connection.");
            clientSocket.Shutdown(SocketShutdown.Both);
            clientSocket.Close();
            connected = false;
            return;
        }
        //If we reach here, status is PacketStatus.Ok
        var clientID = incoming.ClientID;
        var headers = incoming.Headers;
        var text = Encoding.UTF8.GetString(incoming.Payload);
        int commandStatus = ReadCommand(text);
        if (commandStatus == 2)
        {
            clientSocket.Shutdown(SocketShutdown.Both);
            clientSocket.Close();
            connected = false;
        }
        else if (commandStatus == 1)
        {
            return;
        }
        else if (commandStatus == 0)
        {
            Console.WriteLine($"Received Message from {clientID}: {text}");
            Packet reply = new Packet
            {
                ClientID = "Server",
                Headers = new Dictionary<string, string> { { "Type", "Ack" } },
                Payload = Encoding.ASCII.GetBytes($"Ack: {text}")
            };
            PacketIO.SendPacket(clientSocket, reply);
        }
        else
        {
            Console.WriteLine($"Unexpected return value from ReadCommand: {commandStatus}");
            return;
        }
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
                Console.WriteLine("Awaiting connection...");
                client = await listener.AcceptAsync();
                client.NoDelay = true;
                int id = Interlocked.Increment(ref nextID);
                clients[id] = client;
                Console.WriteLine($"Client with ID {id} connected using IP {client.RemoteEndPoint.ToString()}.");
                HandleClientAsync(id);
            }
            catch (OperationCanceledException e) 
            {
                Console.WriteLine(e.ToString());
                break; 
            }

        }
    }

    private static async Task<bool> ProcessPacketAsync(int id)
    {
        Socket client = clients[id];
        Packet incoming = new Packet();
        var status = await PacketIO.ReceivePacketAsync_Compat(client, p => incoming = p);

        if (status == PacketStatus.Disconnected)
        {
            Console.WriteLine($"Client {id} forcibly disconnected");
            client.Shutdown(SocketShutdown.Both);
            client.Close();
            return false;
        }
        else if (status == PacketStatus.Error)
        {
            Console.WriteLine("An error occured trying to receive the last packet. Closing connection.");
            client.Shutdown(SocketShutdown.Both);
            client.Close();
            return false;
        }
        //if we reach here status is Ok
        var clientID = incoming.ClientID;
        var headers = incoming.Headers;
        var text = Encoding.UTF8.GetString(incoming.Payload);
        int commandStatus = ReadCommand(text);

        if (commandStatus == 2)
        {
            client.Shutdown(SocketShutdown.Both);
            client.Close();
        }
        else if (commandStatus == 1)
        {
            return true;
        }
        else if (commandStatus == 0)
        {
            Console.WriteLine($"Received Message from {clientID}: {text}");
            Packet reply = new Packet
            {
                ClientID = "Server",
                Headers = new Dictionary<string, string> { { "Type", "Ack" } },
                Payload = Encoding.ASCII.GetBytes($"Ack: {text}")
            };
            PacketIO.SendPacketAsync(client, reply);
            return true;
        }
        else
        {
            Console.WriteLine($"Unexpected return value from ReadCommand: {commandStatus}");
            return false;
        }
        return false;
    }

    private static async Task HandleClientAsync(int id)
    {
        Console.WriteLine($"Client #{id} handler started.");
        while (true)
        {
            bool keepAlive = await ProcessPacketAsync(id);
            if (!keepAlive) break;
        }
    }

    private static async Task SendPacketAsync(int id)
    {
        Socket client = clients[id];
    }

    public static async Task BroadcastAsync(byte[] data, int? excludeID = null)
    {
        var dead = new List<int>();
        foreach (var client in clients)
        {
            if (excludeID.HasValue && client.Key == excludeID.Value) continue;
            try
            {
                await client.Value.SendAsync(data, SocketFlags.None);
            }
            catch
            {
                dead.Add(client.Key);
            }
        }

        foreach (var id in dead)
        {
            clients.TryRemove(id, out _);
        }
    }
}