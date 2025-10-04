using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using System.Buffers.Binary;
using Client_Server;

class Client
{
    //static void Main(string[] args)
    //{
    //    ExecuteClient();
    //}
    public static async Task Main(string[] args)
    {
        await ExecuteClientAsync();
    }

    static void ExecuteClient()
    {
        Console.Title = "Client";
        //var hosts = ReadHostsFile("Assets/Client-Server Architecture/hosts.txt");
        try
        {
            //Set remote endpoint
            Console.Write("Enter the server IP address, a known hostname, or hit enter for localhost: ");
            string serverIP = Console.ReadLine();
            IPAddress ipAddr;
            IPHostEntry ipHost = Dns.GetHostEntry(Dns.GetHostName());
            ipAddr = ipHost.AddressList[0];
            if (serverIP != "")
            {
                try
                {
                    ipAddr = IPAddress.Parse(serverIP);
                }
                catch (FormatException)
                {
                    Console.WriteLine("Invalid IP address format. Please enter a valid IP address.");
                    return;
                }
            }
            IPEndPoint localEndPoint = new IPEndPoint(ipAddr, 11111);

            //Create TCP Socket
            Socket sender = new Socket(ipAddr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                sender.Connect(localEndPoint);

                Console.WriteLine("Socket connected to -> {0} < -", sender.RemoteEndPoint.ToString());
                Console.WriteLine("Enter a message to send to the server, or enter \\q to quit:");
                while (true)
                {
                    string message = Console.ReadLine();
                    Packet pkt = new Packet
                    {
                        ClientID = System.Environment.MachineName.ToString(),
                        Headers = new Dictionary<string, string>
                        {
                            { "Type", "Message" }
                        },
                        Payload = Encoding.UTF8.GetBytes(message)
                    };
                    PacketIO.SendPacket(sender, pkt);

                    var incoming = new Packet();
                    var status = PacketIO.ReceivePacket(sender, ref incoming);
                    var text = Encoding.UTF8.GetString(incoming.Payload);
                    Console.WriteLine("Message from Server -> {0} < -", text);
                }
                Console.WriteLine("Closing connection");
                sender.Shutdown(SocketShutdown.Both);
                sender.Close();
            }
            catch (ArgumentNullException ane)
            {

                Console.WriteLine("ArgumentNullException : {0}", ane.ToString());
            }
            catch (SocketException se)
            {

                Console.WriteLine("SocketException : {0}", se.ToString());
            }
            catch (Exception e)
            {
                Console.WriteLine("Unexpected exception : {0}", e.ToString());
            }
        }
        catch (Exception e)
        {

            Console.WriteLine(e.ToString());
        }
    }

    private static async Task ExecuteClientAsync()
    {
        IPAddress ipAddr;
        IPHostEntry ipHostEntry = Dns.GetHostEntry(Dns.GetHostName());
        ipAddr = ipHostEntry.AddressList[0];
        Socket sender = new Socket(ipAddr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        IPEndPoint localEndPoint = new IPEndPoint(ipAddr, 11111);

        sender.Connect(localEndPoint);
        Console.WriteLine("Socket connected to -> {0} < -", sender.RemoteEndPoint.ToString());
        Console.WriteLine("Enter a message to send to the server, or enter \\q to quit:");
        while (true)
        {
            string message = Console.ReadLine();
            Packet pkt = new Packet
            {
                ClientID = System.Environment.MachineName.ToString(),
                Headers = new Dictionary<string, string>
                {
                    { "Type", "Message" }
                },
                Payload = Encoding.UTF8.GetBytes(message)
            };
            PacketIO.SendPacketAsync(sender, pkt);
            var (status, packet) = await PacketIO.ReceivePacketAsync(sender);
            if (status == PacketStatus.Disconnected)
            {
                Console.WriteLine("Server disconnected");
                continue;
            }
            if (status == PacketStatus.Error)
            {
                Console.WriteLine("Error receiving packet");
                break;
            }

            var text = Encoding.UTF8.GetString(packet.Payload);
            Console.WriteLine("Message from Server -> {0} < -", text);
        }
    }

    static Dictionary<string, string> ReadHostsFile(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            // remove optional trailing comma
            if (line.EndsWith(",")) line = line.Substring(0, line.Length - 1);

            // split on the first colon
            int i = line.IndexOf(':');
            if (i < 0) continue; // or throw for strict parsing

            var host = line.Substring(0, i).Trim();
            var ip = line.Substring(i + 1).Trim();

            if (host.Length == 0 || ip.Length == 0) continue;
            map[host] = ip; // last wins
        }

        return map;
    }
}