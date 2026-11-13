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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        for (int i = 0; i < e.Args.Length; i++)
        {
            string? next = i + 1 < e.Args.Length ? e.Args[i + 1] : null;
            if (e.Args[i] == "--shots") ShotsFolder = next;
            if (e.Args[i] == "--page") StartPage = next;
            if (e.Args[i] == "--scan") DevScanPath = next;
            if (e.Args[i] == "--analyze") DevAnalyze = true;
            if (e.Args[i] == "--refresh-preview") DevRefreshPreview = true;
            if (e.Args[i] == "--refresh-run" && next != null) DevRefreshRun = next;
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
        }

        SystemMonitor.Instance.Start(Dispatcher);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
