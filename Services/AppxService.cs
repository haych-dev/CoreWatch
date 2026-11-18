using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CoreWatch.Services;

/// <summary>An app that came with Windows or the PC and can be removed for the current user.</summary>
public sealed class StoreApp : INotifyPropertyChanged
{
    public required string PackageName { get; init; }
    public required string FullName { get; init; }
    public required string FamilyName { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string? Note { get; init; }
    public bool HasNote => Note != null;

    bool _selected, _busy;
    public bool Selected { get => _selected; set { _selected = value; Notify(); } }
    public bool Busy { get => _busy; set { _busy = value; Notify(); } }
    public bool CanSelect => !_busy;

    void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record RemovedApp(string PackageName, string Name, string FamilyName, DateTime Time)
{
    public string When => Time.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}

public static class AppxService
{
    // Only apps from this list are offered. A trailing * matches any ending.
    static readonly (string Match, string Name, string Description, string? Note)[] Known =
    [
        ("Microsoft.BingNews", "News", "Microsoft's news feed app", null),
        ("Microsoft.BingWeather", "Weather", "Weather forecasts", null),
        ("Microsoft.BingFinance", "Money", "Stock and finance news", null),
        ("Microsoft.BingSports", "Sports", "Sports news and scores", null),
        ("Microsoft.GetHelp", "Get Help", "Microsoft's support chat", null),
        ("Microsoft.Getstarted", "Tips", "Introductory tips for Windows", null),
        ("Microsoft.Microsoft3DViewer", "3D Viewer", "Viewer for 3D model files", null),
        ("Microsoft.Print3D", "Print 3D", "3D printing helper", null),
        ("Microsoft.MSPaint", "Paint 3D", "The 3D version of Paint, not classic Paint", null),
        ("Microsoft.MixedReality.Portal", "Mixed Reality Portal", "Setup for Windows Mixed Reality headsets", null),
        ("Microsoft.MicrosoftOfficeHub", "Microsoft 365 hub", "Launcher and adverts for Microsoft 365, not the Office apps themselves", null),
        ("Microsoft.Office.OneNote", "OneNote for Windows 10", "The older Store version of OneNote", null),
        ("Microsoft.OutlookForWindows", "Outlook (new)", "The web-based Outlook that replaces Mail and Calendar", null),
        ("microsoft.windowscommunicationsapps", "Mail and Calendar", "The older Mail and Calendar apps", null),
        ("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", "Card games with adverts", null),
        ("Microsoft.MicrosoftStickyNotes", "Sticky Notes", "Notes on the desktop", null),
        ("Microsoft.People", "People", "Contacts app", null),
        ("Microsoft.Todos", "Microsoft To Do", "Task lists", null),
        ("Microsoft.PowerAutomateDesktop", "Power Automate", "Automation tool for repetitive tasks", null),
        ("Microsoft.SkypeApp", "Skype", "Skype calls and chat", null),
        ("MicrosoftTeams", "Teams (personal)", "The consumer version of Teams that starts with Windows", null),
        ("MSTeams", "Microsoft Teams", "Teams for work or school", "Skip this if you use Teams"),
        ("Microsoft.Copilot", "Copilot", "Microsoft's AI assistant app", null),
        ("Microsoft.549981C3F5F10", "Cortana", "The retired voice assistant", null),
        ("Microsoft.Windows.DevHome", "Dev Home", "Dashboard for developers", null),
        ("Microsoft.WindowsFeedbackHub", "Feedback Hub", "Sends feedback to Microsoft", "Needed on Windows Insider PCs"),
        ("Microsoft.WindowsMaps", "Maps", "Microsoft's maps app", null),
        ("Microsoft.WindowsSoundRecorder", "Sound Recorder", "Voice recording app", null),
        ("Microsoft.WindowsAlarms", "Clock", "Alarms, timers and focus sessions", null),
        ("Microsoft.YourPhone", "Phone Link", "Shows your phone's messages and photos on the PC", null),
        ("Microsoft.ZuneVideo", "Films & TV", "Player and store for films", null),
        ("Microsoft.ZuneMusic", "Media Player", "Windows' music and video player", "Skip this unless you use another player"),
        ("Clipchamp.Clipchamp", "Clipchamp", "Microsoft's video editor", null),
        ("MicrosoftCorporationII.QuickAssist", "Quick Assist", "Lets someone help you by remote control", null),
        ("MicrosoftCorporationII.MicrosoftFamily", "Family", "Microsoft family safety", null),
        ("Microsoft.Messaging", "Messaging", "Old text messaging app", null),
        ("Microsoft.OneConnect", "Mobile Plans", "Buys mobile data plans for PCs with a SIM", null),
        ("Microsoft.Wallet", "Wallet", "Old Microsoft payments app", null),
        ("Microsoft.Whiteboard", "Whiteboard", "Shared drawing board", null),
        ("Microsoft.GamingApp", "Xbox", "The Xbox app and Game Pass store", "PC Game Pass needs it"),
        ("Microsoft.XboxApp", "Xbox Console Companion", "The older Xbox app", null),
        ("Microsoft.XboxGamingOverlay", "Game Bar", "The Win+G overlay for recording and widgets", "Some games open it for overlays"),
        ("Microsoft.XboxGameOverlay", "Xbox Game Bar plugin", "Helper for the Game Bar", null),
        ("Microsoft.XboxSpeechToTextOverlay", "Xbox speech to text", "Chat transcription in games", null),
        ("Microsoft.Xbox.TCUI", "Xbox Live in-game experience", "Friend and invite windows inside games", "Games that use Xbox Live need it"),
        ("Microsoft.XboxIdentityProvider", "Xbox Identity Provider", "Signs games in to Xbox Live", "Games that sign in to Xbox need it"),
        ("SpotifyAB.SpotifyMusic", "Spotify", "Music streaming", null),
        ("Disney.*", "Disney+", "Video streaming", null),
        ("4DF9E0F8.Netflix", "Netflix", "Video streaming", null),
        ("AmazonVideo.PrimeVideo", "Prime Video", "Video streaming", null),
        ("BytedancePte.Ltd.TikTok", "TikTok", "Short videos", null),
        ("Facebook.*", "Facebook, Instagram or Messenger", "Meta's apps", null),
        ("7EE7776C.LinkedInforWindows", "LinkedIn", "LinkedIn app", null),
        ("king.com.*", "A King game", "Candy Crush and similar games", null),
    ];

    static string RemovedFile => Path.Combine(Settings.Folder, "removed-apps.json");
    static string PowerShell => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    static (int Code, string Output, string Errors) Run(string command)
    {
        var info = new ProcessStartInfo(PowerShell, $"-NoProfile -NonInteractive -Command \"{command}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(info) ?? throw new InvalidOperationException("Could not start PowerShell.");
        var errors = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } throw new TimeoutException("PowerShell did not finish."); }
        return (p.ExitCode, output, errors.Result);
    }

    static (string Name, string Description, string? Note)? Match(string package)
    {
        foreach (var (match, name, description, note) in Known)
        {
            bool hit = match.EndsWith('*')
                ? package.StartsWith(match[..^1], StringComparison.OrdinalIgnoreCase)
                : package.Equals(match, StringComparison.OrdinalIgnoreCase);
            if (hit) return (name, description, note);
        }
        return null;
    }

    /// <summary>Turns Get-AppxPackage's JSON into the apps this page offers. Frameworks and apps Windows marks non-removable are dropped.</summary>
    public static List<StoreApp> Parse(string json)
    {
        var apps = new List<StoreApp>();
        json = json.Trim();
        if (json.Length == 0) return apps;
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        foreach (var item in items)
        {
            string Text(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool Flag(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            string package = Text("Name"), full = Text("PackageFullName");
            if (package.Length == 0 || full.Length == 0 || Flag("NonRemovable") || Flag("IsFramework")) continue;
            if (Match(package) is not { } known) continue;
            if (apps.Any(a => a.PackageName.Equals(package, StringComparison.OrdinalIgnoreCase))) continue;
            apps.Add(new StoreApp
            {
                PackageName = package, FullName = full, FamilyName = Text("PackageFamilyName"),
                Name = known.Name == "A King game" ? package.Split('.').Last() : known.Name,
                Description = known.Description, Note = known.Note,
            });
        }
        return apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The preinstalled apps on this PC for the current user (read-only).</summary>
    public static List<StoreApp> List()
    {
        var (code, output, errors) = Run("[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-AppxPackage | Select-Object Name,PackageFullName,PackageFamilyName,NonRemovable,IsFramework | ConvertTo-Json -Compress");
        if (code != 0 && output.Trim().Length == 0) throw new InvalidOperationException(errors.Trim().Split('\n').FirstOrDefault() ?? "PowerShell could not list the apps.");
        return Parse(output);
    }

    /// <summary>Removes the app for the current user and notes it, so the page can offer to get it back. Returns an error message, or null.</summary>
    public static string? Remove(StoreApp app)
    {
        try
        {
            var (code, _, errors) = Run($"Remove-AppxPackage -Package '{app.FullName.Replace("'", "''")}' -ErrorAction Stop");
            if (code != 0) return errors.Trim().Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? $"PowerShell returned {code}.";
            var removed = Removed();
            removed.RemoveAll(r => r.PackageName.Equals(app.PackageName, StringComparison.OrdinalIgnoreCase));
            removed.Insert(0, new RemovedApp(app.PackageName, app.Name, app.FamilyName, DateTime.Now));
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(RemovedFile, JsonSerializer.Serialize(removed, new JsonSerializerOptions { WriteIndented = true }));
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static List<RemovedApp> Removed()
    {
        try
        {
            if (File.Exists(RemovedFile)) return JsonSerializer.Deserialize<List<RemovedApp>>(File.ReadAllText(RemovedFile)) ?? [];
        }
        catch { }
        return [];
    }

    /// <summary>The Microsoft Store page the app can be installed from again.</summary>
    public static string StoreLink(string familyName) => "ms-windows-store://pdp/?PFN=" + Uri.EscapeDataString(familyName);
}
