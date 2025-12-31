// ChatWindow.xaml.cs
using ClientApp;
using Common;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClientGUI;

public partial class ChatWindow : Window {
    private readonly Client _client;

    private bool hasAttachment;
    private string attachmentPath;

    private const string RoomKey = "__ROOM__";
    private string _activeConversationKey = RoomKey;

    private readonly ObservableCollection<UserListItem> _users = new();
    private readonly Dictionary<string, ObservableCollection<string>> _histories =
        new(System.StringComparer.OrdinalIgnoreCase);

    private sealed class UserListItem {
        public string Key { get; }
        public string DisplayName { get; }
        public bool IsRoom => Key == RoomKey;

        public UserListItem(string key, string displayName) {
            Key = key;
            DisplayName = displayName;
        }
    }

    public ChatWindow(Client client, string serverIP) {
        InitializeComponent();

        _client = client;
        _client.MessageReceived += Client_MessageReceived;
        _client.ClientsUpdated += roster => {
            Dispatcher.BeginInvoke(() => UpdateConnectedUsers(roster.ToArray()));
        };
        _client.WhisperReceived += Client_WhisperReceived;
        _client.Error += (msg) => {
            Dispatcher.BeginInvoke(() => {
                MessageBox.Show(this, msg, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            });
        };
        _client.Notification += (type, msg) => {
            Dispatcher.BeginInvoke(() => {
                AddToHistory(RoomKey, $"[Notification]: {msg}");
            });
        };

        // Users list UI
        UsersListBox.ItemsSource = _users;

        // Always keep Room at top
        _users.Add(new UserListItem(RoomKey, "Room"));
        EnsureHistory(RoomKey);
        UpdateConnectedUsers(_client.ConnectedClients);
        // Default to room
        UsersListBox.SelectedIndex = 0;
        SwitchConversation(RoomKey);

        HeaderText.Text = $"Logged in as {_client.Name} | Server: {serverIP}";
        MessageTextBox.Focus();
    }

    private void UsersListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (UsersListBox.SelectedItem is not UserListItem item)
            return;

        SwitchConversation(item.Key);
    }

    private void UsersListBox_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e) {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem) {
            dep = VisualTreeHelper.GetParent(dep);
        }

        if (dep is not ListBoxItem lbi || lbi.DataContext is not UserListItem item) {
            return;
        }

        UsersListBox.SelectedItem = item;
        ShowUserContextMenu(item, lbi);
        e.Handled = true;
    }

    private void ShowUserContextMenu(UserListItem item, FrameworkElement placementTarget) {
        var menu = new ContextMenu();

        if (item.IsRoom) {
            var callRoom = new MenuItem { Header = "Call Room (Voice)" };
            callRoom.Click += async (_, _) => await CallRoomVoiceAsync();
            menu.Items.Add(callRoom);

            var callServer = new MenuItem { Header = "Call Server (Voice)" };
            callServer.Click += async (_, _) => await CallServerVoiceAsync();
            menu.Items.Add(callServer);
        }
        else {
            var callUser = new MenuItem { Header = $"Call {item.Key} (Voice)" };
            callUser.Click += async (_, _) => await CallUserVoiceAsync(item.Key);
            menu.Items.Add(callUser);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "More options coming...", IsEnabled = false });

        placementTarget.ContextMenu = menu;
        menu.PlacementTarget = placementTarget;
        menu.IsOpen = true;
    }


    private void SwitchConversation(string key) {
        _activeConversationKey = key;
        EnsureHistory(key);

        MessagesListBox.ItemsSource = _histories[key];

        if (key == RoomKey) {
            ConversationText.Text = "Room";
            MessageTextBox.Tag = "Enter a message...";
        }
        else {
            ConversationText.Text = $"Whisper: {key}";
            MessageTextBox.Tag = $"Whisper to {key}...";
        }

        // Attachments only allowed in Room for now (client -> server)
        bool inRoom = (key == RoomKey);
        AttachButton.ToolTip = inRoom
            ? "Attach a file (sends to server)"
            : $"Attach a file (sends to {key}";
        //Remove attachment if switching out of room
        if (!inRoom)
            RemoveAttachment_Click(this, new RoutedEventArgs());

        ScrollToBottom();
        MessageTextBox.Focus();
    }

    private async void MessageTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) {
        if (e.Key == System.Windows.Input.Key.Enter) {
            e.Handled = true;
            await SendCurrentAsync();
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e) {
        await SendCurrentAsync();
    }

    // NOTE: Attach_Click remains for your future re-enable work; attach button is disabled in XAML for now.
    private void Attach_Click(object sender, RoutedEventArgs e) {
        var dlg = new OpenFileDialog {
            Title = "Select a file to attach",
            CheckFileExists = true,
            Multiselect = false
        };

        bool? ok = dlg.ShowDialog(this);
        if (ok != true)
            return;

        attachmentPath = dlg.FileName;   // full path
        hasAttachment = true;

        // Update UI indicator
        AttachmentFileNameText.Text = Path.GetFileName(attachmentPath);
        AttachmentBanner.Visibility = Visibility.Visible;
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e) {
        attachmentPath = null;
        hasAttachment = false;

        AttachmentFileNameText.Text = "";
        AttachmentBanner.Visibility = Visibility.Collapsed;
    }

    private async System.Threading.Tasks.Task SendCurrentAsync() {
        // If a file is attached, send it
        if (hasAttachment && !string.IsNullOrWhiteSpace(attachmentPath)) {
            if (_activeConversationKey == RoomKey) {
                var ok = await FileTransfer.SendFileAsync(_client.StreamOrThrow(), attachmentPath, _client.pendingResponses, _client.NameOrThrow(), "Server");
                AddToHistory(RoomKey, ok
                    ? $"(file) You: {Path.GetFileName(attachmentPath)}"
                    : $"(file) Failed to send: {Path.GetFileName(attachmentPath)}");

                RemoveAttachment_Click(this, new RoutedEventArgs());
            }
            else {
                string targetUser = _activeConversationKey;
                var ok = await FileTransfer.SendFileAsync(_client.StreamOrThrow(), attachmentPath, _client.pendingResponses, _client.NameOrThrow(), _activeConversationKey);
                AddToHistory(targetUser, ok
                    ? $"(file) You: {Path.GetFileName(attachmentPath)}"
                    : $"(file) Failed to send: {Path.GetFileName(attachmentPath)}");

                if (ok) BumpUserToTop(targetUser);
                RemoveAttachment_Click(this, new RoutedEventArgs());
            }
        }

        string message = MessageTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(message))
            return;

        if (_activeConversationKey == RoomKey) {
            await _client.SendMessageAsync(message);
            AddToHistory(RoomKey, $"You: {message}");
        }
        else {
            string targetUser = _activeConversationKey;
            await _client.SendWhisperAsync(targetUser, message);
            AddToHistory(targetUser, $"You: {message}");

            // When you message a user, move them to the top (below Room)
            BumpUserToTop(targetUser);
        }

        MessageTextBox.Clear();
        MessageTextBox.Focus();
    }

    private void Client_MessageReceived(string sender, string text) {
        Dispatcher.BeginInvoke(() => {
            AddToHistory(RoomKey, $"{sender}: {text}");
        });
    }

    private async void VoiceButton_Click(object sender, RoutedEventArgs e) {
        try {
            if (_activeConversationKey == RoomKey) {
                await CallRoomVoiceAsync();
            }
            else {
                await CallUserVoiceAsync(_activeConversationKey);
            }
        }
        catch (Exception ex) {
            AddToHistory(RoomKey, $"(voice) Error: {ex.Message}");
        }
    }

    private async void VoiceServerButton_Click(object sender, RoutedEventArgs e) {
        try {
            await CallServerVoiceAsync();
        }
        catch (Exception ex) {
            AddToHistory(RoomKey, $"(voice) Error: {ex.Message}");
        }
    }

    private async Task CallRoomVoiceAsync() {
        var invitees = _client.ConnectedClients
            .Where(ci => ci != null && !string.IsNullOrWhiteSpace(ci.Name))
            .Select(ci => ci.Name)
            .Where(n => !string.Equals(n, _client.Name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        invitees.Add("Server");

        AddToHistory(RoomKey, "(voice) Starting Room call...");
        await _client.StartVoiceRoom(invitees);
    }

    private async Task CallServerVoiceAsync() {
        AddToHistory(RoomKey, "(voice) Calling server...");
        await _client.StartVoiceRoom(new[] { "Server" });
    }

    private async Task CallUserVoiceAsync(string targetUser) {
        EnsureUserExists(targetUser);
        AddToHistory(targetUser, $"(voice) Calling {targetUser}...");
        await _client.StartVoiceRoom(new[] { targetUser });
        BumpUserToTop(targetUser);
    }

    private void Client_WhisperReceived(string sender, string text) {
        Dispatcher.BeginInvoke(() => {
            EnsureUserExists(sender);
            AddToHistory(sender, $"{sender}: {text}");
        });
    }

    private void UpdateConnectedUsers(IEnumerable<ClientInfo> rosterInServerOrder) {
        // Normalize + remove self
        var incoming = rosterInServerOrder
            .Where(ci => ci is not null)
            .Where(ci => !string.IsNullOrWhiteSpace(ci.Name))
            .Where(ci => !string.Equals(ci.Name, _client.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Track incoming names
        var incomingByName = incoming.ToDictionary(ci => ci.Name, ci => ci, StringComparer.OrdinalIgnoreCase);
        var incomingNames = new HashSet<string>(incomingByName.Keys, StringComparer.OrdinalIgnoreCase);

        // 1) Remove users no longer present (keep Room pinned)
        for (int i = _users.Count - 1; i >= 0; i--) {
            if (_users[i].IsRoom)
                continue;

            if (!incomingNames.Contains(_users[i].Key))
                _users.RemoveAt(i);
        }

        // 2) Update display for users that still exist (optional; keeps list order)
        // If you only show Name, you can set display = info.Name.
        for (int i = 0; i < _users.Count; i++) {
            var u = _users[i];
            if (u.IsRoom) continue;

            if (incomingByName.TryGetValue(u.Key, out var info)) {
                // Choose how you want to show them:
                // string display = info.Name;
                string display = string.IsNullOrWhiteSpace(info.Platform)
                    ? info.Name
                    : $"{info.Name} ({info.Platform})";

                if (!string.Equals(u.DisplayName, display, StringComparison.Ordinal))
                    _users[i] = new UserListItem(u.Key, display); // replace in-place, no reordering

                EnsureHistory(u.Key);
            }
        }

        // 3) Append brand-new users in server-provided order
        foreach (var info in incoming) {
            if (_users.Any(u => string.Equals(u.Key, info.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            string display = string.IsNullOrWhiteSpace(info.Platform)
                ? info.Name
                : $"{info.Name} ({info.Platform})";

            _users.Add(new UserListItem(info.Name, display));
            EnsureHistory(info.Name);
        }

        // 4) If currently whispering to someone who disappeared, fall back to Room
        if (_activeConversationKey != RoomKey &&
            !_users.Any(u => string.Equals(u.Key, _activeConversationKey, StringComparison.OrdinalIgnoreCase))) {
            UsersListBox.SelectedIndex = 0;
        }
    }

    private void EnsureUserExists(string user) {
        if (_users.Any(x => x.IsRoom == false && string.Equals(x.Key, user, System.StringComparison.OrdinalIgnoreCase)))
            return;

        _users.Add(new UserListItem(user, user));
        EnsureHistory(user);
    }

    private void BumpUserToTop(string user) {
        var item = _users.FirstOrDefault(x =>
            !x.IsRoom && string.Equals(x.Key, user, System.StringComparison.OrdinalIgnoreCase));

        if (item is null)
            return;

        int idx = _users.IndexOf(item);
        if (idx <= 1) // already at top (below Room)
            return;

        _users.Move(idx, 1);
    }

    private void EnsureHistory(string key) {
        if (!_histories.ContainsKey(key))
            _histories[key] = new ObservableCollection<string>();
    }

    private void AddToHistory(string conversationKey, string line) {
        EnsureHistory(conversationKey);
        _histories[conversationKey].Add(line);

        // If we're currently viewing this conversation, keep it scrolled
        if (string.Equals(_activeConversationKey, conversationKey, System.StringComparison.OrdinalIgnoreCase)) {
            ScrollToBottom();
        }
    }

    private void ScrollToBottom() {
        if (MessagesListBox.ItemsSource is not ObservableCollection<string> src || src.Count == 0)
            return;

        MessagesListBox.ScrollIntoView(src[src.Count - 1]);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) {
        if (e.ClickCount == 2) {
            MaxRestore_Click(sender, e);
            return;
        }
        DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaxRestore_Click(object sender, RoutedEventArgs e) {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
}
