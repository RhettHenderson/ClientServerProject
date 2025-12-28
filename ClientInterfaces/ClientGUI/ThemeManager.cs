using Microsoft.Win32;
using System.Windows;


namespace ClientGUI;

public static class ThemeManager {
    private static readonly Uri LightUri = new("pack://application:,,,/Themes/Light.xaml");
    private static readonly Uri DarkUri = new("pack://application:,,,/Themes/Dark.xaml");

    public static void ApplySystemTheme() {
        bool isLight = GetAppsUseLightTheme();
        ApplyTheme(isLight);
    }

    public static void ApplyTheme(bool useLight) {
        var dicts = Application.Current.Resources.MergedDictionaries;

        // remove existing theme dictionaries (if any)
        var existing = dicts
            .FirstOrDefault(d => d.Source != null && (d.Source == LightUri || d.Source == DarkUri));

        if (existing != null)
            dicts.Remove(existing);

        dicts.Add(new ResourceDictionary { Source = useLight ? LightUri : DarkUri });
    }

    // Windows: HKCU\...\Personalize\AppsUseLightTheme (1=light, 0=dark)
    private static bool GetAppsUseLightTheme() {
        try {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            object? value = key?.GetValue("AppsUseLightTheme");
            return value is int i ? i != 0 : true; // default to light if missing
        }
        catch {
            return true;
        }
    }
}
