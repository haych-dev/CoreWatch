using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using Microsoft.Win32;

namespace CoreWatch.Services;

public sealed class InstalledApp
{
    public string Name { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public long SizeBytes { get; init; }
    public DateTime? Installed { get; init; }
    public string UninstallCommand { get; init; } = "";
    public string? InstallLocation { get; init; }
    public ImageSource? Icon { get; init; }

    public string SizeText => SizeBytes > 0 ? Fmt.Bytes(SizeBytes) : "—";
    public string InstalledText => Installed?.ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? "—";
    public bool CanUninstall => UninstallCommand.Length > 0;
}

public static class AppsService
{
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledApp> Load()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        Read(apps, RegistryHive.LocalMachine, RegistryView.Registry64);
        Read(apps, RegistryHive.LocalMachine, RegistryView.Registry32);
        Read(apps, RegistryHive.CurrentUser, RegistryView.Default);
        return apps.Values.OrderByDescending(a => a.SizeBytes).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void Read(Dictionary<string, InstalledApp> apps, RegistryHive hive, RegistryView view)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(UninstallKey);
            if (key == null) return;
            foreach (var sub in key.GetSubKeyNames())
            {
                try
                {
                    using var k = key.OpenSubKey(sub);
                    if (k == null) continue;
                    string name = (k.GetValue("DisplayName") as string ?? "").Trim();
                    if (name.Length == 0) continue;
                    if (Convert.ToInt32(k.GetValue("SystemComponent") ?? 0) == 1) continue;
                    if (k.GetValue("ParentKeyName") != null) continue; // patches / sub-components

                    string uninstall = k.GetValue("UninstallString") as string ?? "";
                    if (Convert.ToInt32(k.GetValue("WindowsInstaller") ?? 0) == 1 && sub.StartsWith('{'))
                        uninstall = $"MsiExec.exe /X{sub}";

                    string version = k.GetValue("DisplayVersion") as string ?? "";
                    string id = name + "|" + version;
                    if (apps.ContainsKey(id)) continue;

                    DateTime? installed = null;
                    if (k.GetValue("InstallDate") is string ds && DateTime.TryParseExact(ds, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                        installed = dt;

                    string? iconPath = (k.GetValue("DisplayIcon") as string)?.Trim('"');
                    if (iconPath != null)
                    {
                        int comma = iconPath.LastIndexOf(',');
                        if (comma > 2) iconPath = iconPath[..comma].Trim('"');
                    }
                    string? location = k.GetValue("InstallLocation") as string;

                    apps[id] = new InstalledApp
                    {
                        Name = name,
                        Publisher = (k.GetValue("Publisher") as string ?? "").Trim(),
                        Version = version,
                        SizeBytes = Convert.ToInt64(k.GetValue("EstimatedSize") ?? 0) * 1024,
                        Installed = installed,
                        UninstallCommand = uninstall,
                        InstallLocation = string.IsNullOrWhiteSpace(location) ? null : location,
                        Icon = IconCache.Get(FindIconSource(iconPath, location)),
                    };
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>Picks a file to take the app's icon from: its registered icon, else the largest exe in its install folder.</summary>
    static string? FindIconSource(string? iconPath, string? location)
    {
        try
        {
            if (iconPath is { Length: > 0 } && File.Exists(iconPath)) return iconPath;
            if (string.IsNullOrWhiteSpace(location) || !Directory.Exists(location)) return null;
            return new DirectoryInfo(location).EnumerateFiles("*.exe")
                .Where(f => !f.Name.Contains("unins", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.Length).FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    /// <summary>Launches the app's own uninstaller (the user completes it in the vendor's UI).</summary>
    public static bool Uninstall(InstalledApp app)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{app.UninstallCommand}\"") { UseShellExecute = false, CreateNoWindow = true });
            return true;
        }
        catch { return false; }
    }
}

public static class MemoryOptimizer
{
    /// <summary>Asks Windows to trim the working set of every process we can open. Returns how many were trimmed.</summary>
    public static int TrimWorkingSets()
    {
        int trimmed = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id <= 4 || p.Id == Environment.ProcessId) continue;
                var h = Native.OpenProcess(Native.PROCESS_SET_QUOTA | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try { if (Native.EmptyWorkingSet(h)) trimmed++; }
                finally { Native.CloseHandle(h); }
            }
            catch { }
            finally { p.Dispose(); }
        }
        return trimmed;
    }

    /// <summary>Purges the standby (cached) page list. Requires administrator rights.</summary>
    public static bool PurgeStandbyList()
    {
        if (!Sys.IsAdmin) return false;
        Native.EnablePrivilege("SeProfileSingleProcessPrivilege");
        int command = 4; // MemoryPurgeStandbyList
        return Native.NtSetSystemInformation(Native.SystemMemoryListInformation, ref command, sizeof(int)) == 0;
    }
}
