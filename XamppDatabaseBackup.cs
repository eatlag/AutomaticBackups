using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AutomaticBackups;

internal static class XamppDatabaseBackup
{
    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "information_schema", "mysql", "performance_schema", "phpmyadmin", "sys"
    };

    public static string ProtectPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return string.Empty;
        var data = Encoding.UTF8.GetBytes(password);
        var input = CreateBlob(data);
        var output = new DataBlob();
        try
        {
            if (!CryptProtectData(ref input, "Automatic Backups XAMPP password", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var protectedBytes = new byte[output.Length];
            Marshal.Copy(output.Data, protectedBytes, 0, output.Length);
            return Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            FreeBlob(ref input);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    public static string UnprotectPassword(string protectedPassword)
    {
        if (string.IsNullOrEmpty(protectedPassword)) return string.Empty;
        var data = Convert.FromBase64String(protectedPassword);
        var input = CreateBlob(data);
        var output = new DataBlob();
        IntPtr description = IntPtr.Zero;
        try
        {
            if (!CryptUnprotectData(ref input, out description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var clearBytes = new byte[output.Length];
            Marshal.Copy(output.Data, clearBytes, 0, output.Length);
            return Encoding.UTF8.GetString(clearBytes);
        }
        finally
        {
            FreeBlob(ref input);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            if (description != IntPtr.Zero) LocalFree(description);
        }
    }

    public static void ValidateXamppFolder(string xamppFolder)
    {
        var bin = GetBinFolder(xamppFolder);
        if (!File.Exists(Path.Combine(bin, "mysql.exe")))
            throw new FileNotFoundException("mysql.exe was not found. Choose the XAMPP installation folder that contains mysql\\bin.");
        if (!File.Exists(FindDumpClient(bin)))
            throw new FileNotFoundException("Neither mysqldump.exe nor mariadb-dump.exe was found in XAMPP's mysql\\bin folder.");
    }

    public static IReadOnlyList<string> ListDatabases(string xamppFolder, string username, string password, int port)
    {
        var bin = GetBinFolder(xamppFolder);
        var mysql = Path.Combine(bin, "mysql.exe");
        if (!File.Exists(mysql)) throw new FileNotFoundException("mysql.exe was not found. Choose your XAMPP installation folder.");
        var optionsFile = CreateOptionsFile(username, password, port);
        try
        {
            var start = CreateStartInfo(mysql);
            start.ArgumentList.Add($"--defaults-extra-file={optionsFile}");
            start.ArgumentList.Add("--batch");
            start.ArgumentList.Add("--skip-column-names");
            start.ArgumentList.Add("--execute=SHOW DATABASES;");
            var (exitCode, stdout, stderr) = RunForText(start);
            if (exitCode != 0) throw new InvalidOperationException(FormatClientError("Could not list XAMPP databases", stderr, exitCode));
            return stdout.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(name => name.Length > 0 && !SystemDatabases.Contains(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally { TryDelete(optionsFile); }
    }

    public static void CreateDump(BackupProfile profile, string outputPath)
    {
        var bin = GetBinFolder(profile.XamppDirectory);
        var dumpClient = FindDumpClient(bin);
        if (!File.Exists(dumpClient)) throw new FileNotFoundException("XAMPP's database dump tool was not found.");
        if (string.IsNullOrWhiteSpace(profile.DatabaseName)) throw new InvalidOperationException("Select an XAMPP database in the profile settings.");
        var optionsFile = CreateOptionsFile(profile.DatabaseUsername, UnprotectPassword(profile.DatabasePasswordProtected), profile.DatabasePort);
        try
        {
            var start = CreateStartInfo(dumpClient);
            start.ArgumentList.Add($"--defaults-extra-file={optionsFile}");
            start.ArgumentList.Add("--single-transaction");
            start.ArgumentList.Add("--routines");
            start.ArgumentList.Add("--events");
            start.ArgumentList.Add("--triggers");
            start.ArgumentList.Add("--databases");
            start.ArgumentList.Add(profile.DatabaseName);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The XAMPP database dump tool could not be started.");
            var stderrTask = process.StandardError.ReadToEndAsync();
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                process.StandardOutput.BaseStream.CopyTo(output);
            process.WaitForExit();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(FormatClientError("The XAMPP database backup failed", stderr, process.ExitCode));
        }
        finally { TryDelete(optionsFile); }
    }

    private static string GetBinFolder(string xamppFolder)
    {
        if (string.IsNullOrWhiteSpace(xamppFolder)) throw new DirectoryNotFoundException("Choose your XAMPP installation folder.");
        var root = Path.GetFullPath(xamppFolder.Trim());
        var bin = Path.Combine(root, "mysql", "bin");
        if (!Directory.Exists(bin)) throw new DirectoryNotFoundException($"The XAMPP database tools folder does not exist: {bin}");
        return bin;
    }

    private static string FindDumpClient(string bin)
    {
        var mariadbDump = Path.Combine(bin, "mariadb-dump.exe");
        return File.Exists(mariadbDump) ? mariadbDump : Path.Combine(bin, "mysqldump.exe");
    }

    private static ProcessStartInfo CreateStartInfo(string executable) => new(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
    };

    private static (int ExitCode, string Stdout, string Stderr) RunForText(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The XAMPP MySQL client could not be started.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);
        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static string CreateOptionsFile(string username, string password, int port)
    {
        if (string.IsNullOrWhiteSpace(username)) throw new InvalidOperationException("Enter the XAMPP database username.");
        if (port is < 1 or > 65535) throw new InvalidOperationException("The XAMPP database port must be between 1 and 65535.");
        var path = Path.Combine(Path.GetTempPath(), $"automatic-backups-{Guid.NewGuid():N}.cnf");
        var contents = $"[client]\r\nhost=127.0.0.1\r\nport={port}\r\nuser=\"{EscapeOptionValue(username)}\"\r\npassword=\"{EscapeOptionValue(password)}\"\r\n";
        File.WriteAllText(path, contents, new UTF8Encoding(false));
        return path;
    }

    private static string EscapeOptionValue(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", string.Empty).Replace("\n", string.Empty);

    private static string FormatClientError(string message, string stderr, int exitCode) =>
        string.IsNullOrWhiteSpace(stderr) ? $"{message} (exit code {exitCode})." : $"{message}: {stderr.Trim()}";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static DataBlob CreateBlob(byte[] data)
    {
        var pointer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        return new DataBlob { Length = data.Length, Data = pointer };
    }

    private static void FreeBlob(ref DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero) Marshal.FreeHGlobal(blob.Data);
        blob = new DataBlob();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
