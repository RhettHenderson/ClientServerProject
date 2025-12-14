using Common;
using System.Collections.Concurrent;
using System.Text;
using static Common.Utility;

namespace ServerApp;
internal class ServerAuth {

    // === ServerAuth-specific Fields ===
    private ConcurrentDictionary<string, string> passwords = new();        // username -> password hash
    private int currentAuthCode = 111111;
    private static string passwordsFile = "passwords.txt";

    // === Server Fields ===
    public event Action<NotificationType, string> Notification;
    private ConcurrentDictionary<string, int> names;
    ConcurrentDictionary<int, Server.Conn> clients;
    private VoiceChatManager vc;
    private ConcurrentDictionary<int, string> clientPlatforms;

    //Constructor
    public ServerAuth(Action<NotificationType, string> notification, ConcurrentDictionary<string, int> names,
        ConcurrentDictionary<int, Server.Conn> clients, VoiceChatManager vc, ConcurrentDictionary<int, string> clientPlatforms) {
        Notification = notification;
        this.names = names;
        this.clients = clients;
        this.vc = vc;
        this.clientPlatforms = clientPlatforms;
    }
    internal void Load() {
        try {
            using (var fileReader = File.ReadLines(passwordsFile).GetEnumerator()) {
                while (fileReader.MoveNext()) {
                    var line = fileReader.Current;
                    var parts = line.Split(", ");
                    if (parts.Length != 2) {
                        Notification?.Invoke(NotificationType.Info, $"Invalid line in password file: {line}");
                        continue;
                    }
                    passwords[parts[0]] = parts[1];
                }
            }
        }
        catch (FileNotFoundException) {
            Notification?.Invoke(NotificationType.Warning, "Password file not found. Users cannot connect until one is made. " +
                "\nRun --create <name> <password> to create the file and the first user.");
        }
        catch (Exception e) {
            Notification?.Invoke(NotificationType.Error, $"Unexepected exception: {e.Message}");
        }
    }

    internal async Task CreateUserAsync(string name, string password) {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(password)) {
            Notification?.Invoke(NotificationType.Warning, "Name or password cannot be empty. Aborting.");
            return;
        }
        if (passwords.ContainsKey(name)) {
            Notification?.Invoke(NotificationType.Warning, "User with that name already exists. Aborting.");
            return;
        }
        var passwordHash = HashPasswordPBKDF2(password);
        passwords[name] = passwordHash;
        if (!File.Exists(passwordsFile)) {
            Notification?.Invoke(NotificationType.Info, "Created passwords.txt file.");
        }
        //AppendAllText creates file if it doesn't exist, we just fire the notification first
        await File.AppendAllTextAsync(passwordsFile, $"{name}, {passwordHash}\n");
        Notification?.Invoke(NotificationType.Info, $"Created user {name} successfully.");
    }

    internal async Task<AuthenticationStatus> AuthenticateClient(string username, string password, int clientId) {
        if (names.TryGetValue(username, out var existingId)) {
            if (!clients.ContainsKey(existingId)) {
                names.TryRemove(username, out _);
            }
            else if (existingId != clientId) {
                return AuthenticationStatus.AlreadyLoggedIn;
            }
        }
        if (!passwords.TryGetValue(username, out var storedHash)) {
            return AuthenticationStatus.WrongUsername;
        }
        if (!VerifyPasswordPBKDF2(password, storedHash)) {
            return AuthenticationStatus.WrongPassword;
        }
        return AuthenticationStatus.Success;
    }

    internal async Task RemoveClient(int id) {
        if (clients.TryRemove(id, out var conn)) {
            try { conn.io.Dispose(); } catch { }
            try { conn.socket.Dispose(); } catch { }
        }


        var username = names.FirstOrDefault(kvp => kvp.Value == id).Key;
        if (!string.IsNullOrEmpty(username)) {
            names.TryRemove(username, out _);
        }

        if (vc.pendingVoiceClientId == id) {
            vc.pendingVoiceClientId = null;
            vc.voiceInviteCts?.Cancel();
            vc.voiceInviteCts = null;
        }

        clientPlatforms.TryRemove(id, out _);
        vc.udpClients.TryRemove(id, out _);
        vc.voiceParticipants.TryRemove(id, out _);
    }

    internal async Task RemoveClient(string name) { }

    internal async Task ShowAuthCode() {
        GenerateAuthCode(ref currentAuthCode);
        Notification?.Invoke(NotificationType.Info, $"Authentication Code: {currentAuthCode}");
    }

    internal async Task<bool> CheckAuthCode(int code) {
        return code == currentAuthCode;
    }

    internal enum AuthenticationStatus {
        Success,
        Failed,
        WrongPassword,
        WrongUsername,
        WrongCode,
        UsernameTaken,
        AlreadyLoggedIn
    }

    internal async Task ListUsers() {
        if (this.passwords.Count == 0) {
            Notification?.Invoke(NotificationType.Info, "No users registered.");
            return;
        }
        StringBuilder sb = new StringBuilder("Registered Users:");
        foreach (var user in passwords.Keys) {
            sb.Append($"\n- {user}");
        }
        Notification?.Invoke(NotificationType.Info, sb.ToString());
    }
}
