using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;

namespace CoreWatch.Services;

// ───────────────────────── Power and battery ─────────────────────────

public sealed record PowerPlan(string Guid, string Name, bool Active)
{
    public string Description => Name.ToLowerInvariant() switch
    {
        var n when n.Contains("ultimate") => "No power saving at all. For workstations that should never slow down.",
        var n when n.Contains("high") => "Keeps the processor at full speed. Uses more power and runs warmer.",
        var n when n.Contains("saver") => "Slows the PC and dims the screen to stretch battery life.",
        var n when n.Contains("balanced") => "Speeds up when you need it and saves power when you don't. Right for most PCs.",
        _ => "A custom plan from Windows or your PC maker.",
    };
}

public sealed record BatteryInfo(bool Present, bool PluggedIn, bool Charging, int Percent, TimeSpan? Remaining,
    long DesignCapacity, long FullCapacity, int? Cycles, string Name)
{
    /// <summary>How much of its original capacity the battery still holds, or null when Windows does not say.</summary>
    public double? Health => DesignCapacity > 0 && FullCapacity > 0 ? Math.Min(100, 100.0 * FullCapacity / DesignCapacity) : null;
}

public static partial class PowerService
{
    [GeneratedRegex(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\((.+)\)(\s*\*)?")]
    private static partial Regex PlanLine();

    /// <summary>Runs a System32 tool hidden and returns its output and exit code.</summary>
    static (int Code, string Output) Capture(string exe, string args)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {exe}.");
        var errors = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } throw new TimeoutException($"{exe} did not finish."); }
        return (p.ExitCode, output + errors.Result);
    }

    public static List<PowerPlan> Plans() => ParsePlans(Capture("powercfg.exe", "/list").Output);

    /// <summary>Reads "powercfg /list". Only the GUID, the name in brackets and the trailing star matter, so it works in any language.</summary>
    public static List<PowerPlan> ParsePlans(string output)
    {
        var plans = new List<PowerPlan>();
        foreach (string line in output.Split('\n'))
            if (PlanLine().Match(line) is { Success: true } m)
                plans.Add(new PowerPlan(m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.Trim(), m.Groups[3].Success));
        return plans;
    }

    /// <summary>Returns an error message, or null when the plan was activated.</summary>
    public static string? SetActive(PowerPlan plan)
    {
        try
        {
            var (code, output) = Capture("powercfg.exe", $"/setactive {plan.Guid}");
            return code == 0 ? null : output.Trim().Split('\n').LastOrDefault()?.Trim() is { Length: > 0 } last ? last : $"powercfg returned {code}.";
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static BatteryInfo Battery()
    {
        Native.GetSystemPowerStatus(out var status);
        bool present = (status.BatteryFlag & 128) == 0 && status.BatteryFlag != 255;
        bool plugged = status.ACLineStatus == 1;
        if (!present) return new BatteryInfo(false, plugged, false, 0, null, 0, 0, null, "");

        long design = 0, full = 0;
        int? cycles = null;
        string name = "";
        try
        {
            foreach (ManagementBaseObject o in new ManagementObjectSearcher(@"root\wmi", "SELECT DesignedCapacity, DeviceName FROM BatteryStaticData").Get())
            {
                design += Convert.ToInt64(o["DesignedCapacity"] ?? 0);
                name = (o["DeviceName"] as string ?? "").Trim();
            }
            foreach (ManagementBaseObject o in new ManagementObjectSearcher(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity").Get())
                full += Convert.ToInt64(o["FullChargedCapacity"] ?? 0);
            foreach (ManagementBaseObject o in new ManagementObjectSearcher(@"root\wmi", "SELECT CycleCount FROM BatteryCycleCount").Get())
                if (Convert.ToInt32(o["CycleCount"] ?? 0) is > 0 and var c) cycles = c;
        }
        catch { }

        return new BatteryInfo(true, plugged, (status.BatteryFlag & 8) != 0, status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : 0,
            status.BatteryLifeTime > 0 ? TimeSpan.FromSeconds(status.BatteryLifeTime) : null, design, full, cycles, name);
    }

    /// <summary>Asks Windows to write its battery report and returns the file, or an error.</summary>
    public static (string? File, string? Error) BatteryReport()
    {
        try
        {
            string file = Path.Combine(Settings.Folder, "battery-report.html");
            Directory.CreateDirectory(Settings.Folder);
            var (code, output) = Capture("powercfg.exe", $"/batteryreport /output \"{file}\"");
            return code == 0 && File.Exists(file) ? (file, null) : (null, output.Trim());
        }
        catch (Exception ex) { return (null, ex.Message); }
    }
}

// ───────────────────────── Reliability (event log) ─────────────────────────

public sealed record ReliabilityEvent(DateTime Time, string Kind, string Title, string Detail, string Glyph, string Tone)
{
    public string When => Time.ToString("d MMM, HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    public System.Windows.Media.Brush ToneBrush => (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource(Tone);
}

public sealed record BootRecord(DateTime Time, double Seconds)
{
    public string When => Time.ToString("d MMM, HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    public string Text => $"{Seconds:0} s";
}

public sealed record ReliabilityReport(List<ReliabilityEvent> Events, List<BootRecord> Boots, string? BootNote, List<string> Problems);

/// <summary>Reads crashes, freezes and unexpected shutdowns that Windows already wrote to its event logs. Changes nothing.</summary>
public static class ReliabilityService
{
    public const int Days = 30;

    static IEnumerable<EventRecord> Query(string log, string filter)
    {
        long ms = (long)TimeSpan.FromDays(Days).TotalMilliseconds;
        var query = new EventLogQuery(log, PathType.LogName, $"*[System[{filter} and TimeCreated[timediff(@SystemTime) <= {ms}]]]") { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        for (int i = 0; i < 400; i++)
        {
            EventRecord? record;
            try { record = reader.ReadEvent(); } catch { break; }
            if (record == null) break;
            yield return record;
        }
    }

    static string Data(EventRecord record, int index)
    {
        try { return index < record.Properties.Count ? record.Properties[index].Value?.ToString()?.Trim() ?? "" : ""; }
        catch { return ""; }
    }

    static DateTime When(EventRecord record) => record.TimeCreated ?? DateTime.MinValue;

    public static ReliabilityReport Load()
    {
        var events = new List<ReliabilityEvent>();
        var problems = new List<string>();

        void Read(string what, Action work)
        {
            try { work(); }
            catch (Exception ex) { problems.Add($"{what}: {(ex is UnauthorizedAccessException ? "needs administrator rights" : ex.Message)}"); }
        }

        Read("App crashes", () =>
        {
            foreach (var r in Query("Application", "Provider[@Name='Application Error'] and EventID=1000"))
                using (r)
                {
                    string app = Data(r, 0), module = Data(r, 3);
                    events.Add(new ReliabilityEvent(When(r), "App crash", $"{Trim(app)} stopped working",
                        module.Length > 0 && !module.Equals(app, StringComparison.OrdinalIgnoreCase) ? $"The fault was in {module}." : "The app closed unexpectedly.", "", "B.Warn"));
                }
        });

        Read("App freezes", () =>
        {
            foreach (var r in Query("Application", "Provider[@Name='Application Hang'] and EventID=1002"))
                using (r)
                    events.Add(new ReliabilityEvent(When(r), "App freeze", $"{Trim(Data(r, 0))} stopped responding", "Windows closed it after it froze.", "", "B.Disk"));
        });

        Read("Blue screens", () =>
        {
            foreach (var r in Query("System", "Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001"))
                using (r)
                {
                    string code = Data(r, 0);
                    int space = code.IndexOf(' ');
                    events.Add(new ReliabilityEvent(When(r), "Blue screen", "Windows stopped with a blue screen",
                        code.Length > 0 ? $"Stop code {(space > 0 ? code[..space] : code)}." : "Windows restarted after a system error.", "", "B.Bad"));
                }
        });

        Read("Unexpected shutdowns", () =>
        {
            foreach (var r in Query("System", "Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41"))
                using (r)
                    events.Add(new ReliabilityEvent(When(r), "Unexpected shutdown", "The PC turned off without shutting down",
                        "A power cut, a held power button, or a crash that left no blue screen record.", "", "B.Bad"));
        });

        // A blue screen is also logged as an unexpected shutdown a moment earlier: keep the more specific entry.
        var screens = events.Where(e => e.Kind == "Blue screen").Select(e => e.Time).ToList();
        events.RemoveAll(e => e.Kind == "Unexpected shutdown" && screens.Any(t => Math.Abs((t - e.Time).TotalMinutes) < 10));

        var boots = new List<BootRecord>();
        string? bootNote = null;
        try
        {
            foreach (var r in Query("Microsoft-Windows-Diagnostics-Performance/Operational", "EventID=100"))
                using (r)
                {
                    // Named data: BootTime is the full time to a usable desktop, in milliseconds.
                    double ms = 0;
                    try
                    {
                        var xml = System.Xml.Linq.XDocument.Parse(r.ToXml());
                        var node = xml.Descendants().FirstOrDefault(n => n.Name.LocalName == "Data" && (string?)n.Attribute("Name") == "BootTime");
                        if (node != null) double.TryParse(node.Value, out ms);
                    }
                    catch { }
                    if (ms > 0) boots.Add(new BootRecord(When(r), ms / 1000));
                }
            if (boots.Count == 0) bootNote = $"Windows has not recorded a start-up time in the last {Days} days.";
        }
        catch (UnauthorizedAccessException) { bootNote = "Start-up times are kept in a log only administrators can read. Restart CoreWatch as administrator to see them."; }
        catch (Exception ex) { bootNote = "Start-up times could not be read: " + ex.Message; }

        return new ReliabilityReport(events.OrderByDescending(e => e.Time).ToList(), boots.OrderByDescending(b => b.Time).ToList(), bootNote, problems);
    }

    static string Trim(string app) => app.Length == 0 ? "An app" : app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
}
