using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace CoreWatch.Services;

public sealed record RegCategory(string Id, string Name, string Description);

/// <summary>One leftover registry entry: either a single value or a whole key.</summary>
public sealed class RegIssue : INotifyPropertyChanged
{
    public required RegCategory Category { get; init; }
    public required string Title { get; init; }
    public required string Reason { get; init; }
    public required RegistryHive Hive { get; init; }
    public required string KeyPath { get; init; }
    /// <summary>The value to remove; ignored when <see cref="WholeKey"/> is set. Empty means the key's default value.</summary>
    public string ValueName { get; init; } = "";
    public bool WholeKey { get; init; }

    public bool Locked => Hive == RegistryHive.LocalMachine && !Sys.IsAdmin;
    public bool CanSelect => !Locked;
    public string FullKey => (Hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER") + "\\" + KeyPath;
    public string Location => (Hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU") + "\\" + KeyPath +
                              (WholeKey ? "" : "  →  " + (ValueName.Length == 0 ? "(Default)" : ValueName));

    bool _selected;
    public bool Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

static class Reg
{
    static readonly RegistryKey Hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
    static readonly RegistryKey Hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    /// <summary>Opens a key in the 64-bit view; 32-bit entries are reached through their explicit WOW6432Node path.</summary>
    public static RegistryKey? Open(RegistryHive hive, string path, bool writable = false) =>
        (hive == RegistryHive.LocalMachine ? Hklm : Hkcu).OpenSubKey(path, writable);

    public static RegistryKey Create(RegistryHive hive, string path) =>
        (hive == RegistryHive.LocalMachine ? Hklm : Hkcu).CreateSubKey(path, true);

    /// <summary>Reads a value exactly as stored: its registry type number and raw bytes.</summary>
    public static bool TryReadRaw(RegistryKey key, string name, out uint type, out byte[] data)
    {
        data = [];
        uint size = 0;
        if (Native.RegQueryValueEx(key.Handle, name, IntPtr.Zero, out type, null, ref size) != 0) return false;
        if (size == 0) return true;
        var buffer = new byte[size];
        if (Native.RegQueryValueEx(key.Handle, name, IntPtr.Zero, out type, buffer, ref size) != 0) return false;
        if (size < buffer.Length) Array.Resize(ref buffer, (int)size);
        data = buffer;
        return true;
    }
}

/// <summary>Finds registry entries that point at programs and files which no longer exist.</summary>
public static class RegistryScanner
{
    public static readonly RegCategory Startup = new("startup", "Startup entries", "Programs set to start with Windows whose file no longer exists.");
    public static readonly RegCategory Uninstall = new("uninstall", "Installed apps list", "Entries in the installed-apps list for programs that have already been removed.");
    public static readonly RegCategory AppPaths = new("app-paths", "Application paths", "Names Windows uses to find a program (for example from Win+R) that point to a missing file.");
    public static readonly RegCategory OpenWith = new("open-with", "Open with list", "Programs offered under \"Open with\" that are no longer installed.");
    public static readonly RegCategory SharedDlls = new("shared-files", "Shared files", "Usage counters for shared DLLs that have been deleted.");
    public static readonly RegCategory Fonts = new("fonts", "Fonts", "Registered fonts whose font file is missing.");
    public static readonly RegCategory NameCache = new("name-cache", "Program name cache", "Cached display names for programs that are no longer there.");
    public static readonly RegCategory Compat = new("compatibility", "Compatibility records", "Compatibility settings and records kept for programs that no longer exist.");

    public static IReadOnlyList<RegCategory> Categories { get; } = [Startup, Uninstall, AppPaths, OpenWith, SharedDlls, Fonts, NameCache, Compat];

    const string Hkcu = "Software", Hklm = "SOFTWARE", Wow = @"SOFTWARE\WOW6432Node";
    static readonly (RegistryHive Hive, string Software, bool Wow)[] Roots =
    [
        (RegistryHive.CurrentUser, Hkcu, false),
        (RegistryHive.LocalMachine, Hklm, false),
        (RegistryHive.LocalMachine, Wow, true),
    ];

    public static List<RegIssue> Scan(CancellationToken ct = default)
    {
        var probe = new Probe();
        var found = new List<RegIssue>();
        void Step(Action scan)
        {
            if (ct.IsCancellationRequested) return;
            try { scan(); } catch { } // an unreadable key only skips its own check
        }

        foreach (var root in Roots)
        {
            bool machine = root.Hive == RegistryHive.LocalMachine;
            string cv = root.Software + @"\Microsoft\Windows\CurrentVersion";

            Step(() => EachValue(root.Hive, cv + @"\Run", (key, name) =>
            {
                if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string command &&
                    probe.MissingTarget(command, root.Wow, machine) is { } target)
                    found.Add(Value(Startup, root.Hive, cv + @"\Run", name, name.Length == 0 ? "(Default)" : name, $"Starts {target}, which no longer exists"));
            }));

            Step(() => EachSubKey(root.Hive, cv + @"\Uninstall", (name, key) =>
            {
                if (key.GetValue("DisplayName") is not string display || display.Trim().Length == 0) return;
                if (key.GetValue("SystemComponent") is int sc && sc == 1) return;
                if (key.GetValue("WindowsInstaller") is int msi && msi == 1) return; // removed through msiexec, which always exists
                if (probe.MissingTarget(Raw(key, "UninstallString"), root.Wow, machine) is not { } target) return;
                // Only when nothing else of the program is left either.
                if (Raw(key, "InstallLocation") is { Length: > 0 } location && !probe.IsGone(location, root.Wow, machine)) return;
                if (IconFile(Raw(key, "DisplayIcon")) is { Length: > 0 } icon && !probe.IsGone(icon, root.Wow, machine)) return;
                found.Add(Key(Uninstall, root.Hive, $@"{cv}\Uninstall\{name}", display.Trim(), $"Its uninstaller {target} no longer exists"));
            }));

            Step(() => EachSubKey(root.Hive, cv + @"\App Paths", (name, key) =>
            {
                if (Raw(key, "") is { } path && probe.IsGone(path, root.Wow, machine))
                    found.Add(Key(AppPaths, root.Hive, $@"{cv}\App Paths\{name}", name, $"Points to {probe.Resolve(path, machine)}, which no longer exists"));
            }));

            if (machine)
                Step(() => EachValue(root.Hive, cv + @"\SharedDLLs", (_, name) =>
                {
                    if (probe.IsGone(name, root.Wow, true) && !ShipsBroken(name))
                        found.Add(Value(SharedDlls, root.Hive, cv + @"\SharedDLLs", name, Path.GetFileName(name), $"{name} no longer exists"));
                }));
        }

        foreach (var (hive, path) in new[] { (RegistryHive.CurrentUser, @"Software\Classes\Applications"), (RegistryHive.LocalMachine, @"SOFTWARE\Classes\Applications") })
            Step(() => EachSubKey(hive, path, (name, key) =>
            {
                using var command = key.OpenSubKey(@"shell\open\command");
                if (command != null && probe.MissingTarget(Raw(command, ""), false, hive == RegistryHive.LocalMachine) is { } target)
                    found.Add(Key(OpenWith, hive, $@"{path}\{name}", name, $"Opens files with {target}, which no longer exists"));
            }));

        string fontFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        foreach (var (hive, path) in new[] { (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"), (RegistryHive.CurrentUser, @"Software\Microsoft\Windows NT\CurrentVersion\Fonts") })
            Step(() => EachValue(hive, path, (key, name) =>
            {
                if (key.GetValue(name) is not string file || file.Trim().Length == 0) return;
                string full = file.Contains('\\') ? file : Path.Combine(fontFolder, file.Trim());
                if (probe.IsGone(full, false, hive == RegistryHive.LocalMachine) && !ShipsBroken(full))
                    found.Add(Value(Fonts, hive, path, name, name, $"Font file {full} no longer exists"));
            }));

        const string muiCache = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
        Step(() => EachValue(RegistryHive.CurrentUser, muiCache, (_, name) =>
        {
            foreach (var (suffix, label) in new[] { (".FriendlyAppName", "name"), (".ApplicationCompany", "publisher") })
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name[..^suffix.Length] is var file && probe.IsGone(file))
                    found.Add(Value(NameCache, RegistryHive.CurrentUser, muiCache, name, $"{Path.GetFileName(file)} ({label})", $"{file} no longer exists"));
        }));

        const string compat = @"Microsoft\Windows NT\CurrentVersion\AppCompatFlags";
        foreach (var (hive, path) in new[]
                 {
                     (RegistryHive.CurrentUser, $@"Software\{compat}\Compatibility Assistant\Store"),
                     (RegistryHive.CurrentUser, $@"Software\{compat}\Layers"),
                     (RegistryHive.LocalMachine, $@"SOFTWARE\{compat}\Layers"),
                 })
            Step(() => EachValue(hive, path, (_, name) =>
            {
                if (probe.IsGone(name, false, hive == RegistryHive.LocalMachine))
                    found.Add(Value(Compat, hive, path, name, Path.GetFileName(name), $"{name} no longer exists"));
            }));

        return found;
    }

    /// <summary>
    /// Entries that point nowhere on every Windows install (old .NET 1.x counters, the HoloLens icon font).
    /// They are Windows' own, not leftovers, so they are not reported.
    /// </summary>
    static bool ShipsBroken(string path) =>
        path.Contains(@"\Microsoft.NET\Framework\v1.0.3705\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\Microsoft.NET\Framework\v1.1.4322\", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(@"\Fonts\holomdl2.ttf", StringComparison.OrdinalIgnoreCase);

    static RegIssue Value(RegCategory category, RegistryHive hive, string key, string value, string title, string reason) =>
        new() { Category = category, Hive = hive, KeyPath = key, ValueName = value, Title = title, Reason = reason };

    static RegIssue Key(RegCategory category, RegistryHive hive, string key, string title, string reason) =>
        new() { Category = category, Hive = hive, KeyPath = key, WholeKey = true, Title = title, Reason = reason };

    static string? Raw(RegistryKey key, string name) => key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

    /// <summary>"C:\app\app.exe,0" → "C:\app\app.exe".</summary>
    static string? IconFile(string? icon)
    {
        if (icon == null) return null;
        int comma = icon.LastIndexOf(',');
        return comma > 0 && int.TryParse(icon[(comma + 1)..].Trim().Trim('"'), out _) ? icon[..comma] : icon;
    }

    static void EachValue(RegistryHive hive, string path, Action<RegistryKey, string> visit)
    {
        using var key = Reg.Open(hive, path);
        if (key == null) return;
        foreach (string name in key.GetValueNames())
            try { visit(key, name); } catch { }
    }

    static void EachSubKey(RegistryHive hive, string path, Action<string, RegistryKey> visit)
    {
        using var key = Reg.Open(hive, path);
        if (key == null) return;
        foreach (string name in key.GetSubKeyNames())
            try
            {
                using var sub = key.OpenSubKey(name);
                if (sub != null) visit(name, sub);
            }
            catch { }
    }

    /// <summary>
    /// Decides whether a path is really gone. It only ever says "gone" for a full path on a fixed, ready drive that
    /// Windows reports as not found; anything it cannot judge (network, removable, access denied) counts as present.
    /// </summary>
    sealed class Probe
    {
        static readonly string[] UserVariables = ["%USERPROFILE%", "%APPDATA%", "%LOCALAPPDATA%", "%TEMP%", "%TMP%", "%HOMEPATH%", "%USERNAME%"];
        static readonly (string From, string To)[] Wow64Folders = BuildWow64Folders();

        readonly HashSet<char> _fixedDrives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        readonly Dictionary<string, bool> _exists = new(StringComparer.OrdinalIgnoreCase);

        static (string, string)[] BuildWow64Folders()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return
            [
                (Path.Combine(windows, "System32") + "\\", Path.Combine(windows, "SysWOW64") + "\\"),
                (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "\\", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + "\\"),
            ];
        }

        /// <summary>The expanded full path, or null when the text is not something this scan can judge.</summary>
        public string? Resolve(string? raw, bool machine = false)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim().Trim('"').Trim();
            // A machine-wide entry that mentions a per-user folder may belong to another account.
            if (machine && UserVariables.Any(v => s.Contains(v, StringComparison.OrdinalIgnoreCase))) return null;
            s = Environment.ExpandEnvironmentVariables(s);
            if (s.Length < 4 || !char.IsAsciiLetter(s[0]) || s[1] != ':' || s[2] != '\\') return null;
            if (s.IndexOfAny(['%', '*', '?', '<', '>', '|', '"']) >= 0) return null;
            return _fixedDrives.Contains(char.ToUpperInvariant(s[0])) ? s : null;
        }

        bool Exists(string path)
        {
            if (_exists.TryGetValue(path, out bool known)) return known;
            bool exists;
            try { File.GetAttributes(path); exists = true; }
            catch (FileNotFoundException) { exists = false; }
            catch (DirectoryNotFoundException) { exists = false; }
            catch { exists = true; }
            return _exists[path] = exists;
        }

        bool ExistsAnywhere(string path, bool wow)
        {
            if (Exists(path)) return true;
            if (!wow) return false;
            // 32-bit programs see System32 and Program Files redirected to their 32-bit twins.
            foreach (var (from, to) in Wow64Folders)
                if (path.StartsWith(from, StringComparison.OrdinalIgnoreCase) && Exists(to + path[from.Length..])) return true;
            return false;
        }

        public bool IsGone(string? raw, bool wow = false, bool machine = false) =>
            Resolve(raw, machine) is { } path && !ExistsAnywhere(path, wow);

        /// <summary>For a command line: the program it would run, when that program is gone. Null when it exists or cannot be judged.</summary>
        public string? MissingTarget(string? command, bool wow, bool machine)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            string c = command.Trim();
            if (c.StartsWith('"'))
            {
                int close = c.IndexOf('"', 1);
                if (close < 0) return null;
                string quoted = c[1..close];
                return IsGone(quoted, wow, machine) ? Resolve(quoted, machine) : null;
            }

            if (machine && UserVariables.Any(v => c.Contains(v, StringComparison.OrdinalIgnoreCase))) return null;
            c = Environment.ExpandEnvironmentVariables(c);
            // Unquoted paths may contain spaces: if any leading part is a real file, the program is there.
            for (int space = c.IndexOf(' '); ; space = c.IndexOf(' ', space + 1))
            {
                string candidate = space < 0 ? c : c[..space];
                if (Resolve(candidate) is not { } path) return null;
                if (ExistsAnywhere(path, wow) || ExistsAnywhere(path + ".exe", wow)) return null;
                if (space < 0) break;
            }
            int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe < 0) return null;
            string program = c[..(exe + 4)];
            return IsGone(program, wow) ? program : null;
        }
    }
}

public sealed record RegFixResult(int Removed, int Failed, string? BackupFile, string? Error);
public sealed record RegBackup(string File, DateTime When, int Entries, bool NeedsAdmin, long Size);

/// <summary>Removes leftover entries after saving them to a .reg file, and restores from those files.</summary>
public static class RegistryFixer
{
    public static string BackupFolder => Path.Combine(Settings.Folder, "RegistryBackups");

    public static RegFixResult Fix(IReadOnlyList<RegIssue> issues, string? backupFolder = null)
    {
        // 1. Save every entry exactly as it is now. Anything that cannot be saved is not removed.
        var text = new StringBuilder();
        var saved = new List<RegIssue>();
        int failed = 0;
        foreach (var issue in issues)
        {
            try
            {
                using var key = Reg.Open(issue.Hive, issue.KeyPath);
                if (key == null) continue; // already gone
                var part = new StringBuilder();
                if (issue.WholeKey) ExportKey(part, issue.FullKey, key);
                else
                {
                    if (!Reg.TryReadRaw(key, issue.ValueName, out uint type, out byte[] data)) continue;
                    part.Append('[').Append(issue.FullKey).Append("]\r\n").Append(FormatValue(issue.ValueName, type, data)).Append("\r\n\r\n");
                }
                text.Append(part);
                saved.Add(issue);
            }
            catch { failed++; }
        }
        if (saved.Count == 0) return new RegFixResult(0, failed, null, failed > 0 ? "The entries could not be read for backup, so nothing was removed." : null);

        string file;
        try
        {
            string folder = backupFolder ?? BackupFolder;
            Directory.CreateDirectory(folder);
            file = Path.Combine(folder, $"Registry backup {DateTime.Now:yyyy-MM-dd HH.mm.ss}.reg");
            string header = "Windows Registry Editor Version 5.00\r\n\r\n" +
                            $"; CoreWatch registry backup. Entries: {saved.Count}\r\n" +
                            "; Use Restore in CoreWatch, or double-click this file, to put these entries back.\r\n\r\n";
            File.WriteAllText(file, header + text, Encoding.Unicode);
        }
        catch (Exception ex) { return new RegFixResult(0, issues.Count, null, "The backup could not be saved, so nothing was removed: " + ex.Message); }

        // 2. Remove.
        int removed = 0;
        foreach (var issue in saved)
        {
            try
            {
                if (issue.WholeKey)
                {
                    int cut = issue.KeyPath.LastIndexOf('\\');
                    using var parent = Reg.Open(issue.Hive, issue.KeyPath[..cut], true) ?? throw new IOException("Key not found.");
                    parent.DeleteSubKeyTree(issue.KeyPath[(cut + 1)..], false);
                }
                else
                {
                    using var key = Reg.Open(issue.Hive, issue.KeyPath, true) ?? throw new IOException("Key not found.");
                    key.DeleteValue(issue.ValueName, false);
                }
                removed++;
            }
            catch { failed++; }
        }
        return new RegFixResult(removed, failed, file, null);
    }

    /// <summary>Puts a backup's entries back. Returns an error message, or null on success.</summary>
    public static string? Restore(string file)
    {
        try
        {
            Tool.Run("reg.exe", $"import \"{file}\"", mustSucceed: true);
            return null;
        }
        catch (UnauthorizedAccessException) { return "Windows refused the change. Restart CoreWatch as administrator and try again."; }
        catch (Exception ex) { return ex.Message; }
    }

    public static List<RegBackup> ListBackups()
    {
        var list = new List<RegBackup>();
        try
        {
            if (!Directory.Exists(BackupFolder)) return list;
            foreach (var info in new DirectoryInfo(BackupFolder).GetFiles("*.reg"))
            {
                try
                {
                    string text = File.ReadAllText(info.FullName);
                    int entries = 0;
                    const string marker = "Entries: ";
                    int at = text.IndexOf(marker, StringComparison.Ordinal);
                    if (at >= 0) int.TryParse(new string(text[(at + marker.Length)..].TakeWhile(char.IsDigit).ToArray()), out entries);
                    list.Add(new RegBackup(info.FullName, info.LastWriteTime, entries, text.Contains("[HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase), info.Length));
                }
                catch { }
            }
        }
        catch { }
        return list.OrderByDescending(b => b.When).ToList();
    }

    /// <summary>Opens Registry Editor at a key (Windows asks for permission to start it).</summary>
    public static void OpenInRegedit(string fullKey)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                key.SetValue("LastKey", @"Computer\" + fullKey);
            Process.Start(new ProcessStartInfo("regedit.exe", "/m") { UseShellExecute = true });
        }
        catch { } // the permission prompt was declined
    }

    // ───────── .reg file writing ─────────
    static void ExportKey(StringBuilder sb, string fullKey, RegistryKey key)
    {
        sb.Append('[').Append(fullKey).Append("]\r\n");
        foreach (string name in key.GetValueNames())
            if (Reg.TryReadRaw(key, name, out uint type, out byte[] data))
                sb.Append(FormatValue(name, type, data)).Append("\r\n");
        sb.Append("\r\n");
        foreach (string child in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(child) ?? throw new IOException($"Cannot read {fullKey}\\{child}.");
            ExportKey(sb, fullKey + "\\" + child, sub);
        }
    }

    static string FormatValue(string name, uint type, byte[] data)
    {
        string label = name.Length == 0 ? "@" : "\"" + Escape(name) + "\"";
        if (type == 4 && data.Length == 4) return $"{label}=dword:{BitConverter.ToUInt32(data):x8}";
        if (type == 1 && data.Length % 2 == 0)
        {
            string s = Encoding.Unicode.GetString(data);
            if (s.EndsWith('\0')) s = s[..^1];
            if (s.IndexOfAny(['\0', '\r', '\n']) < 0) return $"{label}=\"{Escape(s)}\"";
        }

        var sb = new StringBuilder(label).Append('=').Append(type == 3 ? "hex:" : $"hex({type.ToString("x", CultureInfo.InvariantCulture)}):");
        int column = sb.Length;
        for (int i = 0; i < data.Length; i++)
        {
            sb.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
            if (i == data.Length - 1) break;
            sb.Append(',');
            column += 3;
            if (column > 74) { sb.Append("\\\r\n  "); column = 2; }
        }
        return sb.ToString();
    }

    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ───────── Developer: back up, remove and restore CoreWatch's own test key ─────────
    public static string SelfTest()
    {
        const string root = @"Software\CoreWatch\RegSelfTest";
        string folder = Path.Combine(Path.GetTempPath(), $"corewatch-regtest-{Environment.ProcessId}");
        var log = new List<string>();
        int failures = 0;
        void Check(string what, bool ok) { log.Add($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }

        static SortedDictionary<string, string> Snapshot(string path)
        {
            var all = new SortedDictionary<string, string>(StringComparer.Ordinal);
            void Walk(RegistryKey key, string at)
            {
                all[at + "\\"] = "<key>";
                foreach (string name in key.GetValueNames())
                    if (Reg.TryReadRaw(key, name, out uint type, out byte[] data)) all[$"{at}\\[{name}]"] = $"{type}:{Convert.ToHexString(data)}";
                foreach (string child in key.GetSubKeyNames()) { using var sub = key.OpenSubKey(child)!; Walk(sub, at + "\\" + child); }
            }
            using var start = Reg.Open(RegistryHive.CurrentUser, path);
            if (start != null) Walk(start, "");
            return all;
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, false);
            using (var tree = Reg.Create(RegistryHive.CurrentUser, root + @"\Tree"))
            {
                tree.SetValue("", "default value text");
                tree.SetValue("Plain", "hello");
                tree.SetValue("Quotes and slashes", "\"C:\\Program Files\\App\\app.exe\" /run \\\\server");
                tree.SetValue("Unicode ünï ✓", "Grüße, 日本語");
                tree.SetValue("Expand", @"%TEMP%\file.txt", RegistryValueKind.ExpandString);
                tree.SetValue("Dword", unchecked((int)0xDEADBEEF), RegistryValueKind.DWord);
                tree.SetValue("Qword", 0x1122334455667788L, RegistryValueKind.QWord);
                tree.SetValue("Binary", Enumerable.Range(0, 90).Select(i => (byte)(i * 3)).ToArray(), RegistryValueKind.Binary);
                tree.SetValue("Multi", new[] { "one", "two words", "three" }, RegistryValueKind.MultiString);
                tree.SetValue("Empty", "");
                tree.SetValue("None", new byte[] { 1, 2, 3 }, RegistryValueKind.None);
                using var deep = tree.CreateSubKey(@"Child [x]\Deep");
                deep.SetValue("Inside", "nested");
                deep.SetValue("", 7, RegistryValueKind.DWord);
            }
            using (var values = Reg.Create(RegistryHive.CurrentUser, root + @"\Values"))
            {
                values.SetValue(@"C:\Gone\app.exe.FriendlyAppName", "Gone App");
                values.SetValue("Keep", "stays");
            }

            var before = Snapshot(root);
            RegIssue Issue(string key, string? value) => new()
            {
                Category = RegistryScanner.NameCache, Title = "", Reason = "", Hive = RegistryHive.CurrentUser,
                KeyPath = root + "\\" + key, WholeKey = value == null, ValueName = value ?? "",
            };

            var result = Fix([Issue("Tree", null), Issue("Values", @"C:\Gone\app.exe.FriendlyAppName"), Issue("Missing", null), Issue("Values", "NoSuchValue")], folder);
            Check($"fix removed 2, failed 0 (got {result.Removed}/{result.Failed}, error {result.Error ?? "none"})", result is { Removed: 2, Failed: 0, Error: null });
            Check("backup file written", result.BackupFile != null && File.Exists(result.BackupFile));
            using (var gone = Reg.Open(RegistryHive.CurrentUser, root + @"\Tree")) Check("the whole key is gone", gone == null);
            using (var values = Reg.Open(RegistryHive.CurrentUser, root + @"\Values"))
                Check("only the one value was removed", values != null && values.GetValue(@"C:\Gone\app.exe.FriendlyAppName") == null && values.GetValue("Keep") as string == "stays");

            string? error = result.BackupFile == null ? "no backup" : Restore(result.BackupFile);
            Check("restore reports no error" + (error != null ? $" ({error})" : ""), error == null);
            var after = Snapshot(root);
            var diff = before.Keys.Union(after.Keys).Where(k => before.GetValueOrDefault(k) != after.GetValueOrDefault(k)).ToList();
            Check($"registry is byte-for-byte the same after restore ({before.Count} keys and values)" + (diff.Count > 0 ? " differs: " + string.Join(" | ", diff) : ""), diff.Count == 0);
        }
        catch (Exception ex) { Check("no exception: " + ex, false); }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(root, false); } catch { }
            try { Directory.Delete(folder, true); } catch { }
        }

        log.Insert(0, failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
        return string.Join(Environment.NewLine, log);
    }
}
