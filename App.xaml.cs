using System.IO;
using System.Windows;
using System.Windows.Threading;
using CoreWatch.Services;

namespace CoreWatch;

public partial class App : Application
{
    /// <summary>Folder to write page screenshots into (developer switch: --shots &lt;folder&gt;).</summary>
    public static string? ShotsFolder { get; private set; }
    public static string? StartPage { get; private set; }
    /// <summary>Developer switches: --scan &lt;folder&gt; analyzes a folder on the Storage page, --analyze runs the (read-only) junk analysis.</summary>
    public static string? DevScanPath { get; private set; }
    public static bool DevAnalyze { get; private set; }
    /// <summary>Developer switch: --refresh-preview shows the Refresh card on Overview without changing anything.</summary>
    public static bool DevRefreshPreview { get; private set; }
    /// <summary>Developer switch: --refresh-run &lt;folder&gt; runs the real Refresh as the button would, saves a report and screenshot, and exits.</summary>
    public static string? DevRefreshRun { get; private set; }
    /// <summary>Developer switch: --tweak-toggle &lt;id&gt; &lt;folder&gt; flips one feature on the Tweaks page, saves a report and screenshot, and exits.</summary>
    public static (string Id, string Folder)? DevTweak { get; private set; }
    /// <summary>Developer switches: --reg-fix &lt;category&gt; &lt;folder&gt; fixes one category on the Registry page; --reg-restore &lt;folder&gt; restores the newest backup. Both save a report and screenshots, then exit.</summary>
    public static (string? Category, string Folder)? DevRegistry { get; private set; }
    /// <summary>Developer switch: --dup-scan &lt;folder&gt; scans that folder on the Duplicates page.</summary>
    public static string? DevDuplicatePath { get; private set; }
    /// <summary>Developer switch: --history-demo charts generated readings (nothing is saved) so the History page can be checked without waiting days.</summary>
    public static bool DevHistoryDemo { get; private set; }
    /// <summary>Developer switch: --tab &lt;name&gt; opens a page on a second view ("preinstalled" on Apps, "updates" on Install).</summary>
    public static string? DevTab { get; private set; }

    /// <summary>True for developer runs: no tray icon, no single-instance check and no background jobs.</summary>
    public static bool DevMode { get; private set; }
    /// <summary>True once the user chose Exit, so closing the window really closes the app.</summary>
    public static bool Exiting { get; private set; }
    public static TrayService? Tray { get; private set; }

    Mutex? _single;
    EventWaitHandle? _wake;
    int _tipTick;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        bool startHidden = false, restarted = false;
        for (int i = 0; i < e.Args.Length; i++)
        {
            string? next = i + 1 < e.Args.Length ? e.Args[i + 1] : null;
            if (e.Args[i] == "--shots") ShotsFolder = next;
            if (e.Args[i] == "--page") StartPage = next;
            if (e.Args[i] == "--scan") DevScanPath = next;
            if (e.Args[i] == "--analyze") DevAnalyze = true;
            if (e.Args[i] == "--refresh-preview") DevRefreshPreview = true;
            if (e.Args[i] == "--refresh-run" && next != null) DevRefreshRun = next;
            if (e.Args[i] == "--dup-scan" && next != null) DevDuplicatePath = next;
            if (e.Args[i] == "--history-demo") DevHistoryDemo = true;
            if (e.Args[i] == "--tab") DevTab = next;
            if (e.Args[i] == "--dev") DevMode = true;
            if (e.Args[i] == "--tray") startHidden = true;
            if (e.Args[i] == "--restarted") restarted = true;
            if (e.Args[i] == "--catalog-check" && next != null)
            {
                // Developer: asks winget whether every app in the Install catalogue exists (read-only), writes the report and exits.
                File.WriteAllText(next, Task.Run(WingetService.CheckCatalog).GetAwaiter().GetResult());
                Shutdown();
                return;
            }
            if (e.Args[i] == "--tweak-toggle" && i + 2 < e.Args.Length) DevTweak = (e.Args[i + 1], e.Args[i + 2]);
            if (e.Args[i] == "--reg-fix" && i + 2 < e.Args.Length) DevRegistry = (e.Args[i + 1], e.Args[i + 2]);
            if (e.Args[i] == "--reg-restore" && next != null) DevRegistry = (null, next);
            if (e.Args[i] is "--reg-selftest" or "--reg-scan" && next != null)
            {
                // Developer: --reg-selftest backs up, removes and restores CoreWatch's own test key;
                // --reg-scan lists what the registry scan finds without changing anything.
                File.WriteAllText(next, e.Args[i] == "--reg-selftest"
                    ? RegistryFixer.SelfTest()
                    : string.Join(Environment.NewLine, RegistryScanner.Scan().Select(x => $"{x.Category.Id,-14} {x.Location}\n               {x.Title}: {x.Reason}")));
                Shutdown();
                return;
            }
            if (e.Args[i] == "--tweak-selftest" && next != null)
            {
                // Developer: checks switch-off and undo against CoreWatch's own registry key, writes the report and exits.
                File.WriteAllText(next, TweakService.SelfTest());
                Shutdown();
                return;
            }
            if (e.Args[i] == "--selftest" && next != null)
            {
                // Developer: checks the parts of the newer features that can be verified without changing Windows, and lists what they read.
                File.WriteAllText(next, Task.Run(SelfTest.Run).GetAwaiter().GetResult());
                Shutdown();
                return;
            }
        }
        DevMode |= ShotsFolder != null || DevScanPath != null || DevAnalyze || DevRefreshPreview || DevRefreshRun != null
                   || DevTweak != null || DevRegistry != null || DevDuplicatePath != null || DevHistoryDemo || DevTab != null;

        if (!DevMode && !IsFirstInstance(restarted))
        {
            Shutdown();
            return;
        }

        SystemMonitor.Instance.Start(Dispatcher);
        var window = new MainWindow();
        MainWindow = window;
        if (!DevMode) StartBackground();
        if (!(startHidden && Tray != null)) window.Show();
    }

    /// <summary>
    /// False when another copy is already running: that copy is asked to show its window instead.
    /// <paramref name="waitForPrevious"/> covers "Restart as administrator", where the old copy is still closing.
    /// </summary>
    bool IsFirstInstance(bool waitForPrevious)
    {
        const string mutexName = @"Local\CoreWatch.SingleInstance", wakeName = @"Local\CoreWatch.Show";
        try
        {
            _single = new Mutex(true, mutexName, out bool owned);
            if (!owned && waitForPrevious)
            {
                try { owned = _single.WaitOne(6000); }
                catch (AbandonedMutexException) { owned = true; }
            }
            if (!owned)
            {
                using var wake = EventWaitHandle.OpenExisting(wakeName);
                wake.Set();
                return false;
            }

            _wake = new EventWaitHandle(false, EventResetMode.AutoReset, wakeName);
            var wakeHandle = _wake;
            new Thread(() =>
            {
                try { while (wakeHandle.WaitOne()) Dispatcher.BeginInvoke(ShowMainWindow); }
                catch { }
            }) { IsBackground = true, Name = "CoreWatch.Wake" }.Start();
            return true;
        }
        catch { return true; } // cannot tell (for example the other copy runs elevated): start normally
    }

    void StartBackground()
    {
        try { Tray = new TrayService(ShowMainWindow, RefreshFromTray, ExitFromTray); }
        catch { Tray = null; }

        AlertService.Start();
        AlertService.Raised += alert =>
        {
            Tray?.Notify(alert.Title, alert.Detail, warning: true);
            if (MainWindow is MainWindow { IsVisible: true } w) w.Toast($"{alert.Title}. {alert.Detail}", true);
        };

        MaintenanceService.Start();
        MaintenanceService.Finished += run =>
        {
            if (run.Automatic) Tray?.Notify("Scheduled refresh finished", run.Headline);
        };

        MetricLog.Start();
        _ = UpdateService.CheckIfDue();

        SystemMonitor.Instance.Updated += s =>
        {
            if (Tray == null || _tipTick++ % 3 != 0) return;
            Tray.SetTip($"CoreWatch  ·  CPU {Fmt.Pct(s.CpuTotal)}  ·  RAM {Fmt.Pct(s.MemPct)}" + (s.PrimaryGpu is { } g ? $"  ·  GPU {Fmt.Pct(g.Utilization)}" : ""));
        };
    }

    public void ShowMainWindow()
    {
        if (MainWindow is not { } w) return;
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    async void RefreshFromTray()
    {
        var run = await MaintenanceService.RunNow(automatic: false);
        Tray?.Notify(run != null ? "Refresh complete" : "A refresh is already running", run?.Headline ?? "It will finish shortly.");
    }

    void ExitFromTray()
    {
        Exiting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Tray?.Dispose();
        if (!DevMode) MetricLog.Stop();
        SystemMonitor.Instance.Stop();
        Settings.Current.Save();
        base.OnExit(e);
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.AppendAllText(Path.Combine(Settings.Folder, "error.log"), $"[{DateTime.Now:u}] {e.Exception}\n\n");
        }
        catch { }
        (Current.MainWindow as MainWindow)?.Toast("Something went wrong: " + e.Exception.Message, true);
        e.Handled = true;
    }
}
