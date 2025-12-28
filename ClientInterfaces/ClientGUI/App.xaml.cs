using Microsoft.Win32;
using System.Windows;
namespace ClientGUI;
/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application {
    protected override async void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);
        /*
         * Since IP prompt is a dialog, when we hit OK, it closes and WPF detects 
         * that there are no windows open and shuts down the app, 
         * so we set it to explicit shutdown here
         */
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        ThemeManager.ApplySystemTheme();
        SystemEvents.UserPreferenceChanged += (s, ev) => ThemeManager.ApplySystemTheme();
        //IP Prompt
        var ipPrompt = new IpPromptWindow();
        bool? ipOk = ipPrompt.ShowDialog();
        if (ipOk != true) {
            Shutdown();
            return;
        }

        string serverIp = ipPrompt.ServerIP;

        //Switch back to shutting down when the last window is closed
        ShutdownMode = ShutdownMode.OnLastWindowClose;

        //Login
        var loginWindow = new LoginWindow(serverIp);
        MainWindow = loginWindow;
        loginWindow.Show();

    }

    protected override void OnExit(ExitEventArgs e) {
        SystemEvents.UserPreferenceChanged -= (s, ev) => ThemeManager.ApplySystemTheme();
        base.OnExit(e);
    }
}

