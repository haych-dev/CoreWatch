using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CoreWatch.Services;

// ───────────────────────── Notification-area icon ─────────────────────────

/// <summary>The icon next to the clock: opens the window, offers a short menu and shows Windows notifications.</summary>
public sealed class TrayService : IDisposable
{
    readonly System.Windows.Forms.NotifyIcon _icon;
    readonly Action _open;
    Action? _onClick;

    public TrayService(Action open, Action refresh, Action exit)
    {
        _open = open;
        System.Drawing.Icon icon;
        try
        {
            using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;
            icon = new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        }
        catch { icon = System.Drawing.SystemIcons.Application; }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open CoreWatch", null, (_, _) => open());
        menu.Items.Add("Refresh PC now", null, (_, _) => refresh());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _icon = new System.Windows.Forms.NotifyIcon { Icon = icon, Text = "CoreWatch", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) open(); };
        _icon.BalloonTipClicked += (_, _) => (_onClick ?? _open)();
    }

    /// <summary>Hover text; Windows limits it to a short line.</summary>
    public void SetTip(string text)
    {
        try { _icon.Text = text.Length > 63 ? text[..63] : text; } catch { }
    }

    public void Notify(string title, string text, bool warning = false, Action? onClick = null)
    {
        _onClick = onClick;
        try { _icon.ShowBalloonTip(8000, title, text, warning ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info); } catch { }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

/// <summary>"Start with Windows" for the current user, through the standard Run key.</summary>
public static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "CoreWatch";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (value && Environment.ProcessPath is { } exe) key.SetValue(Name, $"\"{exe}\" --tray");
            else key.DeleteValue(Name, false);
        }
    }
}

// ───────────────────────── Alerts ─────────────────────────

public sealed record AlertEvent(DateTime Time, string Title, string Detail)
{
    public string When => Time.ToString("d MMM, HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>Watches each snapshot and raises an alert when a reading stays past its threshold.</summary>
public static class AlertService
{
    public static ObservableCollection<AlertEvent> Recent { get; } = [];
    public static event Action<AlertEvent>? Raised;

    static readonly Dictionary<string, DateTime> Since = new(), LastFired = new();
    static DateTime _lastCheck;
    static string LogFile => Path.Combine(Settings.Folder, "alerts.log");

    public static void Start()
    {
        try
        {
            if (File.Exists(LogFile))
                foreach (string line in File.ReadLines(LogFile).TakeLast(20))
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length == 3 && DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time))
                        Recent.Insert(0, new AlertEvent(time, parts[1], parts[2]));
                }
        }
        catch { }
        SystemMonitor.Instance.Updated += Check;
    }

    static void Check(Snapshot s)
    {
        var set = Settings.Current;
        var now = DateTime.Now;
        if (!set.AlertsEnabled || (now - _lastCheck).TotalSeconds < 5) return;
        _lastCheck = now;

        foreach (var d in s.Drives.Where(d => d.Type == DriveType.Fixed && d.Total > 0))
        {
            double freePct = 100.0 * d.Free / d.Total;
            Rule("disk:" + d.Name, set.AlertDiskFreePct > 0 && freePct < set.AlertDiskFreePct, 0, 12 * 60,
                () => ($"Drive {d.Name.TrimEnd('\\')} is almost full", $"{Fmt.Bytes(d.Free)} free of {Fmt.Bytes(d.Total)} ({freePct:0}% free)."));
        }

        Rule("memory", set.AlertMemPct > 0 && s.MemPct >= set.AlertMemPct, 60, 30,
            () => ("Memory is nearly full", $"{Fmt.Pct(s.MemPct)} in use for over a minute, {Fmt.Bytes(s.MemAvail)} available."));

        Rule("cpu", set.AlertCpuPct > 0 && s.CpuTotal >= set.AlertCpuPct, 300, 30,
            () => ("The processor has been busy for 5 minutes", $"CPU load is at {Fmt.Pct(s.CpuTotal)}. The Processes page shows what is using it."));

        foreach (var g in s.Gpus)
            if (g.Temperature is { } t)
                Rule("gpu:" + g.Luid, set.AlertGpuTemp > 0 && t >= set.AlertGpuTemp, 30, 30,
                    () => ("The graphics card is running hot", $"{g.Name} is at {t:0} °C."));
    }

    /// <summary>Fires once the condition has held for <paramref name="holdSeconds"/>, then stays quiet for the cooldown.</summary>
    static void Rule(string key, bool active, int holdSeconds, int cooldownMinutes, Func<(string Title, string Detail)> describe)
    {
        var now = DateTime.Now;
        if (!active) { Since.Remove(key); return; }
        if (!Since.TryGetValue(key, out var since)) Since[key] = since = now;
        if ((now - since).TotalSeconds < holdSeconds) return;
        if (LastFired.TryGetValue(key, out var last) && (now - last).TotalMinutes < cooldownMinutes) return;
        LastFired[key] = now;
        var (title, detail) = describe();
        Raise(title, detail);
    }

    public static void Raise(string title, string detail)
    {
        var alert = new AlertEvent(DateTime.Now, title, detail);
        Recent.Insert(0, alert);
        while (Recent.Count > 20) Recent.RemoveAt(Recent.Count - 1);
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.AppendAllText(LogFile, $"{alert.Time:o}\t{title}\t{detail}{Environment.NewLine}");
        }
        catch { }
        Raised?.Invoke(alert);
    }
}

// ───────────────────────── Scheduled maintenance ─────────────────────────

public sealed record MaintenanceRun(DateTime Time, bool Automatic, string Headline, List<string> Steps)
{
    public string When => Time.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
    public string Kind => Automatic ? "Scheduled" : "Manual";
}

/// <summary>Runs the safe Refresh on a schedule while CoreWatch is open, and keeps a log of every run.</summary>
public static class MaintenanceService
{
    static string LogFile => Path.Combine(Settings.Folder, "maintenance.json");
    static DispatcherTimer? _timer;
    static bool _running;

    public static event Action<MaintenanceRun>? Finished;

    public static List<MaintenanceRun> History(string? file = null)
    {
        try
        {
            if (File.Exists(file ??= LogFile)) return JsonSerializer.Deserialize<List<MaintenanceRun>>(File.ReadAllText(file)) ?? [];
        }
        catch { }
        return [];
    }

    public static MaintenanceRun Record(RefreshReport report, bool automatic, string? file = null)
    {
        var run = new MaintenanceRun(DateTime.Now, automatic, report.Headline, report.Steps.Select(s => $"{s.Name}: {s.Result}").ToList());
        try
        {
            var all = History(file);
            all.Insert(0, run);
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(file ?? LogFile, JsonSerializer.Serialize(all.Take(50).ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        return run;
    }

    public static void Start()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += async (_, _) => { if (IsDue(Settings.Current.AutoRefresh, Settings.Current.LastAutoRefresh, DateTime.Now, Native.IdleTime())) await RunNow(automatic: true); };
        _timer.Start();
    }

    /// <summary>Due when the interval has passed and nobody has touched the PC for a few minutes.</summary>
    public static bool IsDue(string schedule, DateTime lastRun, DateTime now, TimeSpan idle)
    {
        double days = schedule switch { "daily" => 1, "weekly" => 7, _ => 0 };
        return days > 0 && (now - lastRun).TotalDays >= days && idle.TotalMinutes >= 3;
    }

    public static DateTime? NextDue()
    {
        var set = Settings.Current;
        double days = set.AutoRefresh switch { "daily" => 1, "weekly" => 7, _ => 0 };
        if (days == 0) return null;
        var due = set.LastAutoRefresh.AddDays(days);
        return due < DateTime.Now ? DateTime.Now : due;
    }

    /// <summary>Runs the Refresh outside the Overview page (schedule or tray menu). Returns null when one is already running.</summary>
    public static async Task<MaintenanceRun?> RunNow(bool automatic)
    {
        if (_running) return null;
        _running = true;
        try
        {
            if (automatic) { Settings.Current.LastAutoRefresh = DateTime.Now; Settings.Current.Save(); }
            var report = await RefreshService.Run(_ => { });
            var run = Record(report, automatic);
            Finished?.Invoke(run);
            return run;
        }
        catch { return null; }
        finally { _running = false; }
    }
}

// ───────────────────────── Long-term history ─────────────────────────

public sealed record MetricPoint(DateTime Time, double Cpu, double Mem, double Gpu, double GpuTemp, double Vram, double Down, double Up);

/// <summary>Writes one averaged line per minute to a file per day, so readings can be charted over days.</summary>
public static class MetricLog
{
    public const int KeepDays = 30;
    public static string Folder => Path.Combine(Settings.Folder, "History");

    static long _minute;
    static int _count, _tempCount;
    static bool _hasGpu;
    static double _cpu, _mem, _gpu, _temp, _vram, _down, _up;

    public static void Start()
    {
        Task.Run(Prune);
        SystemMonitor.Instance.Updated += Add;
    }

    public static void Stop()
    {
        SystemMonitor.Instance.Updated -= Add;
        if (_count > 0) Flush();
    }

    static void Add(Snapshot s)
    {
        if (!Settings.Current.RecordHistory) { _count = 0; return; }
        long minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        if (_count > 0 && minute != _minute) Flush();
        _minute = minute;
        _count++;
        _cpu += s.CpuTotal;
        _mem += s.MemPct;
        _down += s.NetDown;
        _up += s.NetUp;
        if (s.PrimaryGpu is { } g)
        {
            _hasGpu = true;
            _gpu += g.Utilization;
            _vram += g.DedicatedPct;
            if (g.Temperature is { } t) { _temp += t; _tempCount++; }
        }
    }

    static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    static void Flush()
    {
        try
        {
            var time = DateTimeOffset.FromUnixTimeSeconds(_minute * 60).LocalDateTime;
            string line = string.Join(',', _minute * 60, F(_cpu / _count), F(_mem / _count),
                _hasGpu ? F(_gpu / _count) : "", _tempCount > 0 ? F(_temp / _tempCount) : "", _hasGpu ? F(_vram / _count) : "",
                F(_down / _count), F(_up / _count));
            Directory.CreateDirectory(Folder);
            File.AppendAllText(Path.Combine(Folder, $"{time:yyyy-MM-dd}.csv"), line + "\n");
        }
        catch { }
        _count = _tempCount = 0;
        _hasGpu = false;
        _cpu = _mem = _gpu = _temp = _vram = _down = _up = 0;
    }

    static void Prune()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var cutoff = DateTime.Today.AddDays(-KeepDays);
            foreach (string file in Directory.GetFiles(Folder, "*.csv"))
                if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < cutoff)
                    File.Delete(file);
        }
        catch { }
    }

    /// <summary>Every recorded minute since <paramref name="from"/>, oldest first.</summary>
    public static List<MetricPoint> Load(DateTime from)
    {
        var points = new List<MetricPoint>();
        try
        {
            if (!Directory.Exists(Folder)) return points;
            foreach (string file in Directory.GetFiles(Folder, "*.csv").OrderBy(f => f))
            {
                if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day < from.Date) continue;
                foreach (string line in File.ReadLines(file))
                {
                    string[] p = line.Split(',');
                    if (p.Length < 8 || !long.TryParse(p[0], out long unix)) continue;
                    var time = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
                    if (time < from) continue;
                    points.Add(new MetricPoint(time, N(p[1]), N(p[2]), N(p[3]), N(p[4]), N(p[5]), N(p[6]), N(p[7])));
                }
            }
        }
        catch { }
        return points;

        static double N(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
    }
}

// ───────────────────────── Update check ─────────────────────────

/// <summary>Asks GitHub for the newest release. Nothing about this PC is sent beyond the request itself.</summary>
public static class UpdateService
{
    public const string ReleasesUrl = "https://github.com/haych-dev/CoreWatch/releases/latest";
    const string ApiUrl = "https://api.github.com/repos/haych-dev/CoreWatch/releases/latest";

    public static event Action? Changed;

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static Version? Parse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string text = tag.Trim().TrimStart('v', 'V');
        int cut = text.IndexOfAny(['-', '+', ' ']);
        if (cut > 0) text = text[..cut];
        if (!text.Contains('.')) text += ".0";
        return Version.TryParse(text, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    public static bool IsNewer(string? tag) => Parse(tag) is { } latest && latest > Current;
    public static bool UpdateAvailable => IsNewer(Settings.Current.LatestVersion);

    /// <summary>Returns an error message, or null when GitHub answered.</summary>
    public static async Task<string?> Check()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CoreWatch/" + Current.ToString(3));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(ApiUrl));
            string? tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (Parse(tag) == null) return "GitHub did not report a release version.";
            Settings.Current.LatestVersion = tag!;
            Settings.Current.LastUpdateCheck = DateTime.Now;
            Settings.Current.Save();
            Changed?.Invoke();
            return null;
        }
        catch (Exception ex) { return ex.InnerException?.Message ?? ex.Message; }
    }

    /// <summary>The automatic check: at most once a day, and only when the setting is on.</summary>
    public static async Task CheckIfDue()
    {
        if (Settings.Current.CheckForUpdates && (DateTime.Now - Settings.Current.LastUpdateCheck).TotalHours >= 24) await Check();
    }
}
