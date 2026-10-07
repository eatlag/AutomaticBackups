using Microsoft.Win32;
using System.Windows;
using System.Diagnostics;

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
        RunOnLogonCheckBox.IsChecked = StartupRegistration.IsEnabled();
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

    private void RunOnLogon_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        try
        {
            StartupRegistration.SetEnabled(RunOnLogonCheckBox.IsChecked == true);
            App.SetStartWithWindows(RunOnLogonCheckBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            _initializing = true;
            RunOnLogonCheckBox.IsChecked = StartupRegistration.IsEnabled();
            _initializing = false;
            MessageBox.Show(this, $"Windows couldn't update the sign-in startup setting.\n\n{ex.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true) ?? throw new InvalidOperationException("The current-user startup settings are unavailable.");
        if (enabled)
        {
            var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("The app's executable path couldn't be found.");
            key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}
