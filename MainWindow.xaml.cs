using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace AutomaticBackups;

public partial class MainWindow : Window
{
    private static readonly string AppFolder = App.DataDirectory;
    private static readonly string ProfilesPath = IOPath.Combine(AppFolder, "profiles.json");
    private static readonly string LogPath = IOPath.Combine(AppFolder, "backup.log");
    private readonly ObservableCollection<BackupProfile> _profiles = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _lastScheduleCheck = DateTime.MinValue;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        ProfileList.ItemsSource = _profiles;
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
        try { if (File.Exists(LogPath)) ActivityLog.Text = string.Join(Environment.NewLine, File.ReadLines(LogPath).TakeLast(150)) + Environment.NewLine; ActivityLog.ScrollToEnd(); }
        catch { }
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
        ActivityLog.AppendText(line + Environment.NewLine);
        ActivityLog.ScrollToEnd();
    }

    private void RefreshDashboard()
    {
        ProfileList.Items.Refresh();
        StatProfiles.Text = _profiles.Count.ToString();
        ProfileCount.Text = _profiles.Count == 1 ? "1 profile configured" : $"{_profiles.Count} profiles configured";
        EmptyState.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var enabled = _profiles.Where(p => p.Enabled).ToArray();
        StatNext.Text = enabled.Length == 0 ? "Not scheduled" : $"{enabled.Length} active schedule{(enabled.Length == 1 ? "" : "s")} · checks every 20 sec";
        var selected = ProfileList.SelectedItem as BackupProfile;
        var hasSelection = selected is not null;
        RunButton.IsEnabled = hasSelection && !_busy;
        EditButton.IsEnabled = hasSelection && !_busy;
        DeleteButton.IsEnabled = hasSelection && !_busy;
        if (selected is null)
        {
            SelectedName.Text = "Select a backup"; SelectedSchedule.Text = "Your backup details will appear here.";
            SelectedSource.Text = "—"; SelectedDestination.Text = "—"; SelectedOptions.Text = "Choose a profile to view its ZIP and retention settings.";
        }
        else
        {
            SelectedName.Text = selected.Name;
            SelectedSchedule.Text = selected.Enabled ? selected.ScheduleLabel : $"{selected.ScheduleLabel} · Paused";
            SelectedSource.Text = selected.Source; SelectedDestination.Text = selected.Destination;
            SelectedOptions.Text = $"{selected.Compression} compression  ·  Keep newest {selected.Retention} ZIPs\nExclusions: {(string.IsNullOrWhiteSpace(selected.Exclude) ? "None" : selected.Exclude)}";
        }
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshDashboard();

    private void ProfilesNav_Click(object sender, RoutedEventArgs e) { }

    private void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow { Owner = this };
        settings.ShowDialog();
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

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        ActivityLog.Clear();
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
        var last = p.LastRunUtc?.ToLocalTime();
        if (p.Frequency == "Hourly") return last is null || (now - last.Value).TotalHours >= p.EveryHours;
        if (last?.Date == now.Date) return false;
        if (!TimeSpan.TryParseExact(p.Time, "hh\\:mm", null, out var time)) return false;
        if (now.TimeOfDay < time) return false;
        return p.Frequency == "Daily" || (p.Frequency == "Weekly" && now.DayOfWeek.ToString() == p.DayOfWeek);
    }

    private async Task RunBackupAsync(BackupProfile profile, bool scheduled)
    {
        if (_busy) return;
        _busy = true; RunButton.IsEnabled = false; (ScheduleStatus.Text, ScheduleStatus.Foreground) = ("●  BACKUP RUNNING", Brushes.DarkOrange);
        Log($"{(scheduled ? "Scheduled backup started" : "Backup started")} · {profile.Name}");
        try
        {
            var result = await Task.Run(() => CreateArchive(profile));
            profile.LastRunUtc = DateTime.UtcNow;
            SaveProfiles(); RefreshDashboard();
            Log($"Backup complete · {profile.Name} · {result.Count:N0} files · {FormatSize(result.Size)} · {result.Archive}");
        }
        catch (Exception ex)
        {
            Log($"Backup failed · {profile.Name} · {ex.Message}", true);
            MessageBox.Show(this, $"Backup failed for ‘{profile.Name}’:\n\n{ex.Message}", "Automatic Backups", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false; ScheduleStatus.Text = "●  READY"; ScheduleStatus.Foreground = Brushes.ForestGreen; RefreshDashboard();
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
        if (files.Count == 0) throw new IOException("No files were found to back up. Check the source and exclusions.");

        var safeName = string.Concat(profile.Name.Select(c => "<>:\"/\\|?*".Contains(c) || char.IsControl(c) ? '_' : c)).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "Backup";
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
        var finalPath = IOPath.Combine(destination, $"{safeName}_{timestamp}.zip");
        var tempPath = IOPath.Combine(destination, $".{safeName}_{timestamp}.tmp.zip");
        var compression = profile.Compression switch
        {
            "Fastest" => CompressionLevel.Fastest,
            "No compression" => CompressionLevel.NoCompression,
            _ => CompressionLevel.Optimal
        };

        try
        {
            using (var archive = ZipFile.Open(tempPath, ZipArchiveMode.Create))
            {
                foreach (var (fullPath, entry) in files)
                    ZipFileExtensions.CreateEntryFromFile(archive, fullPath, entry.Replace('\\', '/'), compression);
            }
            File.Move(tempPath, finalPath, true);
            var oldArchives = Directory.EnumerateFiles(destination, $"{safeName}_*.zip")
                .OrderByDescending(File.GetLastWriteTimeUtc).Skip(profile.Retention);
            foreach (var old in oldArchives)
            {
                try { File.Delete(old); } catch { }
            }
            return (finalPath, files.Count, new FileInfo(finalPath).Length);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
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
    public int Retention { get; set; } = 10;
    public string Compression { get; set; } = "Optimal";
    public string Exclude { get; set; } = "";
    public bool IncludeHidden { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime? LastRunUtc { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string ScheduleLabel => Frequency switch
    {
        "Hourly" => $"Every {EveryHours} hour{(EveryHours == 1 ? "" : "s")}",
        "Weekly" => $"Weekly · {DayOfWeek} at {Time}",
        _ => $"Daily at {Time}"
    };
    public string LastRunLabel => LastRunUtc is null ? "Last backup: Not run yet" : $"Last backup: {LastRunUtc.Value.ToLocalTime():dd MMM, HH:mm}";

    public string NextBackupLabel => GetNextBackupLabel(DateTime.Now);

    public void RefreshCountdown()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NextBackupLabel)));
    }

    private string GetNextBackupLabel(DateTime now)
    {
        if (!Enabled) return "Next backup: Schedule paused";
        if (Frequency == "Hourly" && LastRunUtc is null) return "Next backup: Due on first check";
        if (!TimeSpan.TryParseExact(Time, "hh\\:mm", null, out var time)) return "Next backup: Check schedule settings";

        DateTime next;
        if (Frequency == "Hourly")
        {
            next = LastRunUtc!.Value.ToLocalTime().AddHours(Math.Clamp(EveryHours, 1, 24));
        }
        else if (Frequency == "Weekly")
        {
            if (!Enum.TryParse<DayOfWeek>(DayOfWeek, out var day)) return "Next backup: Check schedule settings";
            var offset = ((int)day - (int)now.DayOfWeek + 7) % 7;
            next = now.Date.AddDays(offset).Add(time);
            if (LastRunUtc?.ToLocalTime().Date == next.Date) next = next.AddDays(7);
        }
        else
        {
            next = now.Date.Add(time);
            if (LastRunUtc?.ToLocalTime().Date == now.Date) next = next.AddDays(1);
        }

        if (next < now) next = now;
        var remaining = next - now;
        var countdown = remaining.TotalDays >= 1
            ? $"{(int)remaining.TotalDays}d {remaining:hh\\:mm\\:ss}"
            : remaining.ToString("hh\\:mm\\:ss");
        return $"Next backup: in {countdown}";
    }
}
