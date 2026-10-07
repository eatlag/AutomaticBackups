using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace AutomaticBackups;

public partial class App : Application
{
    public static string DataDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Data");
    public static string CurrentTheme { get; private set; } = "Light";
    public static bool StartWithWindows { get; private set; }
    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var dataInfoPath = Path.Combine(DataDirectory, "README.txt");
            if (!File.Exists(dataInfoPath)) File.WriteAllText(dataInfoPath, "Automatic Backups stores profiles.json, settings.json, and backup.log in this folder. Keep Data beside AutomaticBackups.exe.");
            MigrateExistingData();
            var settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(SettingsPath)) ?? new AppPreferences()
                : new AppPreferences();
            CurrentTheme = string.Equals(settings.Theme, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
            StartWithWindows = settings.StartWithWindows;
            ApplyTheme(CurrentTheme);
            base.OnStartup(e);
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Automatic Backups couldn't open its data folder beside the app.\n\n{DataDirectory}\n\n{ex.Message}", "Automatic Backups", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    public static void SetTheme(string theme)
    {
        var nextTheme = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
        var updated = new AppPreferences { Theme = nextTheme, StartWithWindows = StartWithWindows };
        SavePreferences(updated);
        CurrentTheme = nextTheme;
        ApplyTheme(nextTheme);
    }

    public static void SetStartWithWindows(bool enabled)
    {
        var updated = new AppPreferences { Theme = CurrentTheme, StartWithWindows = enabled };
        SavePreferences(updated);
        StartWithWindows = enabled;
    }

    private static void SavePreferences(AppPreferences preferences)
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void MigrateExistingData()
    {
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BackupStudio");
        foreach (var name in new[] { "profiles.json", "backup.log", "settings.json" })
        {
            var oldPath = Path.Combine(legacy, name);
            var newPath = Path.Combine(DataDirectory, name);
            if (File.Exists(oldPath) && !File.Exists(newPath)) File.Copy(oldPath, newPath);
        }
    }

    private static void ApplyTheme(string theme)
    {
        var dark = theme == "Dark";
        var resources = Current.Resources;
        resources["WindowSurface"] = new LinearGradientBrush(
            dark ? Color.FromRgb(18, 21, 32) : Color.FromRgb(240, 252, 255),
            dark ? Color.FromRgb(25, 24, 42) : Color.FromRgb(248, 245, 255), 45);
        resources["SidebarGradient"] = new LinearGradientBrush(
            dark ? Color.FromRgb(13, 16, 27) : Color.FromRgb(32, 23, 66),
            dark ? Color.FromRgb(21, 35, 53) : Color.FromRgb(23, 77, 104), 45);
        resources["CardSurface"] = Brush(dark ? "#1D2230" : "#FFFFFF");
        resources["InputSurface"] = Brush(dark ? "#171B27" : "#FBFCFE");
        resources["TextPrimary"] = Brush(dark ? "#EDF3FB" : "#202B3C");
        resources["TextSecondary"] = Brush(dark ? "#C0CAD9" : "#566A86");
        resources["TextMuted"] = Brush(dark ? "#A5B0C2" : "#78849A");
        resources["TextFaint"] = Brush(dark ? "#8895A9" : "#9AA4B5");
        resources["CardBorder"] = Brush(dark ? "#343B4B" : "#E5E9F1");
        resources["Divider"] = Brush(dark ? "#303747" : "#EEF0F5");
        resources["HoverSurface"] = Brush(dark ? "#272D3C" : "#F7F8FC");
        resources["SelectedSurface"] = Brush(dark ? "#302A49" : "#F2F1FF");
        resources["InfoSurface"] = Brush(dark ? "#1A303B" : "#EAF7FF");
        resources["InfoBorder"] = Brush(dark ? "#2D5361" : "#CDEBF6");
    }

    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
}

public sealed class AppPreferences
{
    public string Theme { get; set; } = "Light";
    public bool StartWithWindows { get; set; }
}
