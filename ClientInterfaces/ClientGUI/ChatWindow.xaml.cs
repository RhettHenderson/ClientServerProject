using ClientApp;
using Common;
using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace ClientGUI;

public partial class ChatWindow : Window {
    private readonly Client _client;
    private bool hasAttachment;
    private string attachmentPath;

    public ChatWindow(Client client) {
        InitializeComponent();
        _client = client;
        _client.MessageReceived += Client_MessageReceived;

        HeaderText.Text = $"Logged in as {_client.Name} | Server: {_client?.LocalEndPoint}";
        MessageTextBox.Focus();
    }


    private async void MessageTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) {
        if (e.Key == System.Windows.Input.Key.Enter) {
            await SendCurrentAsync();
        }
    }
    private async void Send_Click(object sender, RoutedEventArgs e) {
        await SendCurrentAsync();
    }

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

    private async Task SendCurrentAsync() {
        // If a file is attached, send it
        if (hasAttachment && !string.IsNullOrWhiteSpace(attachmentPath)) {
            await FileTransfer.SendFileAsync(_client._stream, attachmentPath, _client.pendingResponses);

            // show something in history if you want
            MessagesListBox.Items.Add($"(file) You: {Path.GetFileName(attachmentPath)}");
            MessagesListBox.ScrollIntoView(MessagesListBox.Items[^1]);

            // clear attachment state
            RemoveAttachment_Click(this, new RoutedEventArgs());
        }

        // Send the typed message (optional)
        string message = MessageTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(message)) {
            await _client.SendMessageAsync(message);
            MessagesListBox.Items.Add($"You: {message}");
            MessagesListBox.ScrollIntoView(MessagesListBox.Items[^1]);

            MessageTextBox.Clear();
            MessageTextBox.Focus();
        }

    }

    private void Client_MessageReceived(string sender, string text) {
        Dispatcher.BeginInvoke(() => {
            AddToHistory($"{sender}: {text}");
        });
    }

    private void AddToHistory(string line) {
        MessagesListBox.Items.Add(line);
        MessagesListBox.ScrollIntoView(MessagesListBox.Items[^1]);
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
