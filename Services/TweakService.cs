using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace CoreWatch.Services;

/// <summary>Remembers what each setting was before CoreWatch switched it off, so it can be put back exactly.</summary>
public sealed class TweakBackup
{
    readonly string _file;
    readonly Dictionary<string, string> _saved = new();
    readonly object _gate = new();

    public TweakBackup(string file)
    {
        _file = file;
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) is { } d)
                _saved = d;
        }
        catch { }
    }

    public string? Peek(string key) { lock (_gate) return _saved.GetValueOrDefault(key); }

    /// <summary>Keeps the first value recorded: that is the state before CoreWatch touched it.</summary>
    public void Remember(string key, string value) { lock (_gate) _saved.TryAdd(key, value); }

    public void Forget(string key) { lock (_gate) _saved.Remove(key); }

    public void Save()
    {
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_saved, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, json);
        }
        catch { }
    }
}

/// <summary>One Windows setting behind a tweak: a registry value, a service, a scheduled task…</summary>
public abstract class TweakPart
{
    public abstract string Key { get; }
    /// <summary>True when the feature is on, false when off, null when it does not exist on this PC.</summary>
    public abstract bool? IsOn();
    public abstract void Set(bool on, TweakBackup backup);
}

/// <summary>A DWORD registry value. The feature counts as off only while the value equals <c>off</c>.</summary>
public sealed class RegPart(RegistryHive hive, string path, string name, int off, int? windowsDefault) : TweakPart
{
    public override string Key => $"reg:{hive}\\{path}\\{name}";

    RegistryKey Base() => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

    int? Read()
    {
        using var root = Base();
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) is int i ? i : null;
    }

    public override bool? IsOn() => Read() != off;

    public override void Set(bool on, TweakBackup backup)
    {
        using var root = Base();
        if (!on)
        {
            int? current = Read();
            if (current == off) return;
            using var key = root.CreateSubKey(path, true);
            key.SetValue(name, off, RegistryValueKind.DWord);
            backup.Remember(Key, current?.ToString() ?? "absent");
            return;
        }

        string? saved = backup.Peek(Key);
        int? target = saved == null ? windowsDefault : int.TryParse(saved, out int v) ? v : null;
        if (target == off) target = windowsDefault;
        using (var key = root.OpenSubKey(path, true))
        {
            if (target is int t) (key ?? root.CreateSubKey(path, true)).SetValue(name, t, RegistryValueKind.DWord);
            else key?.DeleteValue(name, false);
        }
        backup.Forget(Key);
    }
}

/// <summary>A setting changed through SystemParametersInfo. Windows' default for all of these is on.</summary>
public sealed class SpiPart(string id, Func<bool> get, Action<bool> set) : TweakPart
{
    public override string Key => "spi:" + id;
    public override bool? IsOn() => get();

    public override void Set(bool on, TweakBackup backup)
    {
        bool was = get();
        set(on);
        if (!on && was) backup.Remember(Key, "on");
        if (on) backup.Forget(Key);
    }
}

/// <summary>A Windows service; off means its startup type is Disabled.</summary>
public sealed class ServicePart(string name, string windowsDefault) : TweakPart
{
    public override string Key => "svc:" + name;

    // "2" automatic, "2d" automatic (delayed), "3" manual, "4" disabled
    string? Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        if (key?.GetValue("Start") is not int start) return null;
        return start == 2 && key.GetValue("DelayedAutostart") is int d && d != 0 ? "2d" : start.ToString();
    }

    public override bool? IsOn() => Read() is { } s ? s != "4" : null;

    public override void Set(bool on, TweakBackup backup)
    {
        if (!on)
        {
            string? current = Read();
            if (current is null or "4") return;
            Tool.Run("sc.exe", $"config \"{name}\" start= disabled", mustSucceed: true);
            backup.Remember(Key, current);
            Tool.Run("sc.exe", $"stop \"{name}\"");
            return;
        }

        string saved = backup.Peek(Key) ?? windowsDefault;
        string mode = saved switch { "2" => "auto", "2d" => "delayed-auto", _ => "demand" };
        Tool.Run("sc.exe", $"config \"{name}\" start= {mode}", mustSucceed: true);
        backup.Forget(Key);
        if (mode != "demand") Tool.Run("sc.exe", $"start \"{name}\"");
    }
}

/// <summary>A scheduled task, by its full path in Task Scheduler.</summary>
public sealed class TaskPart(string path) : TweakPart
{
    public override string Key => "task:" + path;

    dynamic Open()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is not available.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service.GetFolder("\\").GetTask(path);
    }

    public override bool? IsOn()
    {
        try { return (bool)Open().Enabled; }
        catch { return null; } // the task does not exist on this version of Windows
    }

    public override void Set(bool on, TweakBackup backup)
    {
        dynamic task = Open();
        bool was = (bool)task.Enabled;
        task.Enabled = on;
        if (!on && was) backup.Remember(Key, "on");
        if (on) backup.Forget(Key);
    }
}

/// <summary>Hibernation (and with it Fast Startup and hiberfil.sys).</summary>
public sealed class HibernatePart : TweakPart
{
    public override string Key => "hibernate";

    public override bool? IsOn()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
        return (key?.GetValue("HibernateEnabled") ?? key?.GetValue("HibernateEnabledDefault")) is int v ? v != 0 : null;
    }

    public override void Set(bool on, TweakBackup backup)
    {
        bool was = IsOn() == true;
        Tool.Run("powercfg.exe", on ? "/hibernate on" : "/hibernate off", mustSucceed: true);
        if (!on && was) backup.Remember(Key, "on");
        if (on) backup.Forget(Key);
    }
}

static class Tool
{
    /// <summary>Runs a Windows command-line tool hidden and returns its exit code.</summary>
    public static int Run(string exe, string args, bool mustSucceed = false)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {exe}.");
        var errors = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } throw new TimeoutException($"{exe} did not finish."); }
        if (mustSucceed && p.ExitCode != 0)
        {
            if (p.ExitCode == 5) throw new UnauthorizedAccessException();
            string last = (output + "\n" + errors.Result).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
            throw new InvalidOperationException(last.Length > 0 ? last : $"{exe} failed with code {p.ExitCode}.");
        }
        return p.ExitCode;
    }
}

/// <summary>A Windows feature the user can switch off (and back on).</summary>
public sealed class Tweak : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>What switching it off buys, in a few words.</summary>
    public required string Gain { get; init; }
    /// <summary>What still has to happen before the change is visible, e.g. "Sign out to finish".</summary>
    public string? Note { get; init; }
    public bool NeedsAdmin { get; init; }
    public bool Recommended { get; init; }
    public required TweakPart[] Parts { get; init; }
    /// <summary>Runs after a change, e.g. to tell running apps that a setting changed.</summary>
    public Action? Applied { get; init; }

    public bool HasNote => Note != null;
    public bool Locked => NeedsAdmin && !Sys.IsAdmin;

    /// <summary>The feature's state in Windows right now.</summary>
    public bool IsOn { get; private set; } = true;
    /// <summary>False when nothing this tweak controls exists on this PC.</summary>
    public bool Available { get; private set; } = true;
    /// <summary>True when CoreWatch switched it off and still holds the original values.</summary>
    public bool Touched { get; private set; }
    public bool Busy { get; private set; }
    public bool CanToggle => !Locked && !Busy;
    public string StateText => IsOn ? "On" : "Off";

    public void Update(TweakState state)
    {
        (Available, IsOn, Touched) = (state.Available, state.IsOn, state.Touched);
        Notify();
    }

    public void SetBusy(bool busy)
    {
        Busy = busy;
        Notify();
    }

    void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); // null = every property

    public event PropertyChangedEventHandler? PropertyChanged;
}

public readonly record struct TweakState(bool Available, bool IsOn, bool Touched);

public static class TweakService
{
    public static TweakBackup Backup { get; } = new(Path.Combine(Settings.Folder, "tweaks-backup.json"));

    /// <summary>Reads a tweak's current state from Windows. A tweak is off only when every part of it is off.</summary>
    public static TweakState Read(Tweak tweak, TweakBackup? backup = null)
    {
        backup ??= Backup;
        var states = tweak.Parts.Select(p => p.IsOn()).Where(s => s != null).ToList();
        bool on = states.Any(s => s == true);
        return new TweakState(states.Count > 0, on, !on && tweak.Parts.Any(p => backup.Peek(p.Key) != null));
    }

    /// <summary>Switches a feature off or back on. Returns an error message, or null when every part changed.</summary>
    public static string? Apply(Tweak tweak, bool on, TweakBackup? backup = null)
    {
        backup ??= Backup;
        // Switching back on returns to how things were before CoreWatch; only when CoreWatch never touched
        // the feature does it fall back to Windows' own defaults.
        bool restoreOnly = on && tweak.Parts.Any(p => backup.Peek(p.Key) != null);
        string? error = null;
        foreach (var part in tweak.Parts)
        {
            try
            {
                if (part.IsOn() is not { } now || now == on) continue;
                if (restoreOnly && backup.Peek(part.Key) == null) continue;
                part.Set(on, backup);
            }
            catch (Exception ex) { error ??= Describe(ex); }
        }
        backup.Save();
        try { tweak.Applied?.Invoke(); } catch { }
        return error;
    }

    static string Describe(Exception ex) => ex is UnauthorizedAccessException or SecurityException
        ? "Windows refused the change. Restart CoreWatch as administrator and try again."
        : ex.Message;

    // ───────── The catalogue ─────────
    public static List<Tweak> Build()
    {
        const string cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        var hkcu = RegistryHive.CurrentUser;
        var hklm = RegistryHive.LocalMachine;
        bool insider = IsInsiderPc();
        string hibernateGain = "Frees disk space";
        try
        {
            var file = new FileInfo(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "hiberfil.sys"));
            if (file.Exists) hibernateGain = $"Frees {Fmt.Bytes(file.Length)} on {file.Directory?.Root.Name.TrimEnd('\\')}";
        }
        catch { }

        return
        [
            // ── Look and feel ──
            new Tweak
            {
                Id = "transparency", Group = "Look and feel", Name = "Transparency effects", Gain = "Less GPU work",
                Description = "The see-through blur behind the taskbar, Start menu and window frames. Switched off, they are drawn solid.",
                Parts = [new RegPart(hkcu, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, 1)],
                Applied = () => Broadcast("ImmersiveColorSet"),
            },
            new Tweak
            {
                Id = "animations", Group = "Look and feel", Name = "Animations", Gain = "Snappier feel",
                Description = "Window open, close and minimize animations, taskbar animations and fades inside apps. Switched off, everything appears instantly.",
                Parts =
                [
                    new SpiPart("client-animation", () => GetSpi(0x1042), on => SetSpi(0x1043, on)),
                    new SpiPart("window-animation", GetMinAnimate, SetMinAnimate),
                    new RegPart(hkcu, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 0, 1),
                ],
                Applied = () => Broadcast("WindowMetrics"),
            },

            // ── Suggestions and feeds ──
            new Tweak
            {
                Id = "tips", Group = "Suggestions and feeds", Name = "Tips, suggestions and welcome screens", Gain = "Less background activity", Recommended = true,
                Description = "Windows tips, the \"finish setting up your PC\" screens after updates, and suggested content in Start and Settings.",
                Parts =
                [
                    new RegPart(hkcu, cdm, "SubscribedContent-338389Enabled", 0, 1),
                    new RegPart(hkcu, cdm, "SubscribedContent-310093Enabled", 0, 1),
                    new RegPart(hkcu, cdm, "SubscribedContent-338388Enabled", 0, 1),
                    new RegPart(hkcu, cdm, "SubscribedContent-338393Enabled", 0, 1),
                    new RegPart(hkcu, cdm, "SubscribedContent-353694Enabled", 0, 1),
                    new RegPart(hkcu, cdm, "SubscribedContent-353696Enabled", 0, 1),
                    new RegPart(hkcu, cdm, "SoftLandingEnabled", 0, 1),
                    new RegPart(hkcu, cdm, "SystemPaneSuggestionsEnabled", 0, 1),
                    new RegPart(hkcu, @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, 1),
                ],
            },
            new Tweak
            {
                Id = "suggested-apps", Group = "Suggestions and feeds", Name = "Automatic install of suggested apps", Gain = "Less background activity", Recommended = true,
                Description = "Lets Windows quietly download promoted Store apps onto this PC.",
                Parts = [new RegPart(hkcu, cdm, "SilentInstalledAppsEnabled", 0, 1)],
            },
            new Tweak
            {
                Id = "search-highlights", Group = "Suggestions and feeds", Name = "Search highlights", Gain = "Lighter search panel",
                Description = "The daily illustration in the search box and the trending content shown when you open search.",
                Parts = [new RegPart(hkcu, @"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0, 1)],
            },
            new Tweak
            {
                Id = "web-search", Group = "Suggestions and feeds", Name = "Web results in Start search", Gain = "Faster Start search", NeedsAdmin = true, Note = "Sign out to finish",
                Description = "Sends what you type in Start to Bing and mixes web results in with your apps and files. Switched off, search only looks on this PC.",
                Parts =
                [
                    new RegPart(hkcu, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null),
                    new RegPart(hkcu, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, 1),
                ],
            },
            new Tweak
            {
                Id = "widgets", Group = "Suggestions and feeds", Name = "Widgets and news feed", Gain = "Frees memory", NeedsAdmin = true, Note = "Sign out to finish",
                Description = "The Widgets board, its taskbar button with the weather, and the background processes that keep the feed up to date.",
                Parts = [new RegPart(hklm, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0, null)],
            },

            // ── Background activity ──
            new Tweak
            {
                Id = "telemetry", Group = "Background activity", Name = "Diagnostic data collection", Gain = "Less background CPU and disk", NeedsAdmin = true, Recommended = !insider,
                Description = "The telemetry service and the scheduled tasks that gather usage and compatibility data for Microsoft." +
                              (insider ? " This PC gets Windows Insider preview builds, which need this left on." : " Windows Insider preview builds need this left on."),
                Parts =
                [
                    new ServicePart("DiagTrack", "2"),
                    new ServicePart("dmwappushservice", "3"),
                    new TaskPart(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser"),
                    new TaskPart(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser Exp"),
                    new TaskPart(@"\Microsoft\Windows\Application Experience\ProgramDataUpdater"),
                    new TaskPart(@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator"),
                    new TaskPart(@"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip"),
                    new TaskPart(@"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector"),
                ],
            },
            new Tweak
            {
                Id = "edge-background", Group = "Background activity", Name = "Microsoft Edge startup boost and background mode", Gain = "Frees memory", NeedsAdmin = true, Recommended = true,
                Description = "Keeps Edge processes running from sign-in, and after you close it, so it opens faster. While this is off, Edge's settings say it is \"managed by your organization\".",
                Parts =
                [
                    new RegPart(hklm, @"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0, null),
                    new RegPart(hklm, @"SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0, null),
                ],
            },
            new Tweak
            {
                Id = "update-sharing", Group = "Background activity", Name = "Sharing updates with other PCs", Gain = "Frees bandwidth", NeedsAdmin = true, Recommended = true,
                Description = "Delivery Optimization uploads Windows and Store updates from this PC to other PCs. Switched off, your own updates still download normally from Microsoft.",
                Parts = [new RegPart(hklm, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, null)],
            },
            new Tweak
            {
                Id = "game-capture", Group = "Background activity", Name = "Game Bar capture", Gain = "Less overhead in games",
                Description = "Lets Xbox Game Bar record clips and screenshots (Win+Alt+R), which hooks a recorder into every game you start. Leave on if you record gameplay.",
                Parts =
                [
                    new RegPart(hkcu, @"System\GameConfigStore", "GameDVR_Enabled", 0, 1),
                    new RegPart(hkcu, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1),
                ],
            },
            new Tweak
            {
                Id = "hibernation", Group = "Background activity", Name = "Hibernation and Fast Startup", Gain = hibernateGain, NeedsAdmin = true,
                Description = "Hibernate saves memory to hiberfil.sys, a file that takes up a large share of your RAM size on the system drive. Switched off, that file is deleted, Hibernate leaves the power menu and cold boots take a little longer.",
                Parts = [new HibernatePart()],
            },

            // ── Services ──
            new Tweak
            {
                Id = "search-indexing", Group = "Services", Name = "Search indexing", Gain = "Less disk and CPU activity", NeedsAdmin = true,
                Description = "Keeps an index of your files and mail so searches are instant. Switched off, searching in File Explorer and Start still works but is much slower, and Outlook search can be limited.",
                Parts = [new ServicePart("WSearch", "2d")],
            },
            new Tweak
            {
                Id = "sysmain", Group = "Services", Name = "SysMain (Superfetch)", Gain = "Less disk activity", NeedsAdmin = true,
                Description = "Preloads the apps you use most into memory. Switched off, more memory stays free, but the first launch of an app can be slower and Windows stops compressing memory.",
                Parts = [new ServicePart("SysMain", "2")],
            },
            new Tweak
            {
                Id = "offline-maps", Group = "Services", Name = "Offline maps updater", Gain = "Fewer background services", NeedsAdmin = true, Recommended = true,
                Description = "Checks for updates to downloaded maps. Only useful if you keep offline maps on this PC.",
                Parts = [new ServicePart("MapsBroker", "2d")],
            },
            new Tweak
            {
                Id = "xbox-services", Group = "Services", Name = "Xbox services", Gain = "Fewer background services", NeedsAdmin = true,
                Description = "Xbox sign-in, cloud saves and networking. Leave on if you use the Xbox app, Game Pass or games that sign in with Xbox.",
                Parts = [new ServicePart("XblAuthManager", "3"), new ServicePart("XblGameSave", "3"), new ServicePart("XboxNetApiSvc", "3"), new ServicePart("XboxGipSvc", "3")],
            },
            new Tweak
            {
                Id = "printing", Group = "Services", Name = "Printing", Gain = "Fewer background services", NeedsAdmin = true,
                Description = "The Print Spooler. Switch it off only if you never print: printers and Print to PDF stop working until it is back on.",
                Parts = [new ServicePart("Spooler", "2")],
            },
        ];
    }

    static bool IsInsiderPc()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsSelfHost\Applicability");
            return key?.GetValue("EnablePreviewBuilds") is int v && v == 1;
        }
        catch { return false; }
    }

    // ───────── System parameters ─────────
    const uint UpdateAndNotify = 0x1 | 0x2; // SPIF_UPDATEINIFILE | SPIF_SENDCHANGE

    static bool GetSpi(uint action)
    {
        int value = 0;
        return !Native.SystemParametersInfo(action, 0, ref value, 0) || value != 0;
    }

    static void SetSpi(uint action, bool on)
    {
        if (!Native.SystemParametersInfo(action, 0, (IntPtr)(on ? 1 : 0), UpdateAndNotify)) throw new Win32Exception();
    }

    static bool GetMinAnimate()
    {
        var info = new Native.ANIMATIONINFO { cbSize = 8 };
        return !Native.SystemParametersInfo(0x0048, 8, ref info, 0) || info.iMinAnimate != 0;
    }

    static void SetMinAnimate(bool on)
    {
        var info = new Native.ANIMATIONINFO { cbSize = 8, iMinAnimate = on ? 1 : 0 };
        if (!Native.SystemParametersInfo(0x0049, 8, ref info, UpdateAndNotify)) throw new Win32Exception();
    }

    /// <summary>Tells running apps (Explorer above all) that a setting changed.</summary>
    static void Broadcast(string area) =>
        Native.SendMessageTimeout((IntPtr)0xFFFF, 0x001A /*WM_SETTINGCHANGE*/, IntPtr.Zero, area, 0x0002 /*ABORTIFHUNG*/, 500, out _);

    // ───────── Developer: exercise switch-off and undo against CoreWatch's own registry key ─────────
    public static string SelfTest()
    {
        const string path = @"Software\CoreWatch\SelfTest";
        string file = Path.Combine(Path.GetTempPath(), $"corewatch-selftest-{Environment.ProcessId}.json");
        var log = new List<string>();
        int failures = 0;
        void Check(string what, bool ok) { log.Add($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }
        int? Value(string name) { using var k = Registry.CurrentUser.OpenSubKey(path); return k?.GetValue(name) is int i ? i : null; }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, false);
            using (var k = Registry.CurrentUser.CreateSubKey(path)) { k.SetValue("WasFive", 5, RegistryValueKind.DWord); k.SetValue("AlreadyOff", 0, RegistryValueKind.DWord); }

            var backup = new TweakBackup(file);
            var tweak = new Tweak
            {
                Id = "selftest", Group = "", Name = "", Description = "", Gain = "",
                Parts =
                [
                    new RegPart(RegistryHive.CurrentUser, path, "WasFive", 0, 1),       // had a custom value
                    new RegPart(RegistryHive.CurrentUser, path, "WasAbsent", 0, 1),     // did not exist
                    new RegPart(RegistryHive.CurrentUser, path, "AlreadyOff", 0, 1),    // the user had switched this off before
                    new RegPart(RegistryHive.CurrentUser, path, "PolicyStyle", 1, null),// off = value present
                ],
            };

            Check("starts on (some parts on)", Read(tweak, backup) is { Available: true, IsOn: true, Touched: false });
            Check("switch off reports no error", Apply(tweak, false, backup) == null);
            Check("every value is at its off setting", Value("WasFive") == 0 && Value("WasAbsent") == 0 && Value("AlreadyOff") == 0 && Value("PolicyStyle") == 1);
            Check("reads as off and changed by CoreWatch", Read(tweak, backup) is { IsOn: false, Touched: true });
            Check("originals survive a reload of the backup file", new TweakBackup(file).Peek(tweak.Parts[0].Key) == "5" && new TweakBackup(file).Peek(tweak.Parts[1].Key) == "absent");
            Check("a value that was already off is not recorded", backup.Peek(tweak.Parts[2].Key) == null);

            Check("switch back on reports no error", Apply(tweak, true, new TweakBackup(file)) == null);
            Check("custom value restored (5), absent values removed", Value("WasFive") == 5 && Value("WasAbsent") == null && Value("PolicyStyle") == null);
            Check("the value the user had switched off stays off", Value("AlreadyOff") == 0);
            Check("backup file is empty again", new TweakBackup(file).Peek(tweak.Parts[0].Key) == null && new TweakBackup(file).Peek(tweak.Parts[3].Key) == null);

            var untouched = new Tweak { Id = "selftest2", Group = "", Name = "", Description = "", Gain = "", Parts = [new RegPart(RegistryHive.CurrentUser, path, "AlreadyOff", 0, 1)] };
            Check("a feature CoreWatch never touched reads as off, not changed", Read(untouched, backup) is { IsOn: false, Touched: false });
            Check("switching it on falls back to the Windows default", Apply(untouched, true, backup) == null && Value("AlreadyOff") == 1);
        }
        catch (Exception ex) { Check("no exception: " + ex, false); }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(path, false); } catch { }
            try { File.Delete(file); } catch { }
        }

        // Read-only: every real tweak must be readable on this PC.
        foreach (var t in Build())
        {
            try
            {
                var s = Read(t);
                int present = t.Parts.Count(p => p.IsOn() != null);
                log.Add($"READ  {t.Id,-18} {(s.Available ? s.IsOn ? "on " : "off" : "n/a")}  {present}/{t.Parts.Length} parts present");
            }
            catch (Exception ex) { Check($"read {t.Id}: {ex.Message}", false); }
        }

        log.Insert(0, failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
        return string.Join(Environment.NewLine, log);
    }
}
