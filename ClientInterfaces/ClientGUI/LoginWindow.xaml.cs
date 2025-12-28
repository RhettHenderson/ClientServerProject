using ClientApp;
using System.Windows;
using System.Windows.Input;

namespace ClientGUI;

public partial class LoginWindow : Window {
    private readonly string _serverIp;
    private readonly Client _client;

    public LoginWindow(string serverIp) {
        InitializeComponent();
        _serverIp = serverIp;

        _client = new Client();
        _client.ConnectAsync(serverIp, 11111);

        IpInfoText.Text = $"Server: {_serverIp}";
        UsernameTextBox.Focus();
    }

    private async void Login_Click(object sender, RoutedEventArgs e) {
        string username = UsernameTextBox.Text.Trim();
        string password = PasswordBox.Password;

        await _client.LoginAsync(username, password);

        var chat = new ChatWindow(_client, _serverIp);
        //Set the chat to the main window so the app closes when it is closed
        Application.Current.MainWindow = chat;
        chat.Show();

        Close();
    }

    // ---- Custom title bar handlers ----
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (e.LeftButton == MouseButtonState.Pressed) {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
}
