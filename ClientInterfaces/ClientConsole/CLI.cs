using Common;
using System;
using System.Text;
using static Common.Utility;

namespace Client_Server;

class CLI {
    static async Task Main(string[] args) {
        Console.Write("Enter server host or press Enter for this device's IP: ");
        string host = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(host)) {
            host = GetLocalIP();
        }
        string username = "";
        string password = "";
        Console.Write("Username: ");
        username = Console.ReadLine() ?? "Client";
        Console.Write("Password: ");
        password = ReadPassword();
        var hash = SHA256Hash(password);

        int port = 11111;
        var portEnv = Environment.GetEnvironmentVariable("APP_PORT");
        if (!string.IsNullOrWhiteSpace(portEnv) && int.TryParse(portEnv, out var parsedPort)) {
            port = parsedPort;
        }
        var client = new Client();
        //Wires all the console logs to the events
        InitEvents(client);
        client.Initialize(host, port);
        Console.WriteLine($"Connecting to server at {host}:{port}...");
        await client.ConnectAsync(username, hash);

        Console.Title = client.Name;

        while (true) {
            var line = Console.ReadLine();
            if (line is null || line == "\\q") break;
            try {
                if (line.StartsWith("--file")) {
                    if (line.Length < 8) {
                        Console.WriteLine("Usage: --file <localPath> [-r remoteFilename] [-s saveLocation]");
                        continue;
                    }
                    string? localPath = null;
                    string? remoteFilename = null;
                    string? saveLocation = null;
                    args = line[7..].Split(" ");
                    if (args.Length < 1 || args.Length > 5) {
                        Console.WriteLine("Usage: --file <localPath> [-r remoteFilename] [-s saveLocation]");
                        continue;
                    }
                    else {
                        for (int i = 0; i < args.Length; i++) {
                            if (i == 0) {
                                localPath = args[i];
                            }
                            else if (args[i] == "-r" && i + 1 < args.Length) {
                                remoteFilename = args[i + 1];
                                i++;
                                continue;
                            }
                            else if (args[i] == "-s" && i + 1 < args.Length) {
                                saveLocation = args[i + 1];
                                i++;
                                continue;
                            }
                        }
                    }
                    await FileTransfer.SendFileAsync(client._stream, localPath!, client.pendingResponses, remoteFilename, saveLocation);
                }
                else if (line.StartsWith("--voice")) {
                    var inviteeText = line.Length > 8 ? line[8..] : string.Empty;
                    var invitees = inviteeText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (invitees.Length == 0) {
                        invitees = new[] { "Server" };
                    }
                    await client.StartVoiceRoom(invitees);
                }
                else if (line.StartsWith("--disconnect") || line.StartsWith("--dc")) {
                    await client.LeaveVoiceRoom("Client requested UDP disconnect.");
                }
                else if (line.StartsWith("--")) {
                    await client.SendCommandAsync(line[2..]);
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
                break;
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
        client.CommandsReceived += cmds => Console.WriteLine("Received commands list.");
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
