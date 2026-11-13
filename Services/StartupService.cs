using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Microsoft.Win32;

namespace CoreWatch.Services;

public sealed class StartupEntry : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string? ExePath { get; init; }
    public string Publisher { get; init; } = "";
    public string Source { get; init; } = "";
    public bool MachineWide { get; init; }
    public ImageSource? Icon { get; init; }

    // Where Windows stores the enabled/disabled flag (the same place Task Manager writes).
    internal RegistryHive ApprovedHive;
    internal string ApprovedKey = "";
    internal string ApprovedValue = "";

    bool _enabled;
    public bool Enabled { get => _enabled; set { _enabled = value; Raise(); Raise(nameof(StateText)); } }
    public string StateText => _enabled ? "Enabled" : "Disabled";
    public string Scope => MachineWide ? "All users" : "This user";

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public static class StartupService
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupEntry> Load()
    {
        var list = new List<StartupEntry>();
        ReadRunKey(list, RegistryHive.CurrentUser, RegistryView.Default, "Registry · current user", "Run");
        ReadRunKey(list, RegistryHive.LocalMachine, RegistryView.Registry64, "Registry · all users", "Run");
        ReadRunKey(list, RegistryHive.LocalMachine, RegistryView.Registry32, "Registry · all users (32-bit)", "Run32");
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser, "Startup folder · current user");
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, "Startup folder · all users");
        return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void ReadRunKey(List<StartupEntry> list, RegistryHive hive, RegistryView view, string source, string approvedSub)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(RunKey);
            if (key == null) return;
            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue;
                string cmd = key.GetValue(name)?.ToString() ?? "";
                if (cmd.Length == 0) continue;
                string? exe = ExtractExe(cmd);
                var (product, company) = Describe(exe);
                var entry = new StartupEntry
                {
                    Name = product ?? name,
                    Command = cmd,
                    ExePath = exe,
                    Publisher = company ?? "",
                    Source = source,
                    MachineWide = hive == RegistryHive.LocalMachine,
                    Icon = IconCache.Get(exe),
                    ApprovedHive = hive,
                    ApprovedKey = Approved + approvedSub,
                    ApprovedValue = name,
                };
                entry.Enabled = ReadEnabled(entry);
                list.Add(entry);
            }
        }
        catch { }
    }

    static void ReadFolder(List<StartupEntry> list, string folder, RegistryHive hive, string source)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                string fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var entry = new StartupEntry
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Command = file,
                    ExePath = file,
                    Source = source,
                    MachineWide = hive == RegistryHive.LocalMachine,
                    Icon = IconCache.Get(file),
                    ApprovedHive = hive,
                    ApprovedKey = Approved + "StartupFolder",
                    ApprovedValue = fileName,
                };
                entry.Enabled = ReadEnabled(entry);
                list.Add(entry);
            }
        }
        catch { }
    }

    static bool ReadEnabled(StartupEntry e)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(e.ApprovedHive, RegistryView.Registry64);
            using var key = root.OpenSubKey(e.ApprovedKey);
            if (key?.GetValue(e.ApprovedValue) is byte[] { Length: > 0 } data) return (data[0] & 1) == 0; // 02/06 enabled, 03/07 disabled
        }
        catch { }
        return true; // no flag recorded means enabled
    }

    /// <summary>Enables or disables an entry. Returns an error message, or null on success.</summary>
    public static string? SetEnabled(StartupEntry e, bool enabled)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(e.ApprovedHive, RegistryView.Registry64);
            using var key = root.CreateSubKey(e.ApprovedKey, true);
            var data = new byte[12];
            data[0] = enabled ? (byte)2 : (byte)3;
            if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            key.SetValue(e.ApprovedValue, data, RegistryValueKind.Binary);
            e.Enabled = enabled;
            return null;
        }
        catch (UnauthorizedAccessException) { return "This entry applies to all users. Restart CoreWatch as administrator to change it."; }
        catch (System.Security.SecurityException) { return "This entry applies to all users. Restart CoreWatch as administrator to change it."; }
        catch (Exception ex) { return ex.Message; }
    }

    public static string? ExtractExe(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end > 1) return command[1..end];
        }
        int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return command[..(exe + 4)];
        int space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    static (string? Product, string? Company) Describe(string? exe)
    {
        try
        {
            if (exe == null || !File.Exists(exe)) return (null, null);
            var v = FileVersionInfo.GetVersionInfo(exe);
            string? product = string.IsNullOrWhiteSpace(v.FileDescription) ? v.ProductName : v.FileDescription;
            return (string.IsNullOrWhiteSpace(product) ? null : product.Trim(), string.IsNullOrWhiteSpace(v.CompanyName) ? null : v.CompanyName.Trim());
        }
        catch { return (null, null); }
    }
}
