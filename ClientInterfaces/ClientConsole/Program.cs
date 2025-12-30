using ClientApp;
using System.Net.Security;
using System.Text;
using static Common.Utility;

class Program {

    //TODO: Clean up Client Console command handling and Main method
    //TODO: Figure out why voice command triggers the SocketException every time

    static async Task Main(string[] args) {
        // === Connection Setup ===
        Console.Write("Enter server host or press Enter for this device's IP: ");
        var host = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(host)) {
            host = GetLocalIP();
        }

        int port = 11111;
        var portEnv = Environment.GetEnvironmentVariable("APP_PORT");
        if (!string.IsNullOrWhiteSpace(portEnv) && int.TryParse(portEnv, out var parsedPort)) {
            port = parsedPort;
        }

        var client = new Client();
        //Wires all the console logs to the events
        InitEvents(client);
        Console.WriteLine($"Connecting to server at {host}:{port}");
        await client.ConnectAsync(host, port);

        // === Login Setup ===
        if (client._stream is not SslStream) {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("Only login over an unencrypted connection in a dev environment, otherwise password details can be sniffed by attackers. " +
                "Continue to login on unencrypted connection? [y/n]: ");
            Console.ResetColor();
            var key = Console.ReadKey(intercept: true);
            Console.WriteLine(key.KeyChar);
            if (key.KeyChar != 'y' && key.KeyChar != 'Y') {
                Console.WriteLine("Exiting...");
                return;
            }
        }
        string username = "";
        string password = "";
        Console.Write("Username: ");
        username = Console.ReadLine() ?? "Client";
        Console.Write("Password: ");
        password = ReadPassword();

        await client.LoginAsync(username, password);

        Console.Title = client.NameOrThrow();

        while (true) {
            var line = Console.ReadLine();
            if (line is null || line == "\\q") break;
            try {
                if (line.StartsWith("--")) {
                    await client.HandleClientCommandAsync(line[2..].Trim());
                }

                else {
                    //Move cursor to start of line and add "You: "
                    var pos = Console.GetCursorPosition();
                    Console.SetCursorPosition(0, pos.Top - 1);
                    Console.WriteLine($"You: {line}");
                    await client.SendMessageAsync(line);
                }
            }
            catch (Exception e) {
                Console.WriteLine(e.Message);
                continue;
            }
        }
    }

    static string ReadPassword() {
        var sb = new StringBuilder();
        while (true) {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && sb.Length > 0) { sb.Remove(sb.Length - 1, 1); Console.Write("\b \b"); }
            else { sb.Append(key.KeyChar); Console.Write('*'); }
        }
        Console.WriteLine();
        return sb.ToString();
    }

    static void InitEvents(Client client) {
        object consoleLock = new object();

        client.MessageReceived += (sender, msg) => Console.WriteLine($"{sender}: {msg}");
        client.WhisperReceived += (from, msg) => Console.WriteLine($"(Whisper) {from}: {msg}");
        client.IdAssigned += id => Console.Title = $"Client {id}";
        client.Error += msg => {
            Console.WriteLine($"\n\nError: {msg} Press any key to exit...");
            Console.ReadKey();
            Environment.Exit(1);
        };
        client.Notification += (type, msg) => {
            Console.ForegroundColor = type switch {
                NotificationType.Info => ConsoleColor.Green,
                NotificationType.Warning => ConsoleColor.Yellow,
                NotificationType.Error => ConsoleColor.Red,
                _ => ConsoleColor.White,
            };
            Console.WriteLine($"{msg}");
            Console.ResetColor();
        };
        client.Disconnected += () => {
            Console.WriteLine("\n\nServer disconnected. Press any key to exit...");
            Console.ReadKey();
            Environment.Exit(1);
        };
    }
}
