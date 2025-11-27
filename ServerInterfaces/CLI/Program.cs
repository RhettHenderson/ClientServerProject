using Client_Server;
using Common;

class Program
{
    static async Task Main(string[] args)
    {
        var server = new Server();
        server.MessageReceived += (sender, msg) => Console.WriteLine($"{sender}: {msg}");
        int port = 11111;
        server.Notification += (type, msg) =>
        {
            Console.ForegroundColor = type switch
            {
                NotificationType.Info => ConsoleColor.Green,
                NotificationType.Warning => ConsoleColor.Yellow,
                NotificationType.Error => ConsoleColor.Red,
                _ => ConsoleColor.White,
            };
            Console.WriteLine($"{msg}");
            Console.ResetColor();
        };

        Console.WriteLine("Starting server...");
        string? serverIP = Console.ReadLine();
        await server.ExecuteServerAsync(port, serverIP);
        Console.WriteLine("Server has started listening.");
    }
}
