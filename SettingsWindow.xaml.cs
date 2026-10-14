using Microsoft.Win32;
using System.Windows;
using System.Diagnostics;
using MessageBox = System.Windows.MessageBox;

namespace AutomaticBackups;

public partial class SettingsWindow : Window
{
    private bool _initializing = true;
    public SettingsWindow()
    {
        InitializeComponent();
        foreach (var item in ThemeSelector.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), App.CurrentTheme, StringComparison.OrdinalIgnoreCase)) ThemeSelector.SelectedItem = item;
        }
        SelectHistoryLimit(App.RecentHistoryLimit);
        RunOnLogonCheckBox.IsChecked = StartupRegistration.IsEnabled();
        StartInTrayCheckBox.IsChecked = App.StartInTray;
        StartInTrayCheckBox.IsEnabled = RunOnLogonCheckBox.IsChecked == true;
        DataDirectoryText.Text = App.DataDirectory;
        _initializing = false;
    }

    private void ThemeSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing || ThemeSelector.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
        try { App.SetTheme(item.Content?.ToString() ?? "Light"); }
        catch (Exception ex)
        {
            _initializing = true;
            foreach (var option in ThemeSelector.Items.OfType<System.Windows.Controls.ComboBoxItem>())
            {
                if (string.Equals(option.Content?.ToString(), App.CurrentTheme, StringComparison.OrdinalIgnoreCase)) ThemeSelector.SelectedItem = option;
            }
            _initializing = false;
            MessageBox.Show(this, $"The appearance setting couldn't be saved in the app's Data folder.\n\n{ex.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void HistoryLimit_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing || HistoryLimitSelector.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
        var selected = item.Content?.ToString();
        var limit = string.Equals(selected, "Unlimited", StringComparison.OrdinalIgnoreCase)
            ? 0
            : int.TryParse(selected, out var parsed) ? parsed : App.RecentHistoryLimit;
        try
        {
            App.SetRecentHistoryLimit(limit);
            if (App.Current.MainWindow is MainWindow mainWindow) mainWindow.ReloadBackupHistory();
        }
        catch (Exception ex)
        {
            _initializing = true;
            SelectHistoryLimit(App.RecentHistoryLimit);
            _initializing = false;
            MessageBox.Show(this, $"The backup history setting couldn't be saved in the app's Data folder.\n\n{ex.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SelectHistoryLimit(int limit)
    {
        var value = limit == 0 ? "Unlimited" : limit.ToString();
        foreach (var item in HistoryLimitSelector.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                HistoryLimitSelector.SelectedItem = item;
        }
    }

    private void RunOnLogon_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        try
        {
            var enabled = RunOnLogonCheckBox.IsChecked == true;
            StartupRegistration.SetEnabled(enabled, App.StartInTray);
            App.SetStartWithWindows(enabled);
            StartInTrayCheckBox.IsEnabled = enabled;
        }
        catch (Exception ex)
        {
            _initializing = true;
            RunOnLogonCheckBox.IsChecked = StartupRegistration.IsEnabled();
            StartInTrayCheckBox.IsEnabled = RunOnLogonCheckBox.IsChecked == true;
            _initializing = false;
            MessageBox.Show(this, $"Windows couldn't update the sign-in startup setting.\n\n{ex.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StartInTray_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var enabled = StartInTrayCheckBox.IsChecked == true;
        try
        {
            if (StartupRegistration.IsEnabled()) StartupRegistration.SetEnabled(true, enabled);
            App.SetStartInTray(enabled);
        }
        catch (Exception ex)
        {
            _initializing = true;
            StartInTrayCheckBox.IsChecked = App.StartInTray;
            _initializing = false;
            MessageBox.Show(this, $"The system tray startup setting couldn't be saved.\n\n{ex.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Website_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://eatlag.net") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The website couldn't be opened.\n\n{ex.Message}", "Automatic Backups", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AutomaticBackups";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled, bool startInTray)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true) ?? throw new InvalidOperationException("The current-user startup settings are unavailable.");
        if (enabled)
        {
            var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("The app's executable path couldn't be found.");
            var launchOption = startInTray ? " --tray" : string.Empty;
            key.SetValue(ValueName, $"\"{exePath}\"{launchOption}", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}
