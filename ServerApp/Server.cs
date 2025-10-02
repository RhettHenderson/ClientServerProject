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
                var incoming = PacketIO.ReceivePacket(clientSocket);
                var clientID = incoming.ClientID;
                var headers = incoming.Headers;
                var text = Encoding.UTF8.GetString(incoming.Payload);
                if (text.Equals("\\fq"))
                {
                    Console.WriteLine("Client forcibly disconnected");
                    clientSocket.Shutdown(SocketShutdown.Both);
                    clientSocket.Close();
                    connected = false;
                }
                else if (text.Equals("\\q"))
                {
                    Console.WriteLine("Client requested to end connection");
                    clientSocket.Shutdown(SocketShutdown.Both);
                    clientSocket.Close();
                    connected = false;
                }
                else
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

    //Original read method, only accepts bytes
    static string ReadPacket(Socket clientSocket)
    {
        byte[] bytes = new byte[1024];
        var sb = new StringBuilder();
        while (true)
        {
            int numByte;
            try
            {
                numByte = clientSocket.Receive(bytes);
            }
            catch (SocketException)
            {
                return "\\fq";
            }
            catch (ObjectDisposedException)
            {
                return "\\fq";
            }

            //fq signifies force quit, q signifies quit
            if (numByte == 0)
            {
                return "\\fq";
            }
            sb.Append(Encoding.ASCII.GetString(bytes, 0, numByte));
            if (sb.ToString().Contains("<EOF>"))
            {
                break;
            }
        }
        return sb.ToString().Replace("<EOF>", "");
    }
}