using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using IOPath = System.IO.Path;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;

namespace AutomaticBackups;

public partial class MainWindow : Window
{
    private static readonly string AppFolder = App.DataDirectory;
    private static readonly string ProfilesPath = IOPath.Combine(AppFolder, "profiles.json");
    private static readonly string LogPath = IOPath.Combine(AppFolder, "backup.log");
    private readonly ObservableCollection<BackupProfile> _profiles = [];
    private readonly ObservableCollection<RecentBackupActivity> _recentActivity = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _lastScheduleCheck = DateTime.MinValue;
    private DateTime _lastCapacityRefresh = DateTime.MinValue;
    private double _capacityRatio;
    private bool _busy;
    private bool _allowClose;
    private Forms.NotifyIcon? _trayIcon;
    private Drawing.Icon? _trayDrawingIcon;

    public MainWindow()
    {
        InitializeComponent();
        ProfileList.ItemsSource = _profiles;
        ActivityList.ItemsSource = _recentActivity;
        LoadProfiles();
        LoadLog();
        RefreshDashboard();
        _timer.Tick += async (_, _) =>
        {
            foreach (var profile in _profiles) profile.RefreshCountdown();
            if (DateTime.Now - _lastScheduleCheck >= TimeSpan.FromSeconds(20))
            {
                _lastScheduleCheck = DateTime.Now;
                await CheckSchedulesAsync();
            }
            if (DateTime.Now - _lastCapacityRefresh >= TimeSpan.FromSeconds(30))
            {
                _lastCapacityRefresh = DateTime.Now;
                UpdateDestinationCapacity();
            }
        };
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Log("Automatic Backups is ready. Scheduled backups run while this app is open.");
    }

    private void LoadProfiles()
    {
        try
        {
            if (File.Exists(ProfilesPath))
            {
                var profiles = JsonSerializer.Deserialize<List<BackupProfile>>(File.ReadAllText(ProfilesPath)) ?? [];
                foreach (var profile in profiles) _profiles.Add(profile);
            }
        }
        catch (Exception ex) { Log($"Could not load saved profiles: {ex.Message}", true); }
    }

    private void SaveProfiles()
    {
        Directory.CreateDirectory(AppFolder);
        var temp = ProfilesPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_profiles, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, ProfilesPath, true);
    }

    private void LoadLog()
    {
        _recentActivity.Clear();
        try
        {
            if (File.Exists(LogPath))
            {
                var lines = File.ReadLines(LogPath);
                if (App.RecentHistoryLimit > 0) lines = lines.TakeLast(Math.Max(1000, App.RecentHistoryLimit * 50));
                foreach (var line in lines) AddActivityFromLog(line);
            }
        }
        catch { }
        UpdateActivityHistoryStatus();
    }

    private void Log(string message, bool error = false)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]  {(error ? "ERROR · " : "")}{message}";
        try { Directory.CreateDirectory(AppFolder); File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => LogToView(line)); return; }
        LogToView(line);
    }

    private void LogToView(string line)
    {
        AddActivityFromLog(line);
    }

    private void AddActivityFromLog(string line)
    {
        if (!TryParseBackupActivity(line, out var activity) || activity is null) return;
        _recentActivity.Insert(0, activity);
        while (App.RecentHistoryLimit > 0 && _recentActivity.Count > App.RecentHistoryLimit) _recentActivity.RemoveAt(_recentActivity.Count - 1);
        ActivityEmptyState.Visibility = _recentActivity.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateActivityHistoryStatus();
    }

    private void UpdateActivityHistoryStatus()
    {
        ActivityEmptyState.Visibility = _recentActivity.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActivitySubtitle.Text = _recentActivity.Count == 0
            ? "Latest backup results"
            : App.RecentHistoryLimit == 0
                ? $"All {_recentActivity.Count} backup results"
                : $"Last {_recentActivity.Count} backup result{(_recentActivity.Count == 1 ? "" : "s")}";
    }

    public void ReloadBackupHistory() => LoadLog();

    private static bool TryParseBackupActivity(string line, out RecentBackupActivity? activity)
    {
        activity = null;
        var closingBracket = line.IndexOf(']');
        if (line.Length < 3 || line[0] != '[' || closingBracket < 0) return false;
        if (!DateTime.TryParseExact(line[1..closingBracket], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)) return false;

        var message = line[(closingBracket + 1)..].Trim();
        if (message.StartsWith("ERROR · ", StringComparison.Ordinal)) message = message[8..];
        var isFailure = message.StartsWith("Backup failed · ", StringComparison.Ordinal);
        var isComplete = message.StartsWith("Backup complete · ", StringComparison.Ordinal);
        if (!isFailure && !isComplete) return false;

        var details = message[(isFailure ? "Backup failed · ".Length : "Backup complete · ".Length)..];
        var parts = details.Split(" · ", StringSplitOptions.None);
        var profileName = parts.FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(profileName)) profileName = "Backup";

        string summary;
        if (isComplete)
        {
            var countIndex = Array.FindIndex(parts, 1, parts.Length - 1, part =>
                part.EndsWith(" files", StringComparison.OrdinalIgnoreCase) || part.EndsWith(" file", StringComparison.OrdinalIgnoreCase));
            if (countIndex > 0)
            {
                profileName = string.Join(" · ", parts.Take(countIndex)).Trim();
                var size = countIndex + 1 < parts.Length ? parts[countIndex + 1].Trim() : string.Empty;
                summary = $"Completed · {parts[countIndex].Trim()}" + (size.Length > 0 ? $" · {size}" : string.Empty);
            }
            else
            {
                summary = "Backup completed";
            }
        }
        else
        {
            var separator = details.IndexOf(" · ", StringComparison.Ordinal);
            if (separator >= 0) profileName = details[..separator].Trim();
            summary = "Failed · Check the source or destination.";
        }

        var timeLabel = when.Date == DateTime.Today
            ? $"Today · {when:h:mm tt}"
            : when.Date == DateTime.Today.AddDays(-1)
                ? $"Yesterday · {when:h:mm tt}"
                : $"{when:MMM d} · {when:h:mm tt}";
        activity = new RecentBackupActivity(profileName, summary, timeLabel, isFailure);
        return true;
    }

    private void RefreshDashboard()
    {
        ProfileList.Items.Refresh();
        StatProfiles.Text = _profiles.Count.ToString();
        ProfileCount.Text = _profiles.Count == 1 ? "1 profile configured" : $"{_profiles.Count} profiles configured";
        EmptyState.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var enabled = _profiles.Where(p => p.Enabled).ToArray();
        StatActiveSchedules.Text = enabled.Length.ToString(CultureInfo.InvariantCulture);
        var selected = ProfileList.SelectedItem as BackupProfile;
        var hasSelection = selected is not null;
        RunButton.IsEnabled = hasSelection && !_busy;
        PauseButton.IsEnabled = hasSelection && !_busy;
        PauseButton.Content = selected?.Enabled == false ? "Resume Backups" : "Pause Backups";
        EditButton.IsEnabled = hasSelection && !_busy;
        DeleteButton.IsEnabled = hasSelection && !_busy;
        if (selected is null)
        {
            SelectedName.Text = "Select a backup"; SelectedSchedule.Text = "Your backup details will appear here.";
            SelectedSource.Text = "—"; SelectedSource.ToolTip = null;
            SelectedDestination.Text = "—"; SelectedDestination.ToolTip = null;
            SelectedOptions.Text = "Choose a profile to view its ZIP and retention settings.";
        }
        else
        {
            SelectedName.Text = selected.Name;
            SelectedSchedule.Text = selected.Enabled ? selected.ScheduleLabel : $"{selected.ScheduleLabel} · Paused";
            SelectedSource.Text = selected.Source; SelectedSource.ToolTip = selected.Source;
            SelectedDestination.Text = selected.Destination; SelectedDestination.ToolTip = selected.Destination;
            var retentionSummary = selected.Retention == 0 ? "Keep all ZIPs" : $"Keep newest {selected.Retention} ZIPs";
            var databaseSummary = selected.IncludeDatabaseBackup ? $"\nXAMPP database: {selected.DatabaseName}" : string.Empty;
            SelectedOptions.Text = $"{selected.Compression} compression  ·  {retentionSummary}{databaseSummary}\nExclusions: {(string.IsNullOrWhiteSpace(selected.Exclude) ? "None" : selected.Exclude)}";
        }
        UpdateDestinationCapacity();
    }

    private void UpdateDestinationCapacity()
    {
        if (ProfileList.SelectedItem is not BackupProfile profile)
        {
            CapacityAmount.Text = "Select a backup";
            CapacityDetail.Text = "Choose a profile to see available space.";
            CapacityPercent.Text = "—";
            CapacityPercent.Foreground = Brushes.LightSkyBlue;
            _capacityRatio = 0;
            UpdateCapacityBarWidth();
            return;
        }

        try
        {
            var destination = PathHelpers.Normalize(profile.Destination);
            var driveRoot = IOPath.GetPathRoot(destination);
            if (string.IsNullOrWhiteSpace(driveRoot)) throw new IOException("The destination drive isn't available.");
            var drive = new DriveInfo(driveRoot);
            if (!drive.IsReady || drive.TotalSize <= 0) throw new IOException("The destination drive isn't ready.");

            var freeBytes = drive.AvailableFreeSpace;
            var totalBytes = drive.TotalSize;
            _capacityRatio = Math.Clamp(freeBytes / (double)totalBytes, 0, 1);
            var capacityBrush = _capacityRatio >= 0.25 ? Brushes.MediumSeaGreen
                : _capacityRatio >= 0.10 ? Brushes.DarkOrange
                : Brushes.IndianRed;
            CapacityAmount.Text = $"{FormatSize(freeBytes)} free";
            CapacityDetail.Text = $"of {FormatSize(totalBytes)} total on the destination drive";
            CapacityPercent.Text = $"{_capacityRatio:P0} available";
            CapacityPercent.Foreground = capacityBrush;
            CapacityBar.Background = capacityBrush;
            UpdateCapacityBarWidth();
        }
        catch
        {
            CapacityAmount.Text = "Space unavailable";
            CapacityDetail.Text = "Couldn't read free space for this destination.";
            CapacityPercent.Text = "—";
            CapacityPercent.Foreground = Brushes.LightSteelBlue;
            _capacityRatio = 0;
            UpdateCapacityBarWidth();
        }
    }

    private void CapacityTrack_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCapacityBarWidth();

    private void UpdateCapacityBarWidth()
    {
        if (CapacityTrack is not null && CapacityBar is not null)
            CapacityBar.Width = CapacityTrack.ActualWidth * _capacityRatio;
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshDashboard();

    private void ProfilesNav_Click(object sender, RoutedEventArgs e) { }

    private void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow { Owner = this };
        settings.ShowDialog();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            DisposeTrayIcon();
            return;
        }

        var choice = new CloseChoiceWindow { Owner = this };
        if (choice.ShowDialog() != true)
        {
            e.Cancel = true;
            return;
        }

        if (choice.Result == CloseChoice.MinimizeToTray)
        {
            e.Cancel = true;
            EnsureTrayIcon();
            Hide();
            ShowInTaskbar = false;
            _trayIcon?.ShowBalloonTip(2500, "Automatic Backups", "The app is still running in the system tray.", Forms.ToolTipIcon.Info);
            return;
        }

        _allowClose = true;
        DisposeTrayIcon();
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null) return;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Automatic Backups", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit completely", null, (_, _) => QuitFromTray());

        _trayDrawingIcon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty)
            ?? (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Automatic Backups",
            Icon = _trayDrawingIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    public void StartMinimizedToTray()
    {
        EnsureTrayIcon();
        ShowInTaskbar = false;
        Hide();
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void QuitFromTray()
    {
        _allowClose = true;
        DisposeTrayIcon();
        Close();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _trayDrawingIcon?.Dispose();
        _trayDrawingIcon = null;
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

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProfileEditorWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        if (_profiles.Any(p => string.Equals(p.Name, dialog.Result.Name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "Profile names must be unique so ZIP files and retention stay separate.", "Name already in use", MessageBoxButton.OK, MessageBoxImage.Information); return;
        }
        _profiles.Add(dialog.Result);
        SaveProfiles(); RefreshDashboard(); Log($"Profile created · {dialog.Result.Name}");
    }

    private void EditSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not BackupProfile profile) return;
        var dialog = new ProfileEditorWindow(profile) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        if (_profiles.Any(p => p.Id != profile.Id && string.Equals(p.Name, dialog.Result.Name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "Profile names must be unique so ZIP files and retention stay separate.", "Name already in use", MessageBoxButton.OK, MessageBoxImage.Information); return;
        }
        var index = _profiles.IndexOf(profile);
        _profiles[index] = dialog.Result;
        ProfileList.SelectedIndex = index;
        SaveProfiles(); RefreshDashboard(); Log($"Profile updated · {dialog.Result.Name}");
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not BackupProfile profile) return;
        if (MessageBox.Show($"Delete profile ‘{profile.Name}’? Existing ZIP files will be kept.", "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _profiles.Remove(profile); SaveProfiles(); RefreshDashboard(); Log($"Profile deleted · {profile.Name}");
    }

    private async void RunSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is BackupProfile profile) await RunBackupAsync(profile, false);
    }

    private void PauseSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not BackupProfile profile || _busy) return;
        var wasEnabled = profile.Enabled;
        profile.Enabled = !wasEnabled;
        try
        {
            SaveProfiles();
            RefreshDashboard();
            Log($"Schedule {(profile.Enabled ? "resumed" : "paused")} · {profile.Name}");
        }
        catch (Exception ex)
        {
            profile.Enabled = wasEnabled;
            RefreshDashboard();
            MessageBox.Show(this, $"The schedule couldn't be updated.\n\n{ex.Message}", "Automatic Backups", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _recentActivity.Clear();
        ActivityEmptyState.Visibility = Visibility.Visible;
        ActivitySubtitle.Text = "Latest backup results";
        try { File.WriteAllText(LogPath, string.Empty); } catch { }
    }

    private async Task CheckSchedulesAsync()
    {
        if (_busy) return;
        var due = _profiles.FirstOrDefault(IsDue);
        if (due is not null) await RunBackupAsync(due, true);
    }

    private static bool IsDue(BackupProfile p)
    {
        if (!p.Enabled) return false;
        var now = DateTime.Now;
        var last = (p.LastAttemptUtc ?? p.LastRunUtc)?.ToLocalTime();
        if (p.Frequency == "Hourly") return last is null || (now - last.Value).TotalHours >= p.EveryHours;
        if (!TimeSpan.TryParseExact(p.Time, "hh\\:mm", null, out var time)) return false;
        if (p.Frequency == "Monthly")
        {
            var scheduled = MonthlyOccurrence(now.Year, now.Month, p.DayOfMonth, time);
            return now >= scheduled && (last is null || last.Value < scheduled);
        }
        if (last?.Date == now.Date || now.TimeOfDay < time) return false;
        return p.Frequency == "Daily" || (p.Frequency == "Weekly" && now.DayOfWeek.ToString() == p.DayOfWeek);
    }

    internal static DateTime MonthlyOccurrence(int year, int month, int dayOfMonth, TimeSpan time)
    {
        var day = Math.Clamp(dayOfMonth, 1, DateTime.DaysInMonth(year, month));
        return new DateTime(year, month, day).Add(time);
    }

    private async Task RunBackupAsync(BackupProfile profile, bool scheduled)
    {
        if (_busy) return;
        _busy = true; profile.IsRunning = true; RunButton.IsEnabled = false; (ScheduleStatus.Text, ScheduleStatus.Foreground) = ("●  BACKUP RUNNING", Brushes.DarkOrange);
        Log($"{(scheduled ? "Scheduled backup started" : "Backup started")} · {profile.Name}");
        try
        {
            var result = await Task.Run(() => CreateArchive(profile));
            profile.LastAttemptUtc = DateTime.UtcNow;
            profile.LastRunUtc = profile.LastAttemptUtc;
            SaveProfiles(); RefreshDashboard();
            Log($"Backup complete · {profile.Name} · {result.Count:N0} files · {FormatSize(result.Size)} · {result.Archive}");
        }
        catch (Exception ex)
        {
            profile.LastAttemptUtc = DateTime.UtcNow;
            try { SaveProfiles(); } catch (Exception saveEx) { Log($"Could not save the failed attempt time for {profile.Name}: {saveEx.Message}", true); }
            Log($"Backup failed · {profile.Name} · {ex.Message}", true);
            if (!scheduled)
                MessageBox.Show(this, $"Backup failed for ‘{profile.Name}’:\n\n{ex.Message}", "Automatic Backups", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            profile.IsRunning = false; _busy = false; ScheduleStatus.Text = "●  READY"; ScheduleStatus.Foreground = Brushes.ForestGreen; RefreshDashboard();
        }
    }

    private static (string Archive, int Count, long Size) CreateArchive(BackupProfile profile)
    {
        var source = PathHelpers.Normalize(profile.Source);
        var destination = PathHelpers.Normalize(profile.Destination);
        if (!File.Exists(source) && !Directory.Exists(source)) throw new DirectoryNotFoundException("The selected source no longer exists.");
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException("The destination folder no longer exists.");
        var sourceIsDirectory = Directory.Exists(source);
        if (sourceIsDirectory && (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) || PathHelpers.IsChildPath(source, destination)))
            throw new IOException("The destination cannot be the source folder or a folder inside it.");

        var patterns = profile.Exclude.Split([',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var files = new List<(string FullPath, string Entry)>();
        if (!sourceIsDirectory)
        {
            files.Add((source, IOPath.GetFileName(source)));
        }
        else
        {
            CollectFiles(source, source, profile.IncludeHidden, patterns, files);
        }
        var safeName = string.Concat(profile.Name.Select(c => "<>:\"/\\|?*".Contains(c) || char.IsControl(c) ? '_' : c)).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "Backup";
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
        var finalPath = IOPath.Combine(destination, $"{safeName}_{timestamp}.zip");
        var tempPath = IOPath.Combine(destination, $"{safeName}_{timestamp}.tmp.zip");
        var compression = profile.Compression switch
        {
            "Fastest" => CompressionLevel.Fastest,
            "No compression" => CompressionLevel.NoCompression,
            _ => CompressionLevel.Optimal
        };
        string? databaseDumpPath = null;

        try
        {
            if (profile.IncludeDatabaseBackup)
            {
                databaseDumpPath = IOPath.Combine(IOPath.GetTempPath(), $"automatic-backups-db-{Guid.NewGuid():N}.sql");
                XamppDatabaseBackup.CreateDump(profile, databaseDumpPath);
                var safeDatabaseName = string.Concat(profile.DatabaseName.Select(c => char.IsLetterOrDigit(c) || ".-_".Contains(c) ? c : '_'));
                if (string.IsNullOrWhiteSpace(safeDatabaseName)) safeDatabaseName = "Database";
                files.Add((databaseDumpPath, $"DatabaseBackups/{safeDatabaseName}.sql"));
            }
            if (files.Count == 0) throw new IOException("No files were found to back up. Check the source and exclusions.");

            using (var archive = ZipFile.Open(tempPath, ZipArchiveMode.Create))
            {
                foreach (var (fullPath, entry) in files)
                    ZipFileExtensions.CreateEntryFromFile(archive, fullPath, entry.Replace('\\', '/'), compression);
            }
            File.Move(tempPath, finalPath, true);
            if (profile.Retention > 0)
            {
                var oldArchives = Directory.EnumerateFiles(destination, $"{safeName}_*.zip")
                    .OrderByDescending(File.GetLastWriteTimeUtc).Skip(profile.Retention);
                foreach (var old in oldArchives)
                {
                    try { File.Delete(old); } catch { }
                }
            }
            return (finalPath, files.Count, new FileInfo(finalPath).Length);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
        finally
        {
            try { if (databaseDumpPath is not null && File.Exists(databaseDumpPath)) File.Delete(databaseDumpPath); } catch { }
        }
    }

    private static void CollectFiles(string root, string current, bool includeHidden, string[] patterns, List<(string FullPath, string Entry)> files)
    {
        foreach (var path in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(path);
            if (!includeHidden && (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            var relative = IOPath.GetRelativePath(root, path);
            if (IsExcluded(relative, patterns)) continue;
            files.Add((path, relative));
        }
        foreach (var path in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
        {
            var info = new DirectoryInfo(path);
            if (!includeHidden && (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            var relative = IOPath.GetRelativePath(root, path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || IsExcluded(relative, patterns)) continue;
            CollectFiles(root, path, includeHidden, patterns, files);
        }
    }

    private static bool IsExcluded(string relative, string[] patterns)
    {
        var segments = relative.Split([IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var pattern in patterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, relative, true)) return true;
            if (segments.Any(segment => FileSystemName.MatchesSimpleExpression(pattern, segment, true))) return true;
        }
        return false;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.##} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.##} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):0.##} GB"
    };
}

public sealed record RecentBackupActivity(string ProfileName, string Summary, string TimeLabel, bool IsFailure);

internal static class PathHelpers
{
    public static string Normalize(string path)
    {
        var full = IOPath.GetFullPath(path);
        var root = IOPath.GetPathRoot(full);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            ? full
            : full.TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);
    }

    public static bool IsChildPath(string parent, string candidate)
    {
        var prefix = parent.EndsWith(IOPath.DirectorySeparatorChar) ? parent : parent + IOPath.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class BackupProfile : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Frequency { get; set; } = "Daily";
    public int EveryHours { get; set; } = 1;
    public string Time { get; set; } = "09:00";
    public string DayOfWeek { get; set; } = "Monday";
    public int DayOfMonth { get; set; } = 1;
    public int Retention { get; set; } = 10;
    public string Compression { get; set; } = "Optimal";
    public string Exclude { get; set; } = "";
    public bool IncludeHidden { get; set; }
    public bool IncludeDatabaseBackup { get; set; }
    public string DatabaseName { get; set; } = "";
    public string XamppDirectory { get; set; } = @"C:\xampp";
    public string DatabaseUsername { get; set; } = "root";
    public int DatabasePort { get; set; } = 3306;
    public string DatabasePasswordProtected { get; set; } = "";
    public bool Enabled { get; set; } = true;
    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLabel)));
        }
    }
    public string StatusLabel => IsRunning ? "●  IN PROGRESS" : Enabled ? "●  ACTIVE" : "Ⅱ  PAUSED";
    public DateTime? LastRunUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string ScheduleLabel => Frequency switch
    {
        "Hourly" => $"Every {EveryHours} hour{(EveryHours == 1 ? "" : "s")}",
        "Weekly" => $"Weekly · {DayOfWeek} at {FormattedScheduleTime}",
        "Monthly" => $"Monthly · Day {DayOfMonth} at {FormattedScheduleTime}",
        _ => $"Daily at {FormattedScheduleTime}"
    };
    private string FormattedScheduleTime => TimeSpan.TryParseExact(Time, "hh\\:mm", CultureInfo.InvariantCulture, out var time)
        ? DateTime.Today.Add(time).ToString("h:mm tt", CultureInfo.InvariantCulture)
        : Time;
    public string LastRunLabel => LastRunUtc is null ? "Last backup: Not run yet" : $"Last backup: {LastRunUtc.Value.ToLocalTime().ToString("dd MMM, h:mm tt", CultureInfo.InvariantCulture)}";

    public string NextBackupLabel => GetNextBackupLabel(DateTime.Now);

    public void RefreshCountdown()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NextBackupLabel)));
    }

    private string GetNextBackupLabel(DateTime now)
    {
        if (!Enabled) return "Next backup: Schedule paused";
        var lastAttempt = (LastAttemptUtc ?? LastRunUtc)?.ToLocalTime();
        if (Frequency == "Hourly" && lastAttempt is null) return "Next backup: Due on first check";
        if (!TimeSpan.TryParseExact(Time, "hh\\:mm", null, out var time)) return "Next backup: Check schedule settings";

        DateTime next;
        if (Frequency == "Hourly")
        {
            next = lastAttempt!.Value.AddHours(Math.Clamp(EveryHours, 1, 24));
        }
        else if (Frequency == "Weekly")
        {
            if (!Enum.TryParse<DayOfWeek>(DayOfWeek, out var day)) return "Next backup: Check schedule settings";
            var offset = ((int)day - (int)now.DayOfWeek + 7) % 7;
            next = now.Date.AddDays(offset).Add(time);
            if (lastAttempt?.Date == next.Date) next = next.AddDays(7);
        }
        else if (Frequency == "Monthly")
        {
            next = MainWindow.MonthlyOccurrence(now.Year, now.Month, DayOfMonth, time);
            if (next <= now || (lastAttempt is not null && lastAttempt.Value >= next))
            {
                var followingMonth = now.AddMonths(1);
                next = MainWindow.MonthlyOccurrence(followingMonth.Year, followingMonth.Month, DayOfMonth, time);
            }
        }
        else
        {
            next = now.Date.Add(time);
            if (lastAttempt?.Date == now.Date) next = next.AddDays(1);
        }

        if (next < now) next = now;
        var remaining = next - now;
        var countdown = remaining.TotalDays >= 1
            ? $"{(int)remaining.TotalDays}d {remaining:hh\\:mm\\:ss}"
            : remaining.ToString("hh\\:mm\\:ss");
        return $"Next backup: in {countdown}";
    }
}
