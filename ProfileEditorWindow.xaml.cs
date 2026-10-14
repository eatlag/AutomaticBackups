using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using IOPath = System.IO.Path;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace AutomaticBackups;

public partial class ProfileEditorWindow : Window
{
    private readonly BackupProfile? _original;
    public BackupProfile? Result { get; private set; }

    public ProfileEditorWindow(BackupProfile? profile = null)
    {
        InitializeComponent();
        TimeHourBox.ItemsSource = Enumerable.Range(1, 12).Select(hour => hour.ToString(CultureInfo.InvariantCulture)).ToArray();
        TimeMinuteBox.ItemsSource = Enumerable.Range(0, 60).Select(minute => minute.ToString("00", CultureInfo.InvariantCulture)).ToArray();
        TimePeriodBox.ItemsSource = new[] { "AM", "PM" };
        MonthDayBox.ItemsSource = Enumerable.Range(1, 31).Select(day => day.ToString(CultureInfo.InvariantCulture)).ToArray();
        _original = profile;
        XamppFolderBox.Text = !string.IsNullOrWhiteSpace(profile?.XamppDirectory) ? profile.XamppDirectory : FindDefaultXamppFolder();
        DatabaseUsernameBox.Text = profile?.DatabaseUsername ?? "root";
        DatabasePortBox.Text = (profile?.DatabasePort ?? 3306).ToString(CultureInfo.InvariantCulture);
        if (profile is not null)
        {
            DialogTitle.Text = "Edit backup profile";
            NameBox.Text = profile.Name; SourceBox.Text = profile.Source; DestinationBox.Text = profile.Destination;
            HoursBox.Text = profile.EveryHours.ToString(CultureInfo.InvariantCulture); SetTimeControls(profile.Time);
            MonthDayBox.SelectedItem = profile.DayOfMonth.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(profile.DatabaseName))
            {
                DatabaseSelector.Items.Add(profile.DatabaseName);
                DatabaseSelector.SelectedItem = profile.DatabaseName;
            }
            try { DatabasePasswordBox.Password = XamppDatabaseBackup.UnprotectPassword(profile.DatabasePasswordProtected); }
            catch { DatabaseStatusText.Text = "Saved password could not be unlocked for this Windows account. Enter it again."; }
            RetentionBox.Text = profile.Retention.ToString(CultureInfo.InvariantCulture); ExcludeBox.Text = profile.Exclude;
            EnabledBox.IsChecked = profile.Enabled; HiddenBox.IsChecked = profile.IncludeHidden;
            SelectItem(FrequencyBox, profile.Frequency); SelectItem(DayBox, profile.DayOfWeek); SelectItem(CompressionBox, profile.Compression);
            IncludeDatabaseBox.IsChecked = profile.IncludeDatabaseBackup;
        }
        else
        {
            SelectItem(FrequencyBox, "Daily"); SelectItem(DayBox, "Monday"); SelectItem(CompressionBox, "Optimal");
            HoursBox.Text = "1"; MonthDayBox.SelectedItem = "1"; SetTimeControls("09:00"); RetentionBox.Text = "10"; EnabledBox.IsChecked = true;
            DatabaseUsernameBox.Text = "root";
        }
        UpdateFrequencyFields();
    }

    private static void SelectItem(System.Windows.Controls.ComboBox combo, string value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>()) if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase)) { combo.SelectedItem = item; return; }
    }

    private void SetTimeControls(string value)
    {
        if (!TimeSpan.TryParseExact(value, "hh\\:mm", CultureInfo.InvariantCulture, out var time))
            time = new TimeSpan(9, 0, 0);
        var hour24 = (int)time.TotalHours;
        var hour12 = hour24 % 12;
        if (hour12 == 0) hour12 = 12;
        TimeHourBox.SelectedItem = hour12.ToString(CultureInfo.InvariantCulture);
        TimeMinuteBox.SelectedItem = time.Minutes.ToString("00", CultureInfo.InvariantCulture);
        TimePeriodBox.SelectedItem = hour24 >= 12 ? "PM" : "AM";
    }

    private bool TryGetScheduledTime(out string value)
    {
        value = "";
        if (TimeHourBox.SelectedItem is not string hourText || !int.TryParse(hourText, out var hour12) ||
            TimeMinuteBox.SelectedItem is not string minuteText || !int.TryParse(minuteText, out var minute) ||
            TimePeriodBox.SelectedItem is not string period || hour12 is < 1 or > 12 || minute is < 0 or > 59)
            return false;

        var hour24 = hour12 % 12 + (period == "PM" ? 12 : 0);
        value = $"{hour24:00}:{minute:00}";
        return true;
    }

    private void FrequencyBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFrequencyFields();
    private void UpdateFrequencyFields()
    {
        var frequency = (FrequencyBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Daily";
        HoursPanel.Visibility = frequency == "Hourly" ? Visibility.Visible : Visibility.Collapsed;
        TimePanel.Visibility = frequency == "Hourly" ? Visibility.Collapsed : Visibility.Visible;
        DayPanel.Visibility = frequency == "Weekly" ? Visibility.Visible : Visibility.Collapsed;
        MonthDayPanel.Visibility = frequency == "Monthly" ? Visibility.Visible : Visibility.Collapsed;
        MonthShortDayNote.Visibility = frequency == "Monthly" ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FindDefaultXamppFolder() => Directory.Exists(@"C:\xampp\mysql\bin") ? @"C:\xampp" : string.Empty;

    private async void IncludeDatabase_Changed(object sender, RoutedEventArgs e)
    {
        var included = IncludeDatabaseBox.IsChecked == true;
        DatabaseOptionsPanel.Visibility = included ? Visibility.Visible : Visibility.Collapsed;
        if (included && DatabaseSelector.SelectedItem is null)
            await RefreshDatabaseListAsync();
    }

    private async void RefreshDatabases_Click(object sender, RoutedEventArgs e) => await RefreshDatabaseListAsync();

    private async Task RefreshDatabaseListAsync()
    {
        if (!int.TryParse(DatabasePortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            ShowDatabaseStatus("Enter a valid port between 1 and 65535.", true);
            return;
        }

        var previous = DatabaseSelector.SelectedItem?.ToString();
        var xamppFolder = XamppFolderBox.Text.Trim();
        var username = DatabaseUsernameBox.Text.Trim();
        var password = DatabasePasswordBox.Password;
        RefreshDatabasesButton.IsEnabled = false;
        ShowDatabaseStatus("Connecting to XAMPP and loading databases…");
        try
        {
            var names = await Task.Run(() => XamppDatabaseBackup.ListDatabases(
                xamppFolder, username, password, port));
            DatabaseSelector.Items.Clear();
            foreach (var name in names) DatabaseSelector.Items.Add(name);
            if (previous is not null && DatabaseSelector.Items.Contains(previous)) DatabaseSelector.SelectedItem = previous;
            else if (DatabaseSelector.Items.Count > 0) DatabaseSelector.SelectedIndex = 0;
            ShowDatabaseStatus(names.Count == 0 ? "No user databases were found." : $"Found {names.Count} database{(names.Count == 1 ? "" : "s")}.");
        }
        catch (Exception ex)
        {
            ShowDatabaseStatus($"Could not load databases: {ex.Message}", true);
        }
        finally { RefreshDatabasesButton.IsEnabled = true; }
    }

    private void ShowDatabaseStatus(string message, bool isError = false)
    {
        DatabaseStatusText.Text = message;
        DatabaseStatusText.Foreground = isError ? System.Windows.Media.Brushes.IndianRed : (System.Windows.Media.Brush)FindResource("TextMuted");
    }

    private void BrowseXamppFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the XAMPP installation folder", Multiselect = false, InitialDirectory = ExistingParent(XamppFolderBox.Text) };
        if (dialog.ShowDialog(this) == true) XamppFolderBox.Text = dialog.FolderName;
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
        var monthlyDay = int.TryParse(MonthDayBox.SelectedItem?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var selectedMonthDay) ? selectedMonthDay : 1;
        var compression = (CompressionBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Optimal";
        if (!int.TryParse(RetentionBox.Text, out var retention) || retention is < 0 or > 999)
        {
            MessageBox.Show(this, "Keep between 0 and 999 ZIP files. Enter 0 to keep every ZIP file.", "Check backup settings", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        if (frequency == "Monthly" && monthlyDay is < 1 or > 31)
        {
            MessageBox.Show(this, "Choose a day of the month between 1 and 31.", "Check backup settings", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        var includeDatabase = IncludeDatabaseBox.IsChecked == true;
        var databaseName = DatabaseSelector.SelectedItem?.ToString() ?? _original?.DatabaseName ?? string.Empty;
        var databaseUsername = DatabaseUsernameBox.Text.Trim();
        var databasePort = _original?.DatabasePort ?? 3306;
        if (int.TryParse(DatabasePortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort) && parsedPort is >= 1 and <= 65535)
            databasePort = parsedPort;
        if (includeDatabase)
        {
            if (!int.TryParse(DatabasePortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var selectedPort) || selectedPort is < 1 or > 65535)
            {
                MessageBox.Show(this, "Enter a valid XAMPP database port between 1 and 65535.", "Check database settings", MessageBoxButton.OK, MessageBoxImage.Warning); return;
            }
            databasePort = selectedPort;
            try { XamppDatabaseBackup.ValidateXamppFolder(XamppFolderBox.Text.Trim()); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Check XAMPP settings", MessageBoxButton.OK, MessageBoxImage.Warning); return;
            }
            if (string.IsNullOrWhiteSpace(databaseUsername) || string.IsNullOrWhiteSpace(databaseName))
            {
                MessageBox.Show(this, "Enter the database username and refresh the list to choose a database.", "Check database settings", MessageBoxButton.OK, MessageBoxImage.Warning); return;
            }
        }
        var hours = 1;
        if (frequency == "Hourly" && (!int.TryParse(HoursBox.Text, out hours) || hours is < 1 or > 24))
        {
            MessageBox.Show(this, "Hourly interval must be between 1 and 24 hours.", "Check backup settings", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        var validTime = TryGetScheduledTime(out var time);
        if (frequency != "Hourly" && !validTime)
        {
            MessageBox.Show(this, "Choose an hour, minute, and AM/PM for the scheduled start time.", "Check backup settings", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        if (!validTime) time = "09:00";

        string protectedDatabasePassword;
        try { protectedDatabasePassword = XamppDatabaseBackup.ProtectPassword(DatabasePasswordBox.Password); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The database password couldn't be protected for this Windows account.\n\n{ex.Message}", "Database settings", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }

        Result = new BackupProfile
        {
            Id = _original?.Id ?? Guid.NewGuid(), Name = name, Source = source, Destination = destination,
            Frequency = frequency, EveryHours = hours, Time = time, DayOfWeek = day, DayOfMonth = monthlyDay,
            Retention = retention, Compression = compression, Exclude = ExcludeBox.Text.Trim(),
            IncludeHidden = HiddenBox.IsChecked == true, Enabled = EnabledBox.IsChecked == true,
            LastRunUtc = _original?.LastRunUtc, LastAttemptUtc = _original?.LastAttemptUtc,
            IncludeDatabaseBackup = includeDatabase, DatabaseName = databaseName,
            XamppDirectory = XamppFolderBox.Text.Trim(), DatabaseUsername = databaseUsername,
            DatabasePort = databasePort,
            DatabasePasswordProtected = protectedDatabasePassword
        };
        DialogResult = true;
    }
}
