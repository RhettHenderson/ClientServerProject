using Common;
using System.Windows;
using System.Windows.Input;

namespace ClientGUI;

/// <summary>
/// Interaction logic for IpPromptWindow.xaml
/// </summary>
public partial class IpPromptWindow : Window {
    public string ServerIP { get; private set; } = "";

    public IpPromptWindow() {
        InitializeComponent();
        IpTextBox.Text = Utility.GetLocalIP();
        IpTextBox.SelectAll();
        IpTextBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) {
        ServerIP = IpTextBox.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) {
        DialogResult = false;
    }

    // ---- Custom title bar handlers ----
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (e.LeftButton == MouseButtonState.Pressed) {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Close_Click(object sender, RoutedEventArgs e) {
        // Treat the custom X as cancel for this dialog.
        DialogResult = false;
    }
}
