using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Collections.Generic;

namespace Client_Server;
class Server
{
    private static Socket listener;

    static void Main(string[] args)
    {
        ExecuteServer();
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
        /*
         * Packet incoming = new Packet();
         * var status = PacketIO.ReceivePacket(clientSocket, ref incoming);
         * if (status == ReceiveStatus.Ok)
         * {
         *  do stuff
         * }
         * else if (status == ReceiveStatus.Disconnected)
         * {
         * force quit
         * }
         * else if (status == ReceiveStatus.Error)
         * {
         * output an error occured receiving the last packet
         * }
         * 
         * */
        Packet incoming = new Packet();
        var status = PacketIO.ReceivePacket(clientSocket, ref incoming);
        var clientID = incoming.ClientID;
        var headers = incoming.Headers;
        var text = Encoding.UTF8.GetString(incoming.Payload);

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
}