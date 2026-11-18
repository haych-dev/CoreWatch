using System.ComponentModel;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Microsoft.Win32;

namespace CoreWatch.Services;

/// <summary>One Windows service as shown on the Services page.</summary>
public sealed class ServiceItem : INotifyPropertyChanged
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = "";
    public string Account { get; init; } = "";
    public bool ThirdParty { get; init; }
    /// <summary>Why CoreWatch will not change this service, or null when it can be changed.</summary>
    public string? ProtectedReason { get; init; }

    public string State { get; private set; } = "";
    /// <summary>"2" automatic, "2d" automatic (delayed), "3" manual, "4" disabled.</summary>
    public string Start { get; private set; } = "";
    /// <summary>The startup type before CoreWatch changed it, or null when untouched.</summary>
    public string? Original { get; private set; }
    public bool Busy { get; private set; }

    public bool Running => State == "Running";
    public bool Protected => ProtectedReason != null;
    public bool Changed => Original != null && Original != Start;
    public string StartText => ServiceManager.StartName(Start);
    public string StateText => State.Length == 0 ? "Unknown" : State;
    public int StartOrder => Start switch { "2" => 0, "2d" => 1, "3" => 2, "4" => 3, _ => 4 };
    public Brush StateBrush => (Brush)System.Windows.Application.Current.FindResource(Running ? "B.Good" : State == "Stopped" ? "B.Faint" : "B.Warn");
    public string Search => $"{DisplayName} {Name}";

    public void Update(string state, string start, string? original)
    {
        (State, Start, Original) = (state, start, original);
        Notify();
    }

    public void SetBusy(bool busy)
    {
        Busy = busy;
        Notify();
    }

    void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public static partial class ServiceManager
{
    /// <summary>Original startup types of services changed here, so "Undo" is exact.</summary>
    public static TweakBackup Backup { get; } = new(Path.Combine(Settings.Folder, "services-backup.json"));

    // Security features stay out of reach, in line with the rest of the app.
    static readonly HashSet<string> Security = new(StringComparer.OrdinalIgnoreCase)
    {
        "WinDefend", "WdNisSvc", "Sense", "SecurityHealthService", "wscsvc", "mpssvc", "BFE", "MDCoreSvc", "webthreatdefsvc", "webthreatdefusersvc",
        "wuauserv", "UsoSvc", "WaaSMedicSvc", "DoSvc", "AppIDSvc", "KeyIso", "VaultSvc", "EFS", "sppsvc", "ClipSVC", "TrustedInstaller",
    };

    // Windows does not start, sign in or connect without these.
    static readonly HashSet<string> Core = new(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "EventLog", "PlugPlay", "Power", "ProfSvc", "Schedule", "SystemEventsBroker",
        "BrokerInfrastructure", "CoreMessagingRegistrar", "gpsvc", "Winmgmt", "CryptSvc", "Dhcp", "Dnscache", "NlaSvc", "nsi", "netprofm",
        "LanmanWorkstation", "AudioSrv", "AudioEndpointBuilder", "UserManager", "StateRepository", "msiserver", "AppXSvc", "Themes",
        "ShellHWDetection", "TimeBrokerSvc", "TokenBroker", "Wcmsvc", "WlanSvc", "EventSystem", "SENS", "DeviceInstall", "DsmSvc", "FontCache",
        "camsvc", "TextInputManagementService", "DispBrokerDesktopSvc", "WinHttpAutoProxySvc", "iphlpsvc", "LanmanServer", "W32Time",
    };

    [GeneratedRegex("_[0-9a-f]{4,}$", RegexOptions.IgnoreCase)]
    private static partial Regex PerUserSuffix();

    public static string StartName(string start) => start switch
    {
        "2" => "Automatic", "2d" => "Automatic (delayed)", "3" => "Manual", "4" => "Disabled", "0" or "1" => "With Windows", _ => "Unknown",
    };

    static string StartMode(string start) => start switch { "2" => "auto", "2d" => "delayed-auto", "4" => "disabled", _ => "demand" };

    /// <summary>Startup type straight from the registry, which also knows about delayed start.</summary>
    public static string ReadStart(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            if (key?.GetValue("Start") is not int start) return "";
            return start == 2 && key.GetValue("DelayedAutostart") is int d && d != 0 ? "2d" : start.ToString();
        }
        catch { return ""; }
    }

    static string? WhyProtected(string name)
    {
        if (Security.Contains(name)) return "This is part of Windows' security or update system. CoreWatch leaves those alone.";
        if (Core.Contains(name)) return "Windows needs this service to start, sign in or connect.";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            if (key?.GetValue("LaunchProtected") is int p && p != 0) return "Windows protects this service from changes.";
            if (PerUserSuffix().IsMatch(name) && key?.GetValue("Type") is int type && (type & 0x80) != 0)
                return "This is a per-user copy that Windows recreates at every sign-in, so changes would not last.";
        }
        catch { }
        return null;
    }

    public static List<ServiceItem> List()
    {
        var list = new List<ServiceItem>();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        using var query = new ManagementObjectSearcher("SELECT Name, DisplayName, Description, State, PathName, StartName FROM Win32_Service");
        foreach (ManagementBaseObject o in query.Get())
        {
            string name = o["Name"] as string ?? "";
            if (name.Length == 0) continue;
            string path = (o["PathName"] as string ?? "").Trim().Trim('"');
            var item = new ServiceItem
            {
                Name = name,
                DisplayName = o["DisplayName"] as string is { Length: > 0 } d ? d : name,
                Description = (o["Description"] as string ?? "").Trim(),
                Account = o["StartName"] as string ?? "",
                ThirdParty = path.Length > 0 && !path.StartsWith(windows, StringComparison.OrdinalIgnoreCase),
                ProtectedReason = WhyProtected(name),
            };
            item.Update(o["State"] as string ?? "", ReadStart(name), Backup.Peek("svc:" + name));
            list.Add(item);
        }
        return list.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static string ReadState(string name)
    {
        try
        {
            using var service = new ManagementObject($"Win32_Service.Name='{name.Replace("'", "\\'")}'");
            service.Get();
            return service["State"] as string ?? "";
        }
        catch { return ""; }
    }

    /// <summary>Re-reads one service from Windows so the row shows what really happened.</summary>
    public static void Refresh(ServiceItem item) => item.Update(ReadState(item.Name), ReadStart(item.Name), Backup.Peek("svc:" + item.Name));

    static string Describe(Exception ex) => ex is UnauthorizedAccessException or System.Security.SecurityException
        ? "Windows refused the change. Restart CoreWatch as administrator and try again."
        : ex.Message;

    /// <summary>Changes the startup type and remembers the original. Returns an error message, or null.</summary>
    public static string? SetStart(ServiceItem item, string start)
    {
        if (item.Protected) return item.ProtectedReason;
        try
        {
            string current = ReadStart(item.Name);
            if (current == start) return null;
            Tool.Run("sc.exe", $"config \"{item.Name}\" start= {StartMode(start)}", mustSucceed: true);
            string key = "svc:" + item.Name;
            Backup.Remember(key, current);
            if (Backup.Peek(key) == start) Backup.Forget(key); // back where it began
            Backup.Save();
            return null;
        }
        catch (Exception ex) { return Describe(ex); }
    }

    /// <summary>Puts the startup type back to what it was before CoreWatch changed it.</summary>
    public static string? Undo(ServiceItem item) => Backup.Peek("svc:" + item.Name) is { } original ? SetStart(item, original) : null;

    /// <summary>Starts or stops a service and waits briefly for it to settle. Returns an error message, or null.</summary>
    public static string? Control(ServiceItem item, bool start)
    {
        if (item.Protected) return item.ProtectedReason;
        try
        {
            using var service = new ManagementObject($"Win32_Service.Name='{item.Name.Replace("'", "\\'")}'");
            uint code = Convert.ToUInt32(service.InvokeMethod(start ? "StartService" : "StopService", null));
            string? error = code switch
            {
                0 or 10 => null,
                2 => "Windows refused the request. Restart CoreWatch as administrator and try again.",
                3 => "Other running services depend on it. Stop those first.",
                5 => "The service cannot accept that request right now.",
                6 => "The service is not running.",
                7 => "The service did not respond in time.",
                14 => "The service is disabled. Change its startup type first.",
                _ => $"Windows returned code {code}.",
            };
            if (error != null) return error;
            string want = start ? "Running" : "Stopped";
            for (int i = 0; i < 40 && ReadState(item.Name) != want; i++) Thread.Sleep(250);
            return null;
        }
        catch (Exception ex) { return Describe(ex.InnerException ?? ex); }
    }
}
