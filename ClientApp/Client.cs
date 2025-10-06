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
        Socket socket = new Socket(ipAddr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        IPEndPoint localEndPoint = new IPEndPoint(ipAddr, 11111);

        socket.Connect(localEndPoint);
        Console.WriteLine("Socket connected to -> {0} < -", ipAddr.ToString());

        var recvTask = Task.Run(() => ReceiveLoopAsync(socket));

        while (true)
        {
            string? message = Console.ReadLine();
            if (message == null | message == "\\q")
            {
                break;
            }

            //The message we're sending to the server
            var packet = new Packet
            {
                ClientID = System.Environment.MachineName.ToString(),
                Headers = new Dictionary<string, string>
                {
                    { "Type", "Message" }
                },
                Payload = Encoding.UTF8.GetBytes(message)
            };

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
                    case ("Init"):
                        id = int.Parse(text);
                        Console.Title = $"Client {id}";
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