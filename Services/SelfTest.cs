using System.IO;
using System.Text;

namespace CoreWatch.Services;

/// <summary>
/// Developer check (--selftest): verifies the parts of the newer features that can be checked without changing Windows,
/// and lists what the read-only parts see on this PC.
/// </summary>
public static class SelfTest
{
    public static async Task<string> Run()
    {
        var log = new StringBuilder();
        int failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failed++;
            log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
        }
        void Section(string title) => log.AppendLine().AppendLine("== " + title + " ==");

        // ───────── Update check ─────────
        Section("Update check");
        Check("v1.2.3 parses", UpdateService.Parse("v1.2.3") == new Version(1, 2, 3));
        Check("1.4 parses as 1.4.0", UpdateService.Parse("1.4") == new Version(1, 4, 0));
        Check("v2.0.0-beta parses", UpdateService.Parse("v2.0.0-beta") == new Version(2, 0, 0));
        Check("rubbish is rejected", UpdateService.Parse("latest") == null && UpdateService.Parse("") == null);
        Check("a higher version is newer", UpdateService.IsNewer("v99.0.0"));
        Check("the same version is not newer", !UpdateService.IsNewer("v" + UpdateService.Current.ToString(3)));
        Check("a lower version is not newer", !UpdateService.IsNewer("v0.9.0"));
        string? error = await UpdateService.Check();
        Check("GitHub answers", error == null, error ?? $"latest release {Settings.Current.LatestVersion}, this build {UpdateService.Current.ToString(3)}, update available: {UpdateService.UpdateAvailable}");

        // ───────── Power plans ─────────
        Section("Power");
        var parsed = PowerService.ParsePlans(
            "Existing Power Schemes (* Active)\r\n-----------------------------------\r\n" +
            "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *\r\n" +
            "Power Scheme GUID: 8C5E7FDA-E8BF-4A96-9A85-A6E23A8C635C  (High performance)\r\n" +
            "GUID du mode de gestion de l'alimentation : a1841308-3541-4fab-bc81-f71556f20b4a  (Économie d'énergie)\r\n");
        Check("three plans parsed", parsed.Count == 3);
        Check("active plan marked", parsed.Count == 3 && parsed[0].Active && !parsed[1].Active && parsed[0].Name == "Balanced");
        Check("GUID lower-cased, other languages work", parsed.Count == 3 && parsed[1].Guid == "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" && parsed[2].Name == "Économie d'énergie");
        try
        {
            var plans = PowerService.Plans();
            Check("this PC's plans read", plans.Count > 0 && plans.Count(p => p.Active) == 1, string.Join(", ", plans.Select(p => p.Name + (p.Active ? "*" : ""))));
            if (plans.FirstOrDefault(p => p.Active) is { } active)
                Check("re-selecting the plan already in use works", PowerService.SetActive(active) == null && PowerService.Plans().First(p => p.Active).Guid == active.Guid);
        }
        catch (Exception ex) { Check("this PC's plans read", false, ex.Message); }
        var battery = PowerService.Battery();
        log.AppendLine($"INFO  battery present: {battery.Present}, plugged in: {battery.PluggedIn}, {battery.Percent}%, health: {battery.Health?.ToString("0") ?? "n/a"}");

        // ───────── History log ─────────
        Section("History");
        var points = MetricLog.Load(DateTime.Now.AddDays(-30));
        log.AppendLine($"INFO  {points.Count} recorded minutes on disk" + (points.Count > 0 ? $", first {points[0].Time:g}, last {points[^1].Time:g}" : ""));

        // ───────── Services ─────────
        Section("Services");
        try
        {
            var services = ServiceManager.List();
            Check("services listed", services.Count > 50, $"{services.Count} services, {services.Count(s => s.Running)} running, {services.Count(s => s.ThirdParty)} not from Windows, {services.Count(s => s.Protected)} protected");
            Check("every service has a startup type", services.All(s => s.Start.Length > 0), string.Join(", ", services.Where(s => s.Start.Length == 0).Take(5).Select(s => s.Name)));
            Check("Defender and Windows Update are protected", services.Where(s => s.Name is "WinDefend" or "wuauserv" or "mpssvc").All(s => s.Protected));
            Check("RPC is protected", services.First(s => s.Name == "RpcSs").Protected);
            var spooler = services.FirstOrDefault(s => s.Name == "Spooler");
            Check("an ordinary service is not protected", spooler is { Protected: false }, spooler == null ? "Spooler not found" : $"Spooler: {spooler.StateText}, {spooler.StartText}");
            if (spooler != null)
            {
                string? refusal = Sys.IsAdmin ? null : ServiceManager.SetStart(spooler, spooler.Start == "3" ? "2" : "3");
                if (!Sys.IsAdmin)
                    Check("a change without administrator rights is refused cleanly", refusal != null && ServiceManager.ReadStart("Spooler") == spooler.Start && ServiceManager.Backup.Peek("svc:Spooler") == null, refusal ?? "no error");
            }
        }
        catch (Exception ex) { Check("services listed", false, ex.Message); }

        // ───────── Network ─────────
        Section("Network");
        try
        {
            var adapters = NetworkService.Adapters();
            Check("an adapter is found", adapters.Count > 0, string.Join("; ", adapters.Select(a => $"{a.Kind} {NetworkService.SpeedText(a.Speed)}, {a.Dns.Count} DNS servers, automatic DNS: {a.DnsAutomatic}")));
            long all = 0, real = 0;
            int allCount = 0;
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                    || nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                allCount++;
                long rx = nic.GetIPStatistics().BytesReceived;
                all += rx;
                if (adapters.Any(a => a.Id == nic.Id)) real += rx;
            }
            log.AppendLine($"INFO  {allCount} adapters reported as up, {adapters.Count} with an address; bytes received counted over all {all:N0} vs real {real:N0}");
            var (connections, listeners) = NetworkService.Sockets();
            Check("connections read", connections.Count > 0, $"{connections.Count} connections");
            Check("listening ports read", listeners.Count > 0, $"{listeners.Count} listeners");
            Check("ports are in range", connections.All(c => c.RemotePort is >= 0 and <= 65535 && c.LocalPort is > 0 and <= 65535) && listeners.All(l => l.Port is >= 0 and <= 65535));
            Check("every row has an app name", connections.All(c => c.Process.Length > 0) && listeners.All(l => l.Process.Length > 0));
            Check("a well-known port shows up", listeners.Any(l => l.Port is 135 or 445 or 5040 or 5353), string.Join(", ", listeners.Take(12).Select(l => $"{l.Protocol} {l.Port}")));
            var apps = NetworkService.ByApp(connections, listeners);
            log.AppendLine($"INFO  {apps.Count} apps hold connections; busiest holds {apps.FirstOrDefault()?.Connections ?? 0}");
        }
        catch (Exception ex) { Check("network read", false, ex.Message); }

        // ───────── Reliability ─────────
        Section("Reliability");
        try
        {
            var report = ReliabilityService.Load();
            Check("event logs read", report.Problems.Count == 0, report.Problems.Count > 0 ? string.Join("; ", report.Problems) : $"{report.Events.Count} problems in {ReliabilityService.Days} days: " +
                string.Join(", ", report.Events.GroupBy(e => e.Kind).Select(g => $"{g.Count()} {g.Key}")));
            Check("events are inside the period", report.Events.All(e => e.Time > DateTime.Now.AddDays(-ReliabilityService.Days - 1) && e.Time <= DateTime.Now.AddMinutes(5)));
            log.AppendLine($"INFO  start-up times: {report.Boots.Count}" + (report.BootNote != null ? $" ({report.BootNote})" : $", latest {report.Boots.FirstOrDefault()?.Text}"));
        }
        catch (Exception ex) { Check("event logs read", false, ex.Message); }

        // ───────── Duplicate finder ─────────
        Section("Duplicate finder");
        string folder = Path.Combine(Settings.Folder, "selftest-files");
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(Path.Combine(folder, "sub"));
            var random = new Random(3);
            byte[] big = new byte[300_000], other = new byte[300_000], small = new byte[2_000];
            random.NextBytes(big);
            random.NextBytes(small);
            Array.Copy(big, other, big.Length);
            other[^1] ^= 0xFF; // same size and same first block, different ending
            File.WriteAllBytes(Path.Combine(folder, "a.bin"), big);
            File.WriteAllBytes(Path.Combine(folder, "sub", "a copy.bin"), big);
            File.WriteAllBytes(Path.Combine(folder, "sub", "third.bin"), big);
            File.WriteAllBytes(Path.Combine(folder, "nearly.bin"), other);
            File.WriteAllBytes(Path.Combine(folder, "s1.bin"), small);
            File.WriteAllBytes(Path.Combine(folder, "s2.bin"), small);
            File.WriteAllBytes(Path.Combine(folder, "empty1.bin"), []);
            File.WriteAllBytes(Path.Combine(folder, "empty2.bin"), []);

            var (groups, _) = FileFinder.Scan(folder, 1000, new FinderProgress(), CancellationToken.None);
            Check("two sets found", groups.Count == 2, string.Join(" | ", groups.Select(g => $"{g.Files.Count} x {g.Size}")));
            Check("the three identical files are one set", groups.Any(g => g.Size == 300_000 && g.Files.Count == 3));
            Check("a file that differs only at the end is left out", !groups.SelectMany(g => g.Files).Any(f => f.Name == "nearly.bin"));
            Check("small identical files are matched", groups.Any(g => g.Size == 2_000 && g.Files.Count == 2));
            Check("empty files are ignored", !groups.SelectMany(g => g.Files).Any(f => f.Name.StartsWith("empty")));
            Check("space to free is counted once per extra copy", groups.Sum(g => g.Wasted) == 600_000 + 2_000);
            var (filtered, _) = FileFinder.Scan(folder, 100_000, new FinderProgress(), CancellationToken.None);
            Check("the size limit is respected", filtered.Count == 1 && filtered[0].Size == 300_000);
            var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Check("a cancelled scan returns nothing", FileFinder.Scan(folder, 1000, new FinderProgress(), cancelled.Token).Groups.Count == 0);
        }
        catch (Exception ex) { Check("duplicate finder", false, ex.Message); }
        finally { try { Directory.Delete(folder, true); } catch { } }

        ExtraTests(Check, Section, log);

        log.Insert(0, (failed == 0 ? "ALL CHECKS PASSED" : $"{failed} CHECK(S) FAILED") + $"  ·  administrator: {Sys.IsAdmin}" + Environment.NewLine);
        return log.ToString();
    }

    /// <summary>Checks for the app installer, preinstalled apps and tweak profiles.</summary>
    static void ExtraTests(Action<string, bool, string> check, Action<string> section, StringBuilder log)
    {
        // ───────── winget updates ─────────
        section("App updates");
        string[] sample =
        [
            "   - ",
            "Name                              Id                         Version       Available     Source",
            "-----------------------------------------------------------------------------------------------",
            "Mozilla Firefox (x64 en-US)       Mozilla.Firefox            131.0.2       132.0         winget",
            "Visual Studio Code with a very l… Microsoft.VisualStudioCode 1.94.0        1.95.1        winget",
            "Some Tool                         Vendor.Tool                Unknown       2.0           winget",
            "3 upgrades available.",
            "",
            "The following packages have an upgrade available, but require explicit targeting for upgrade:",
            "Name    Id              Version  Available Source",
            "-------------------------------------------------",
            "Discord Discord.Discord 1.0.9163 1.0.9170  winget",
        ];
        var parsed = WingetService.ParseUpgrades(sample);
        check("four updates parsed from two tables", parsed.Count == 4, string.Join(" | ", parsed.Select(p => $"{p.Id} {p.Current}>{p.Available}")));
        check("ids and versions land in the right columns", parsed.Count == 4 && parsed[0] is { Id: "Mozilla.Firefox", Current: "131.0.2", Available: "132.0" } && parsed[3] is { Id: "Discord.Discord", Available: "1.0.9170" }, "");
        check("a shortened name loses its ellipsis", parsed.Count == 4 && parsed[1].Name == "Visual Studio Code with a very l" && parsed[1].Id == "Microsoft.VisualStudioCode", "");
        check("the summary line is not taken for an app", !parsed.Any(p => p.Name.StartsWith("3 upgrades")), "");
        check("output with no table gives no updates", WingetService.ParseUpgrades(["No installed package found matching input criteria."]).Count == 0, "");
        if (WingetService.Exe != null)
        {
            try
            {
                var live = WingetService.Upgrades().GetAwaiter().GetResult();
                check("this PC's updates read", live.All(u => u.Id.Contains('.') && !u.Id.Contains(' ') && u.Available.Length > 0), $"{live.Count} updates: " + string.Join(", ", live.Take(8).Select(u => $"{u.Id} {u.Current}>{u.Available}")));
            }
            catch (Exception ex) { check("this PC's updates read", false, ex.Message); }
        }

        // ───────── Preinstalled apps ─────────
        section("Preinstalled apps");
        const string json = """
            [{"Name":"Microsoft.BingWeather","PackageFullName":"Microsoft.BingWeather_4.53_x64__8wekyb3d8bbwe","PackageFamilyName":"Microsoft.BingWeather_8wekyb3d8bbwe","NonRemovable":false,"IsFramework":false},
             {"Name":"Microsoft.WindowsStore","PackageFullName":"Microsoft.WindowsStore_1_x64__8wekyb3d8bbwe","PackageFamilyName":"Microsoft.WindowsStore_8wekyb3d8bbwe","NonRemovable":false,"IsFramework":false},
             {"Name":"Microsoft.VCLibs.140.00","PackageFullName":"Microsoft.VCLibs_14_x64__8wekyb3d8bbwe","PackageFamilyName":"Microsoft.VCLibs_8wekyb3d8bbwe","NonRemovable":false,"IsFramework":true},
             {"Name":"Microsoft.GetHelp","PackageFullName":"Microsoft.GetHelp_10_x64__8wekyb3d8bbwe","PackageFamilyName":"Microsoft.GetHelp_8wekyb3d8bbwe","NonRemovable":true,"IsFramework":false},
             {"Name":"king.com.CandyCrushSaga","PackageFullName":"king.com.CandyCrushSaga_1_x86__kgqvnymyfvs32","PackageFamilyName":"king.com.CandyCrushSaga_kgqvnymyfvs32","NonRemovable":false,"IsFramework":false},
             {"Name":"Microsoft.Paint","PackageFullName":"Microsoft.Paint_11_x64__8wekyb3d8bbwe","PackageFamilyName":"Microsoft.Paint_8wekyb3d8bbwe","NonRemovable":false,"IsFramework":false}]
            """;
        var apps = AppxService.Parse(json);
        check("only listed, removable apps are offered", apps.Count == 2 && apps.Any(a => a.Name == "Weather") && apps.Any(a => a.Name == "CandyCrushSaga"), string.Join(", ", apps.Select(a => a.Name)));
        check("the Store, Paint, frameworks and non-removable apps are left out", !apps.Any(a => a.PackageName is "Microsoft.WindowsStore" or "Microsoft.Paint" or "Microsoft.VCLibs.140.00" or "Microsoft.GetHelp"), "");
        check("a single app (not a list) is understood", AppxService.Parse("""{"Name":"Microsoft.Todos","PackageFullName":"Microsoft.Todos_2_x64__8wekyb3d8bbwe","PackageFamilyName":"Microsoft.Todos_8wekyb3d8bbwe","NonRemovable":false,"IsFramework":false}""").Count == 1, "");
        check("the Store link carries the family name", AppxService.StoreLink("Microsoft.BingWeather_8wekyb3d8bbwe") == "ms-windows-store://pdp/?PFN=Microsoft.BingWeather_8wekyb3d8bbwe", "");
        try
        {
            var live = AppxService.List();
            check("this PC's preinstalled apps read", live.All(a => a.FullName.Length > 0 && a.FamilyName.Length > 0), $"{live.Count} offered: " + string.Join(", ", live.Select(a => a.Name)));
        }
        catch (Exception ex) { check("this PC's preinstalled apps read", false, ex.Message); }

        // ───────── Tweak profiles ─────────
        section("Tweak profiles");
        var tweaks = TweakService.Build();
        foreach (var tweak in tweaks) tweak.Update(TweakService.Read(tweak));
        string profile = TweakProfile.Export(tweaks);
        var ids = TweakProfile.Parse(profile);
        var off = tweaks.Where(t => t.Available && !t.IsOn).Select(t => t.Id).ToList();
        check("a saved profile reads back the same", ids != null && ids.SequenceEqual(off), $"{off.Count} off: {string.Join(", ", off)}");
        check("other JSON is rejected", TweakProfile.Parse("{\"hello\":1}") == null && TweakProfile.Parse("not json") == null && TweakProfile.Parse("{\"kind\":\"tweak-profile\"}") == null, "");
        check("a hand-written profile is accepted", TweakProfile.Parse("{\"kind\":\"tweak-profile\",\"off\":[\"tips\",\"tips\",\"\"]}") is { Count: 1 }, "");

        // ───────── Alerts and schedule ─────────
        section("Background");
        var noon = new DateTime(2026, 6, 10, 12, 0, 0);
        TimeSpan idle = TimeSpan.FromMinutes(5), busy = TimeSpan.FromSeconds(20);
        check("off is never due", !MaintenanceService.IsDue("off", DateTime.MinValue, noon, idle) && !MaintenanceService.IsDue("", DateTime.MinValue, noon, idle), "");
        check("daily is due after a day of waiting", MaintenanceService.IsDue("daily", noon.AddHours(-25), noon, idle) && !MaintenanceService.IsDue("daily", noon.AddHours(-23), noon, idle), "");
        check("weekly is due after a week of waiting", MaintenanceService.IsDue("weekly", noon.AddDays(-7.1), noon, idle) && !MaintenanceService.IsDue("weekly", noon.AddDays(-6.9), noon, idle), "");
        check("nothing runs while the PC is in use", !MaintenanceService.IsDue("daily", noon.AddDays(-3), noon, busy), "");
        string runLog = Path.Combine(Settings.Folder, "selftest-maintenance.json");
        try
        {
            File.Delete(runLog);
            var report = new RefreshReport([new RefreshStep("Memory", "1.2 GB released", true), new RefreshStep("DNS cache", "Skipped", false)], "1.2 GB of memory released");
            for (int i = 0; i < 53; i++) MaintenanceService.Record(report, automatic: i % 2 == 0, runLog);
            var runs = MaintenanceService.History(runLog);
            check("the run log keeps the newest 50", runs.Count == 50, $"{runs.Count} kept");
            check("a run reads back as written", runs.Count > 0 && runs[0] is { Headline: "1.2 GB of memory released", Automatic: true, Steps.Count: 2 } && runs[0].Steps[0] == "Memory: 1.2 GB released", "");
        }
        catch (Exception ex) { check("the run log", false, ex.Message); }
        finally { try { File.Delete(runLog); } catch { } }
        log.AppendLine($"INFO  start with Windows: {AutoStart.Enabled}, close to tray: {Settings.Current.CloseToTray}, scheduled refresh: {Settings.Current.AutoRefresh}, idle for {Native.IdleTime().TotalSeconds:0}s");
        log.AppendLine($"INFO  {MaintenanceService.History().Count} refresh runs on record");
    }
}
