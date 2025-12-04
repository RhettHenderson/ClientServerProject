using Client_Server;
using Common;

class Program {
    static async Task Main(string[] args) {
        var server = new Server();
        server.MessageReceived += (sender, msg) => Console.WriteLine($"{sender}: {msg}");
        int port = 11111;

	//Try to read port from env variable
	var portEnv = Environment.GetEnvironmentVariable("APP_PORT");
	if (!string.IsNullOrWhiteSpace(portEnv) && int.TryParse(portEnv, out var parsedPort)) {
		port = parsedPort;
	}
        server.Notification += (type, msg) => {
            Console.ForegroundColor = type switch {
                NotificationType.Info => ConsoleColor.Green,
                NotificationType.Warning => ConsoleColor.Yellow,
                NotificationType.Error => ConsoleColor.Red,
                _ => ConsoleColor.White,
            };
            Console.WriteLine($"{msg}");
            Console.ResetColor();
        };

        Console.WriteLine("Starting server...");
        await server.ExecuteServerAsync(port);
        Console.WriteLine($"Server is listening on {server.listeningIp}:{server.listeningPort}.");
    }
}
