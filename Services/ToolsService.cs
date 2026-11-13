using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Text.Json;

namespace CoreWatch.Services;

/// <summary>Runs a command-line tool hidden and hands back its output line by line while it runs.</summary>
public static class CommandRunner
{
    public static string SystemTool(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    public static async Task<int> Run(string file, string args, Action<string> onLine, CancellationToken ct = default, Encoding? encoding = null)
    {
        var info = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding ?? Encoding.UTF8,
            StandardErrorEncoding = encoding ?? Encoding.UTF8,
        };
        using var p = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(file)}.");
        using var stop = ct.Register(() => { try { p.Kill(true); } catch { } });
        await Task.WhenAll(Pump(p.StandardOutput, onLine), Pump(p.StandardError, onLine));
        await p.WaitForExitAsync(CancellationToken.None);
        return p.ExitCode;
    }

    // Tools redraw progress with carriage returns, so both \r and \n end a line.
    static async Task Pump(StreamReader reader, Action<string> onLine)
    {
        var buffer = new char[1024];
        var line = new StringBuilder();
        void Flush()
        {
            string text = line.ToString().TrimEnd();
            line.Clear();
            if (text.Trim().Length > 0) onLine(text);
        }
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] is '\r' or '\n') Flush();
                else if (buffer[i] != '\0') line.Append(buffer[i]);
            }
        Flush();
    }
}

// ───────────────────────── One-click refresh ─────────────────────────

public sealed record RefreshStep(string Name, string Result, bool Done);
public sealed record RefreshReport(List<RefreshStep> Steps, string Headline);

/// <summary>
/// The safe, repeatable clean-up: nothing here removes anything Windows or an app will not simply rebuild,
/// and temp files from the last 24 hours are always left alone.
/// </summary>
public static class RefreshService
{
    static readonly string[] TempRules = ["user-temp", "win-temp", "wer"];

    /// <summary>Physical memory available right now, asked from Windows directly (the monitor may not have a sample yet).</summary>
    public static ulong AvailableMemory()
    {
        var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        return Native.GlobalMemoryStatusEx(ref mem) ? mem.ullAvailPhys : 0;
    }

    /// <param name="preview">Developer: measure and report without changing anything.</param>
    public static async Task<RefreshReport> Run(Action<string> stage, bool preview = false)
    {
        var steps = new List<RefreshStep>();
        var monitor = SystemMonitor.Instance;

        // 1. Memory
        stage("Releasing memory held by idle apps…");
        ulong released = 0;
        if (preview) steps.Add(new("Memory", "Preview only: idle apps would be asked to hand back memory they are not using", false));
        else
        {
            ulong before = AvailableMemory();
            int trimmed = await Task.Run(MemoryOptimizer.TrimWorkingSets);
            await Task.Delay(1500);
            monitor.Poke();
            ulong after = AvailableMemory();
            released = before > 0 && after > before ? after - before : 0;
            steps.Add(new("Memory", released > 0 ? $"{Fmt.Bytes(released)} released from {trimmed} idle processes" : $"{trimmed} processes trimmed, nothing extra to release", true));
        }

        // 2. Standby cache
        stage("Clearing the standby cache…");
        if (!Sys.IsAdmin) steps.Add(new("Standby cache", "Skipped: needs administrator rights", false));
        else if (preview) steps.Add(new("Standby cache", "Preview only: cached file data would be dropped from memory", false));
        else steps.Add(await Task.Run(MemoryOptimizer.PurgeStandbyList) ? new("Standby cache", "Cleared", true) : new("Standby cache", "Windows refused the request", false));

        // 3. DNS
        stage("Flushing the DNS cache…");
        if (preview) steps.Add(new("DNS cache", "Preview only: saved website address lookups would be forgotten", false));
        else
        {
            bool ok = await Task.Run(() => { try { return Tool.Run("ipconfig.exe", "/flushdns") == 0; } catch { return false; } });
            // Recent Windows builds only let administrators flush the DNS cache.
            steps.Add(new("DNS cache", ok ? "Flushed" : Sys.IsAdmin ? "Could not be flushed" : "Skipped: needs administrator rights on this version of Windows", ok));
        }

        // 4. Temp files
        stage("Removing temporary files…");
        var rules = CleanerService.BuildRules().Where(r => TempRules.Contains(r.Id)).ToList();
        long freed = 0, files = 0, skipped = 0;
        await Task.Run(() =>
        {
            foreach (var rule in rules.Where(r => !r.Locked))
            {
                if (preview) { var found = CleanerService.Scan(rule, CancellationToken.None, keepRecent: true); freed += found.Bytes; files += found.Files; }
                else { var done = CleanerService.Clean(rule, CancellationToken.None, keepRecent: true); freed += done.Freed; files += done.Deleted; skipped += done.Skipped; }
            }
        });
        string count = $"{Fmt.Count(files)} {(files == 1 ? "file" : "files")}";
        string temp = preview ? $"Preview only: {Fmt.Bytes(freed)} in {count} would be removed"
            : files == 0 ? "Already clean"
            : $"{Fmt.Bytes(freed)} removed ({count})";
        if (skipped > 0) temp += $", {Fmt.Count(skipped)} in use were left";
        if (rules.Any(r => r.Locked)) temp += ". Windows' own temp folder needs administrator rights";
        steps.Add(new("Temporary files", temp, !preview));

        string headline = preview ? "Preview: nothing was changed"
            : released > 0 && freed > 0 ? $"{Fmt.Bytes(released)} of memory released and {Fmt.Bytes(freed)} of temporary files removed"
            : released > 0 ? $"{Fmt.Bytes(released)} of memory released"
            : freed > 0 ? $"{Fmt.Bytes(freed)} of temporary files removed"
            : "Everything was already tidy";
        return new RefreshReport(steps, headline);
    }
}

// ───────────────────────── App installer (winget) ─────────────────────────

public sealed class CatalogApp : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required string Description { get; init; }

    bool _selected, _installed, _failed, _busy;
    string _status = "";

    public bool Selected { get => _selected; set { _selected = value; Notify(); } }
    public bool Installed { get => _installed; set { _installed = value; if (value) { _selected = false; _failed = false; _status = "Installed"; } Notify(); } }
    public bool Failed { get => _failed; set { _failed = value; Notify(); } }
    public bool Busy { get => _busy; set { _busy = value; Notify(); } }
    public string Status { get => _status; set { _status = value; Notify(); } }
    public bool CanSelect => !_installed && !_busy;

    void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class WingetService
{
    /// <summary>winget.exe, or null when App Installer is not on this PC.</summary>
    public static string? Exe { get; } = Find();

    static string? Find()
    {
        try
        {
            string alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
            if (File.Exists(alias)) return alias;
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                if (File.Exists(Path.Combine(dir.Trim(), "winget.exe"))) return Path.Combine(dir.Trim(), "winget.exe");
        }
        catch { }
        return null;
    }

    // Everything goes through the community "winget" source, so the Microsoft Store source and its terms are never involved.
    const string Common = "--source winget --disable-interactivity";

    public static string InstallArgs(string id) => $"install --id \"{id}\" --exact --silent --accept-package-agreements {Common}";
    public const string UpgradeAllArgs = "upgrade --all --silent --accept-package-agreements " + Common;
    public static string ShowArgs(string id) => $"show --id \"{id}\" --exact {Common}";

    /// <summary>Drops winget's spinner and progress-bar redraws, which are noise in a log.</summary>
    public static bool IsNoise(string line)
    {
        string t = line.Trim();
        return t.Length == 0 || t.All(c => c is '-' or '\\' or '|' or '/' or ' ') || t.Contains('█') || t.Contains('▒');
    }

    public static Task<int> Run(string args, Action<string> onLine, CancellationToken ct = default) =>
        CommandRunner.Run(Exe ?? throw new InvalidOperationException("winget is not available."), args, line => { if (!IsNoise(line)) onLine(line); }, ct);

    /// <summary>Ids of installed apps that winget recognises (read-only).</summary>
    public static async Task<HashSet<string>> InstalledIds(CancellationToken ct = default)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Exe == null) return ids;
        string file = Path.Combine(Path.GetTempPath(), $"corewatch-winget-{Environment.ProcessId}.json");
        try
        {
            await CommandRunner.Run(Exe, $"export -o \"{file}\" {Common}", _ => { }, ct);
            if (!File.Exists(file)) return ids;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("Sources", out var sources))
                foreach (var source in sources.EnumerateArray())
                    if (source.TryGetProperty("Packages", out var packages))
                        foreach (var package in packages.EnumerateArray())
                            if (package.TryGetProperty("PackageIdentifier", out var id) && id.GetString() is { } s) ids.Add(s);
        }
        catch { }
        finally { try { File.Delete(file); } catch { } }
        return ids;
    }

    public static List<CatalogApp> BuildCatalog()
    {
        var list = new List<CatalogApp>();
        void Add(string category, string id, string name, string description) =>
            list.Add(new CatalogApp { Category = category, Id = id, Name = name, Description = description });

        const string web = "Browsers";
        Add(web, "Mozilla.Firefox", "Firefox", "Independent browser from Mozilla");
        Add(web, "Google.Chrome", "Google Chrome", "Google's browser");
        Add(web, "Brave.Brave", "Brave", "Chromium with built-in ad blocking");
        Add(web, "Vivaldi.Vivaldi", "Vivaldi", "Highly customisable Chromium browser");
        Add(web, "LibreWolf.LibreWolf", "LibreWolf", "Privacy-hardened Firefox build");

        const string chat = "Communication";
        Add(chat, "Discord.Discord", "Discord", "Voice, video and text chat");
        Add(chat, "SlackTechnologies.Slack", "Slack", "Team messaging");
        Add(chat, "Zoom.Zoom", "Zoom", "Video meetings");
        Add(chat, "Telegram.TelegramDesktop", "Telegram", "Cloud messaging");
        Add(chat, "OpenWhisperSystems.Signal", "Signal", "End-to-end encrypted messaging");
        Add(chat, "Mozilla.Thunderbird", "Thunderbird", "Email and calendar");

        const string dev = "Development";
        Add(dev, "Microsoft.VisualStudioCode", "Visual Studio Code", "Code editor");
        Add(dev, "Git.Git", "Git", "Version control");
        Add(dev, "GitHub.GitHubDesktop", "GitHub Desktop", "Git with a visual interface");
        Add(dev, "OpenJS.NodeJS.LTS", "Node.js LTS", "JavaScript runtime");
        Add(dev, "Python.Python.3.13", "Python 3.13", "Python language and pip");
        Add(dev, "Microsoft.PowerShell", "PowerShell 7", "Modern PowerShell");
        Add(dev, "Microsoft.WindowsTerminal", "Windows Terminal", "Tabbed terminal");
        Add(dev, "Notepad++.Notepad++", "Notepad++", "Fast text editor");
        Add(dev, "Docker.DockerDesktop", "Docker Desktop", "Containers");
        Add(dev, "Postman.Postman", "Postman", "API testing");
        Add(dev, "WinSCP.WinSCP", "WinSCP", "SFTP and FTP client");
        Add(dev, "PuTTY.PuTTY", "PuTTY", "SSH client");

        const string media = "Media and creative";
        Add(media, "VideoLAN.VLC", "VLC", "Plays almost any video or audio file");
        Add(media, "Spotify.Spotify", "Spotify", "Music streaming");
        Add(media, "OBSProject.OBSStudio", "OBS Studio", "Screen recording and streaming");
        Add(media, "Audacity.Audacity", "Audacity", "Audio editor");
        Add(media, "HandBrake.HandBrake", "HandBrake", "Video converter");
        Add(media, "GIMP.GIMP", "GIMP", "Image editor");
        Add(media, "Inkscape.Inkscape", "Inkscape", "Vector graphics");
        Add(media, "KDE.Krita", "Krita", "Digital painting");
        Add(media, "BlenderFoundation.Blender", "Blender", "3D modelling and animation");
        Add(media, "dotPDN.PaintDotNet", "Paint.NET", "Simple photo editor");
        Add(media, "IrfanSkiljan.IrfanView", "IrfanView", "Fast image viewer");

        const string office = "Documents";
        Add(office, "TheDocumentFoundation.LibreOffice", "LibreOffice", "Office suite");
        Add(office, "SumatraPDF.SumatraPDF", "Sumatra PDF", "Lightweight PDF reader");
        Add(office, "Obsidian.Obsidian", "Obsidian", "Markdown notes");
        Add(office, "Notion.Notion", "Notion", "Notes and wikis");

        const string util = "Utilities";
        Add(util, "7zip.7zip", "7-Zip", "File archiver");
        Add(util, "voidtools.Everything", "Everything", "Instant file search");
        Add(util, "Microsoft.PowerToys", "PowerToys", "Microsoft's power-user tools");
        Add(util, "ShareX.ShareX", "ShareX", "Screenshots and screen capture");
        Add(util, "Bitwarden.Bitwarden", "Bitwarden", "Password manager");
        Add(util, "KeePassXCTeam.KeePassXC", "KeePassXC", "Offline password manager");
        Add(util, "qBittorrent.qBittorrent", "qBittorrent", "Torrent client");
        Add(util, "Rufus.Rufus", "Rufus", "Bootable USB creator");
        Add(util, "AutoHotkey.AutoHotkey", "AutoHotkey", "Keyboard automation");

        const string hw = "Hardware and diagnostics";
        Add(hw, "REALiX.HWiNFO", "HWiNFO", "Detailed sensors, including CPU temperature");
        Add(hw, "CPUID.CPU-Z", "CPU-Z", "Processor and memory details");
        Add(hw, "CPUID.HWMonitor", "HWMonitor", "Voltages, temperatures and fans");
        Add(hw, "CrystalDewWorld.CrystalDiskInfo", "CrystalDiskInfo", "Drive health (SMART)");
        Add(hw, "CrystalDewWorld.CrystalDiskMark", "CrystalDiskMark", "Drive speed benchmark");
        Add(hw, "WinDirStat.WinDirStat", "WinDirStat", "Disk usage map");

        const string games = "Gaming";
        Add(games, "Valve.Steam", "Steam", "Game store and library");
        Add(games, "EpicGames.EpicGamesLauncher", "Epic Games Launcher", "Game store and library");
        Add(games, "GOG.Galaxy", "GOG Galaxy", "Game store and library");
        Add(games, "Playnite.Playnite", "Playnite", "One library for all your launchers");
        return list;
    }

    /// <summary>Developer: asks winget whether every catalogue id exists (read-only).</summary>
    public static async Task<string> CheckCatalog()
    {
        if (Exe == null) return "winget is not available";
        var log = new List<string>();
        int bad = 0;
        foreach (var app in BuildCatalog())
        {
            int code = await CommandRunner.Run(Exe, ShowArgs(app.Id), _ => { });
            if (code != 0) bad++;
            log.Add($"{(code == 0 ? "OK  " : "BAD ")} {app.Id}" + (code == 0 ? "" : $"  (0x{code:X8})"));
        }
        var installed = await InstalledIds();
        log.Insert(0, $"{(bad == 0 ? "ALL IDS VALID" : $"{bad} INVALID")}  ·  {installed.Count} installed apps recognised, {BuildCatalog().Count(a => installed.Contains(a.Id))} of them in the catalogue");
        return string.Join(Environment.NewLine, log);
    }
}

// ───────────────────────── Repairs ─────────────────────────

public static class ToolboxService
{
    /// <summary>Asks Windows for a system restore point. Returns an error message, or null when the request was accepted.</summary>
    public static string? CreateRestorePoint(string description)
    {
        try
        {
            using var restore = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var args = restore.GetMethodParameters("CreateRestorePoint");
            args["Description"] = description;
            args["RestorePointType"] = 12; // MODIFY_SETTINGS
            args["EventType"] = 100;       // BEGIN_SYSTEM_CHANGE
            using var result = restore.InvokeMethod("CreateRestorePoint", args, null);
            uint code = Convert.ToUInt32(result["ReturnValue"]);
            return code switch
            {
                0 => null,
                1058 => "System Protection is switched off for the system drive. Switch it on under System Properties, then try again.",
                5 => "Windows refused the request. Restart CoreWatch as administrator and try again.",
                _ => $"Windows returned error {code}.",
            };
        }
        catch (UnauthorizedAccessException) { return "Windows refused the request. Restart CoreWatch as administrator and try again."; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>DISM component-store repair followed by the system file checker.</summary>
    public static async Task<bool> RepairSystemFiles(Action<string> log, CancellationToken ct)
    {
        log("▶ DISM /Online /Cleanup-Image /RestoreHealth");
        int dism = await CommandRunner.Run(CommandRunner.SystemTool("Dism.exe"), "/Online /Cleanup-Image /RestoreHealth", log, ct);
        if (ct.IsCancellationRequested) return false;
        log($"DISM finished with code {dism}.");
        log("▶ sfc /scannow");
        int sfc = await CommandRunner.Run(CommandRunner.SystemTool("sfc.exe"), "/scannow", log, ct, Encoding.Unicode); // sfc writes UTF-16
        if (ct.IsCancellationRequested) return false;
        log($"System File Checker finished with code {sfc}.");
        return dism == 0 && sfc == 0;
    }

    public static async Task<bool> ResetNetwork(Action<string> log, CancellationToken ct)
    {
        bool ok = true;
        foreach (var (exe, args) in new[] { ("netsh.exe", "winsock reset"), ("netsh.exe", "int ip reset"), ("netsh.exe", "winhttp reset proxy"), ("ipconfig.exe", "/flushdns") })
        {
            if (ct.IsCancellationRequested) return false;
            log($"▶ {Path.GetFileNameWithoutExtension(exe)} {args}");
            // "int ip reset" reports failure for one protected key on most PCs even though the reset itself works.
            if (await CommandRunner.Run(CommandRunner.SystemTool(exe), args, log, ct) != 0 && args != "int ip reset") ok = false;
        }
        return ok;
    }

    public static void RestartExplorer()
    {
        Tool.Run("taskkill.exe", "/f /im explorer.exe");
        Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
    }

    /// <summary>Opens a Windows panel or management console.</summary>
    public static void Open(string file, string args = "")
    {
        try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }); } catch { }
    }
}
