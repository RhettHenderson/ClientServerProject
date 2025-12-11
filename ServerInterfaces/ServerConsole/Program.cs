using Client_Server;
using static Common.Utility;
using System.Text;

class Program {
    static async Task Main(string[] args) {
        int port = 11111;
	    //Try to read port from env variable
	    var portEnv = Environment.GetEnvironmentVariable("APP_PORT");
	    if (!string.IsNullOrWhiteSpace(portEnv) && int.TryParse(portEnv, out var parsedPort)) {
		    port = parsedPort;
	    }
        var server = new Server();

        //Set this so it can display the progress bar
        Console.OutputEncoding = Encoding.UTF8;
        server.MessageReceived += (sender, msg) => Console.WriteLine($"{sender}: {msg}");

        server.Notification += (type, msg) =>
        {
            bool isProgress = msg.StartsWith("[PROGRESS]");
            bool isProgressEnd = msg.StartsWith("[PROGRESS_END]");
            string text = isProgress ? msg.Substring("[PROGRESS]".Length) : msg;

            Console.ForegroundColor = type switch {
                NotificationType.Info => ConsoleColor.Green,
                NotificationType.Warning => ConsoleColor.Yellow,
                NotificationType.Error => ConsoleColor.Red,
                _ => ConsoleColor.White,
            };

            if (isProgress) {
                // Overwrite the current line
                int width = Console.WindowWidth > 0 ? Console.WindowWidth - 1 : 80;
                if (text.Length < width)
                    text = text.PadRight(width);

                Console.Write("\r" + text);
            }
            else if (isProgressEnd) {
                // Move to the next line after progress ends
                Console.WriteLine();
            }
            else {
                // Normal log line, start a new line
                Console.WriteLine(text);
            }

            Console.ResetColor();
        };

        Console.WriteLine("Starting server...");
        server.Initialize(port);
        var serverTask = server.Start();

        // Start console loop here
        _ = Task.Run(async () =>
        {
            while (true) {
                var line = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("--"))
                    await server.HandleServerCommandAsync(line[2..].Trim());
                else
                    await server.SendMessageAsync(line.Trim());
            }
        });

        await serverTask;
        Console.WriteLine($"Server is listening on {server.listeningIp}:{server.listeningPort}.");
    }
}
