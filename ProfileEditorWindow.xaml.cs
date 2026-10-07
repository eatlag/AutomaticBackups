using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace AutomaticBackups;

public partial class ProfileEditorWindow : Window
{
    private readonly BackupProfile? _original;
    public BackupProfile? Result { get; private set; }

    public ProfileEditorWindow(BackupProfile? profile = null)
    {
        InitializeComponent();
        _original = profile;
        if (profile is not null)
        {
            DialogTitle.Text = "Edit backup profile";
            NameBox.Text = profile.Name; SourceBox.Text = profile.Source; DestinationBox.Text = profile.Destination;
            HoursBox.Text = profile.EveryHours.ToString(CultureInfo.InvariantCulture); TimeBox.Text = profile.Time;
            RetentionBox.Text = profile.Retention.ToString(CultureInfo.InvariantCulture); ExcludeBox.Text = profile.Exclude;
            EnabledBox.IsChecked = profile.Enabled; HiddenBox.IsChecked = profile.IncludeHidden;
            SelectItem(FrequencyBox, profile.Frequency); SelectItem(DayBox, profile.DayOfWeek); SelectItem(CompressionBox, profile.Compression);
        }
        else
        {
            SelectItem(FrequencyBox, "Daily"); SelectItem(DayBox, "Monday"); SelectItem(CompressionBox, "Optimal");
            HoursBox.Text = "1"; TimeBox.Text = "09:00"; RetentionBox.Text = "10"; EnabledBox.IsChecked = true;
        }
        UpdateFrequencyFields();
    }

    private static void SelectItem(ComboBox combo, string value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>()) if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase)) { combo.SelectedItem = item; return; }
    }

    private void FrequencyBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFrequencyFields();
    private void UpdateFrequencyFields()
    {
        var frequency = (FrequencyBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Daily";
        HoursPanel.Visibility = frequency == "Hourly" ? Visibility.Visible : Visibility.Collapsed;
        TimePanel.Visibility = frequency == "Hourly" ? Visibility.Collapsed : Visibility.Visible;
        DayPanel.Visibility = frequency == "Weekly" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseSourceFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a source folder", Multiselect = false, InitialDirectory = ExistingParent(SourceBox.Text) };
        if (dialog.ShowDialog(this) == true) SourceBox.Text = dialog.FolderName;
    }

    private void BrowseSourceFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose a file to back up", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) SourceBox.Text = dialog.FileName;
    }

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a destination folder", Multiselect = false, InitialDirectory = ExistingParent(DestinationBox.Text) };
        if (dialog.ShowDialog(this) == true) DestinationBox.Text = dialog.FolderName;
    }

    private static string? ExistingParent(string path)
    {
        try { var full = IOPath.GetFullPath(path); return Directory.Exists(full) ? full : IOPath.GetDirectoryName(full); }
        catch { return null; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim(); var source = SourceBox.Text.Trim(); var destination = DestinationBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || !File.Exists(source) && !Directory.Exists(source) || !Directory.Exists(destination))
        {
            MessageBox.Show(this, "Enter a name, an existing source file or folder, and an existing destination folder.", "Check backup settings", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        source = PathHelpers.Normalize(source);
        destination = PathHelpers.Normalize(destination);
        if (Directory.Exists(source) && (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) || PathHelpers.IsChildPath(source, destination)))
        {
            MessageBox.Show(this, "The destination cannot be the source folder or a folder inside it. Choose a separate destination.", "Check backup settings", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }

        var frequency = (FrequencyBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Daily";
        var day = (DayBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Monday";
        var compression = (CompressionBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Optimal";
        if (!int.TryParse(RetentionBox.Text, out var retention) || retention is < 1 or > 999)
        {
            MessageBox.Show(this, "Keep between 1 and 999 ZIP files.", "Check backup settings", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        var hours = 1;
        if (frequency == "Hourly" && (!int.TryParse(HoursBox.Text, out hours) || hours is < 1 or > 24))
        {
            MessageBox.Show(this, "Hourly interval must be between 1 and 24 hours.", "Check backup settings", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        var time = TimeBox.Text.Trim();
        if (frequency != "Hourly" && !TimeSpan.TryParseExact(time, "hh\\:mm", CultureInfo.InvariantCulture, out _))
        {
            MessageBox.Show(this, "Use a 24-hour time such as 09:30.", "Check backup settings", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }

        Result = new BackupProfile
        {
            Id = _original?.Id ?? Guid.NewGuid(), Name = name, Source = source, Destination = destination,
            Frequency = frequency, EveryHours = hours, Time = time, DayOfWeek = day,
            Retention = retention, Compression = compression, Exclude = ExcludeBox.Text.Trim(),
            IncludeHidden = HiddenBox.IsChecked == true, Enabled = EnabledBox.IsChecked == true,
            LastRunUtc = _original?.LastRunUtc
        };
        DialogResult = true;
    }
}
