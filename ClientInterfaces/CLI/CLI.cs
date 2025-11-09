using System.Text;
using Common;

namespace Client_Server;

class CLI
{
    static async Task Main(string[] args)
    {
        var client = new Client();
        client.MessageReceived += (sender, msg) => Console.WriteLine($"{sender}: {msg}");
        client.WhisperReceived += (from, msg) => Console.WriteLine($"(Whisper) {from}: {msg}");
        client.IdAssigned += id => Console.Title = $"Client {id}";
        client.CommandsReceived += cmds => Console.WriteLine("Received commands list.");
        client.Error += msg =>
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {msg}");
            Console.ResetColor();
        };
        client.Notification += (type, msg) =>
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

        Console.Write("Enter server host or press Enter for localhost: ");
        string host = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(host))
        {
            host = "localhost";
        }
        //Check for auth file to skip asking for username and password
        string username = "";
        string password = "";
        if (File.Exists(client.authFile))
        {
            using (var fileReader = File.ReadLines(client.authFile).GetEnumerator())
            {
                while (fileReader.MoveNext())
                {
                    var line = fileReader.Current;
                    var parts = line.Split(", ");
                    if (parts.Length != 2)
                    {
                        Console.WriteLine($"Invalid line in auth file");
                        continue;
                    }
                    username = parts[0];
                    password = parts[1];
                }
            }
        }
        else
        {
            Console.Write("Username: ");
            username = Console.ReadLine() ?? "Client";
            Console.Write("Password: ");
            password = ReadPassword();
        }
        var hash = Utility.SHA256Hash(password);

        await client.ConnectAsync(host, 11111, username, hash);

        Console.Title = client.Name;

        while (true)
        {
            var line = Console.ReadLine();
            if (line is null || line == "\\q") break;

            if (line.StartsWith("--file"))
            {
                string localPath = null;
                string remoteFilename = null;
                string saveLocation = null;
                args = line[7..].Split(" ");
                if (args.Length < 1 || args.Length > 5)
                {
                    Console.WriteLine("Usage: --file <localPath> [-r remoteFilename] [-s saveLocation]");
                    continue;
                }
                else
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (i == 0)
                        {
                            localPath = args[i];
                        }
                        else if (args[i] == "-r" && i + 1 < args.Length)
                        {
                            remoteFilename = args[i + 1];
                            i++;
                            continue;
                        }
                        else if (args[i] == "-s" && i + 1 < args.Length)
                        {
                            saveLocation = args[i + 1];
                            i++;
                            continue;
                        }
                    }
                }
                await PacketIO.SendFileAsync(client._stream, localPath!, client.pendingResponses, remoteFilename, saveLocation);
            }
            else if (line.StartsWith("--"))
            {
                 await client.SendCommandAsync(line[2..]);
            }
            else
            {
                await client.SendMessageAsync(line);
            }
        }
    }

    static string ReadPassword()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && sb.Length > 0) { sb.Remove(sb.Length - 1, 1); Console.Write("\b \b"); }
            else { sb.Append(key.KeyChar); Console.Write('*'); }
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
