using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace CoreWatch.Services;

public sealed record CleanTarget(string Path, string Pattern = "*", bool Recursive = true);

/// <summary>One cleanable category. Paths may use environment variables and a "*" wildcard for a folder segment.</summary>
public sealed class CleanRule : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string Glyph { get; init; } = "\uE74D";
    public bool DefaultOn { get; init; } = true;
    public bool NeedsAdmin { get; init; }
    public bool IsRecycleBin { get; init; }
    public bool AgeFiltered { get; init; }
    public CleanTarget[] Targets { get; init; } = [];

    bool _selected;
    long _bytes = -1;
    long _files;
    string _status = "";

    public bool Selected { get => _selected; set { _selected = value; Raise(); } }
    public long Bytes { get => _bytes; set { _bytes = value; Raise(); Raise(nameof(SizeText)); } }
    public long Files { get => _files; set { _files = value; Raise(); Raise(nameof(SizeText)); } }
    public string Status { get => _status; set { _status = value; Raise(); } }

    public string SizeText => _bytes < 0 || (_bytes == 0 && Locked) ? "—" : _bytes == 0 ? "Clean" : $"{Fmt.Bytes(_bytes)}  ·  {Fmt.Count(_files)} {(IsRecycleBin ? "items" : "files")}";
    public bool Locked => NeedsAdmin && !Sys.IsAdmin;
    public bool Available => !Locked;

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class CleanerService
{
    public static List<CleanRule> BuildRules()
    {
        string[] chromium = ["Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage", @"Service Worker\ScriptCache"];
        CleanTarget[] Chromium(string userData) => chromium.Select(c => new CleanTarget($@"{userData}\*\{c}")).ToArray();
        CleanTarget[] Electron(string root) => [new($@"{root}\Cache"), new($@"{root}\Code Cache"), new($@"{root}\GPUCache")];

        return
        [
            // ── Windows ──
            new CleanRule { Id = "user-temp", Group = "Windows", Name = "Temporary files", Glyph = "\uE9F5", AgeFiltered = true,
                Description = "Leftover files in your user Temp folder", Targets = [new("%TEMP%")] },
            new CleanRule { Id = "win-temp", Group = "Windows", Name = "Windows temporary files", Glyph = "\uE9F5", AgeFiltered = true, NeedsAdmin = true,
                Description = @"System-wide temp files in C:\Windows\Temp", Targets = [new(@"%WINDIR%\Temp")] },
            new CleanRule { Id = "recycle-bin", Group = "Windows", Name = "Recycle Bin", Glyph = "\uE74D", IsRecycleBin = true,
                Description = "Deleted files still held on every drive" },
            new CleanRule { Id = "wu-cache", Group = "Windows", Name = "Windows Update downloads", Glyph = "\uE895", NeedsAdmin = true,
                Description = "Installers for updates that are already applied", Targets = [new(@"%WINDIR%\SoftwareDistribution\Download")] },
            new CleanRule { Id = "delivery-opt", Group = "Windows", Name = "Delivery Optimization cache", Glyph = "\uE896", NeedsAdmin = true,
                Description = "Update pieces cached for sharing with other PCs",
                Targets = [new(@"%WINDIR%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache")] },
            new CleanRule { Id = "thumbs", Group = "Windows", Name = "Thumbnail and icon cache", Glyph = "\uE8B9",
                Description = "Explorer rebuilds these on demand",
                Targets = [new(@"%LOCALAPPDATA%\Microsoft\Windows\Explorer", "thumbcache_*.db", false), new(@"%LOCALAPPDATA%\Microsoft\Windows\Explorer", "iconcache_*.db", false)] },
            new CleanRule { Id = "shader-cache", Group = "Windows", Name = "Graphics shader cache", Glyph = "\uE7F4",
                Description = "DirectX, NVIDIA and AMD compiled shaders (rebuilt as games run)",
                Targets = [new(@"%LOCALAPPDATA%\D3DSCache"), new(@"%LOCALAPPDATA%\NVIDIA\DXCache"), new(@"%LOCALAPPDATA%\NVIDIA\GLCache"),
                           new(@"%LOCALAPPDATA%\AMD\DxCache"), new(@"%LOCALAPPDATA%\AMD\DxcCache"), new(@"%LOCALAPPDATA%\AMD\GLCache")] },
            new CleanRule { Id = "wer", Group = "Windows", Name = "Error reports", Glyph = "\uE9F9",
                Description = "Queued and archived Windows Error Reporting files",
                Targets = [new(@"%PROGRAMDATA%\Microsoft\Windows\WER\ReportArchive"), new(@"%PROGRAMDATA%\Microsoft\Windows\WER\ReportQueue"),
                           new(@"%LOCALAPPDATA%\Microsoft\Windows\WER\ReportArchive"), new(@"%LOCALAPPDATA%\Microsoft\Windows\WER\ReportQueue")] },
            new CleanRule { Id = "dumps", Group = "Windows", Name = "Crash dumps", Glyph = "\uE7BA",
                Description = "Memory dumps from app and system crashes",
                Targets = [new(@"%LOCALAPPDATA%\CrashDumps"), new(@"%WINDIR%\Minidump"), new("%WINDIR%", "MEMORY.DMP", false)] },
            new CleanRule { Id = "logs", Group = "Windows", Name = "Servicing logs", Glyph = "\uE8A5", NeedsAdmin = true,
                Description = "Component servicing (CBS) and DISM logs",
                Targets = [new(@"%WINDIR%\Logs\CBS"), new(@"%WINDIR%\Logs\DISM")] },

            // ── Browsers ──
            new CleanRule { Id = "edge", Group = "Browsers", Name = "Microsoft Edge cache", Glyph = "\uE774",
                Description = "Cached pages, scripts and GPU data. History, logins and cookies are kept.",
                Targets = Chromium(@"%LOCALAPPDATA%\Microsoft\Edge\User Data") },
            new CleanRule { Id = "chrome", Group = "Browsers", Name = "Google Chrome cache", Glyph = "\uE774",
                Description = "Cached pages, scripts and GPU data. History, logins and cookies are kept.",
                Targets = Chromium(@"%LOCALAPPDATA%\Google\Chrome\User Data") },
            new CleanRule { Id = "brave", Group = "Browsers", Name = "Brave cache", Glyph = "\uE774",
                Description = "Cached pages, scripts and GPU data. History, logins and cookies are kept.",
                Targets = Chromium(@"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data") },
            new CleanRule { Id = "firefox", Group = "Browsers", Name = "Firefox cache", Glyph = "\uE774",
                Description = "Cached pages and startup cache. History, logins and cookies are kept.",
                Targets = [new(@"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\cache2"), new(@"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\startupCache")] },

            // ── Apps and developer tools (opt-in) ──
            new CleanRule { Id = "discord", Group = "Apps", Name = "Discord cache", Glyph = "\uE8BD", DefaultOn = false,
                Description = "Cached media and scripts", Targets = Electron(@"%APPDATA%\discord") },
            new CleanRule { Id = "slack", Group = "Apps", Name = "Slack cache", Glyph = "\uE8BD", DefaultOn = false,
                Description = "Cached media and scripts", Targets = Electron(@"%APPDATA%\Slack") },
            new CleanRule { Id = "vscode", Group = "Apps", Name = "VS Code cache", Glyph = "\uE943", DefaultOn = false,
                Description = "Editor caches and cached extension VSIX files",
                Targets = [.. Electron(@"%APPDATA%\Code"), new(@"%APPDATA%\Code\CachedData"), new(@"%APPDATA%\Code\CachedExtensionVSIXs")] },
            new CleanRule { Id = "steam", Group = "Apps", Name = "Steam web cache", Glyph = "\uE7FC", DefaultOn = false,
                Description = "Store and community page cache", Targets = [new(@"%LOCALAPPDATA%\Steam\htmlcache")] },
            new CleanRule { Id = "npm", Group = "Apps", Name = "npm cache", Glyph = "\uE943", DefaultOn = false,
                Description = "Downloaded package tarballs (re-downloaded when needed)", Targets = [new(@"%LOCALAPPDATA%\npm-cache\_cacache")] },
            new CleanRule { Id = "pip", Group = "Apps", Name = "pip cache", Glyph = "\uE943", DefaultOn = false,
                Description = "Downloaded Python wheels (re-downloaded when needed)", Targets = [new(@"%LOCALAPPDATA%\pip\cache")] },
            new CleanRule { Id = "nuget", Group = "Apps", Name = "NuGet HTTP cache", Glyph = "\uE943", DefaultOn = false,
                Description = "Feed metadata cache. Installed packages are not touched.",
                Targets = [new(@"%LOCALAPPDATA%\NuGet\v3-cache"), new(@"%LOCALAPPDATA%\NuGet\plugins-cache")] },
        ];
    }

    static readonly EnumerationOptions Flat = new() { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
    static readonly EnumerationOptions Deep = new() { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };

    /// <summary>Expands environment variables and "*" folder segments into existing directories.</summary>
    static IEnumerable<string> Resolve(string template)
    {
        string expanded = Environment.ExpandEnvironmentVariables(template);
        if (expanded.Contains('%')) yield break; // unknown variable: never guess a path
        var parts = expanded.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var current = new List<string> { parts[0] + "\\" };
        for (int i = 1; i < parts.Length && current.Count > 0; i++)
        {
            var next = new List<string>();
            foreach (var dir in current)
            {
                if (parts[i].Contains('*'))
                {
                    try { next.AddRange(Directory.EnumerateDirectories(dir, parts[i], Flat)); } catch { }
                }
                else
                {
                    string p = Path.Combine(dir, parts[i]);
                    if (Directory.Exists(p)) next.Add(p);
                }
            }
            current = next;
        }
        foreach (var dir in current)
            if (dir.Length > 12) yield return dir; // guard against anything close to a drive root
    }

    /// <summary>Temp files and folders newer than this are left alone (MaxValue = no age limit).</summary>
    static DateTime Cutoff(CleanRule rule, bool keepRecent) =>
        rule.AgeFiltered && (keepRecent || Settings.Current.SkipRecentTempFiles) ? DateTime.UtcNow.AddHours(-24) : DateTime.MaxValue;

    static IEnumerable<FileInfo> Files(CleanRule rule, bool keepRecent = false)
    {
        var cutoff = Cutoff(rule, keepRecent);
        foreach (var target in rule.Targets)
        {
            foreach (var dir in Resolve(target.Path))
            {
                IEnumerable<FileInfo> files;
                try { files = new DirectoryInfo(dir).EnumerateFiles(target.Pattern, target.Recursive ? Deep : Flat); }
                catch { continue; }
                using var e = files.GetEnumerator();
                while (true)
                {
                    FileInfo f;
                    try { if (!e.MoveNext()) break; f = e.Current; }
                    catch { break; }
                    // Age is the later of "created" and "modified": a file an installer unpacked a minute ago
                    // keeps its original modified date, but its created date shows it only just arrived.
                    DateTime stamp;
                    try { stamp = f.LastWriteTimeUtc > f.CreationTimeUtc ? f.LastWriteTimeUtc : f.CreationTimeUtc; } catch { continue; }
                    if (stamp <= cutoff) yield return f;
                }
            }
        }
    }

    /// <param name="keepRecent">Always leave temp files from the last 24 hours alone, whatever the setting says.</param>
    public static (long Bytes, long Files) Scan(CleanRule rule, CancellationToken ct, bool keepRecent = false)
    {
        if (rule.IsRecycleBin)
        {
            var info = new Native.SHQUERYRBINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.SHQUERYRBINFO>() };
            return Native.SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
        }
        long bytes = 0, count = 0;
        foreach (var f in Files(rule, keepRecent))
        {
            if (ct.IsCancellationRequested) break;
            try { bytes += f.Length; count++; } catch { }
        }
        return (bytes, count);
    }

    /// <summary>Deletes the rule's files. Files in use are skipped. Returns bytes actually freed.</summary>
    public static (long Freed, long Deleted, long Skipped) Clean(CleanRule rule, CancellationToken ct, bool keepRecent = false)
    {
        if (rule.IsRecycleBin)
        {
            var before = Scan(rule, ct);
            if (before.Files == 0) return (0, 0, 0);
            int hr = Native.SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); // no confirmation, progress UI or sound
            return hr == 0 ? (before.Bytes, before.Files, 0) : (0, 0, before.Files);
        }

        long freed = 0, deleted = 0, skipped = 0;
        foreach (var f in Files(rule, keepRecent))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                long len = f.Length;
                if (f.IsReadOnly) f.IsReadOnly = false;
                f.Delete();
                freed += len;
                deleted++;
            }
            catch { skipped++; }
        }

        // Remove sub-folders that are now empty (deepest first), never the target folder itself.
        // A folder that was only just created may be about to be filled by a running program, so it follows the same age rule.
        var cutoff = Cutoff(rule, keepRecent);
        foreach (var target in rule.Targets.Where(t => t.Recursive))
        {
            foreach (var root in Resolve(target.Path))
            {
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(root, "*", Deep).OrderByDescending(d => d.Length))
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            var info = new DirectoryInfo(dir);
                            if (info.CreationTimeUtc > cutoff || info.LastWriteTimeUtc > cutoff) continue;
                            if (!info.EnumerateFileSystemInfos().Any()) info.Delete();
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        return (freed, deleted, skipped);
    }
}
