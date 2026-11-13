using System.Windows;
using System.Windows.Controls;
using CoreWatch.Services;

namespace CoreWatch.Pages;

public sealed record RepairRow(string Id, string Glyph, string Name, string Description, string Action, bool NeedsAdmin, string? Note = null)
{
    public bool Locked => NeedsAdmin && !Sys.IsAdmin;
    public bool Enabled => !Locked;
    public bool HasNote => Note != null;
}

public sealed record PanelRow(string Glyph, string Name, string File, string Args = "")
{
    public string Tip => (File + " " + Args).Trim();
}

public partial class ToolboxPage : UserControl, IPage
{
    CancellationTokenSource? _running;

    public ToolboxPage()
    {
        InitializeComponent();

        RepairList.ItemsSource = new List<RepairRow>
        {
            new("restore-point", "", "Create a restore point",
                "Saves Windows' current system files and settings so you can roll back from System Restore. Worth doing before big changes. Windows keeps at most one per day.",
                "Create", true),
            new("system-files", "", "Check and repair system files",
                "Runs DISM and then the System File Checker, which replace damaged Windows files with good copies. Use it when Windows features crash or misbehave. Your files and apps are not touched.",
                "Run", true, "10 to 30 minutes"),
            new("network", "", "Reset the network stack",
                "Resets Winsock, TCP/IP and the proxy setting and flushes DNS. Fixes connections that are stuck or half-working. Manually entered IP addresses and some VPN adapters have to be set up again afterwards.",
                "Reset", true, "Restart to finish"),
            new("explorer", "", "Restart Windows Explorer",
                "Restarts the taskbar, Start menu and desktop when they freeze or stop redrawing. Open File Explorer windows close; your apps keep running.",
                "Restart", false),
        };
        AdminBar.Visibility = Sys.IsAdmin ? Visibility.Collapsed : Visibility.Visible;

        PanelList.ItemsSource = new List<PanelRow>
        {
            new("", "Control Panel", "control.exe"),
            new("", "Programs and Features", "appwiz.cpl"),
            new("", "Network Connections", "ncpa.cpl"),
            new("", "Power Options", "powercfg.cpl"),
            new("", "Sound", "mmsys.cpl"),
            new("", "System Properties", "sysdm.cpl"),
            new("", "User Accounts", "netplwiz.exe"),
            new("", "Region", "intl.cpl"),
            new("", "Printers", "control.exe", "printers"),
            new("", "Device Manager", "devmgmt.msc"),
            new("", "Disk Management", "diskmgmt.msc"),
            new("", "Services", "services.msc"),
            new("", "Event Viewer", "eventvwr.msc"),
            new("", "Task Scheduler", "taskschd.msc"),
            new("", "Computer Management", "compmgmt.msc"),
            new("", "System Information", "msinfo32.exe"),
            new("", "Resource Monitor", "resmon.exe"),
            new("", "Windows Features", "OptionalFeatures.exe"),
            new("", "Registry Editor", "regedit.exe"),
            new("", "Windows Security", "windowsdefender:"),
        };
    }

    void Panel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is PanelRow panel) ToolboxService.Open(panel.File, panel.Args);
    }

    void Admin_Click(object sender, RoutedEventArgs e)
    {
        if (!Sys.RestartAsAdmin("toolbox")) MainWindow.Instance.Toast("CoreWatch was not restarted.", true);
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => _running?.Cancel();

    async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not RepairRow row || row.Locked) return;
        if (_running != null) { MainWindow.Instance.Toast("Another repair is still running.", true); return; }
        var toast = MainWindow.Instance;

        switch (row.Id)
        {
            case "restore-point":
                _running = new CancellationTokenSource();
                try
                {
                    toast.Toast("Asking Windows for a restore point…");
                    string? error = await Task.Run(() => ToolboxService.CreateRestorePoint("CoreWatch restore point"));
                    if (error != null) toast.Toast(error, true);
                    else toast.Toast("Restore point requested. If Windows already made one today, it keeps that one instead.");
                }
                finally { _running = null; }
                break;

            case "system-files":
                if (!await toast.Confirm("Check and repair system files?",
                        "DISM and the System File Checker run one after the other. This usually takes 10 to 30 minutes and can be cancelled. Keep CoreWatch open until it finishes.", "Run"))
                    return;
                await RunWithLog("Checking system files", ct => ToolboxService.RepairSystemFiles(Log.Append, ct),
                    "System file check finished. The output shows whether anything was repaired.", "The system file check reported a problem. See the output for details.");
                break;

            case "network":
                if (!await toast.Confirm("Reset the network stack?",
                        "You will be disconnected briefly and need to restart Windows to finish.\n\nManually entered IP addresses and DNS servers are cleared, and some VPN or virtual network adapters may need reinstalling.", "Reset", danger: true))
                    return;
                await RunWithLog("Resetting the network stack", ct => ToolboxService.ResetNetwork(Log.Append, ct),
                    "Network stack reset. Restart Windows to finish.", "Part of the network reset failed. See the output for details.");
                break;

            case "explorer":
                if (!await toast.Confirm("Restart Windows Explorer?", "The taskbar and desktop disappear for a moment and open File Explorer windows close. Your apps keep running.", "Restart"))
                    return;
                try { await Task.Run(ToolboxService.RestartExplorer); toast.Toast("Windows Explorer was restarted."); }
                catch (Exception ex) { toast.Toast("Explorer could not be restarted: " + ex.Message, true); }
                break;
        }
    }

    async Task RunWithLog(string title, Func<CancellationToken, Task<bool>> work, string success, string failure)
    {
        var cts = _running = new CancellationTokenSource();
        LogPanel.Visibility = Visibility.Visible;
        LogTitle.Text = title + "…";
        CancelButton.Visibility = Visibility.Visible;
        Busy.Visibility = Visibility.Visible;
        Busy.IsIndeterminate = true;
        Log.Clear();
        LogPanel.BringIntoView();
        try
        {
            bool ok = await work(cts.Token);
            if (cts.IsCancellationRequested) { Log.Append("Cancelled."); MainWindow.Instance.Toast("Cancelled.", true); }
            else MainWindow.Instance.Toast(ok ? success : failure, !ok);
        }
        catch (Exception ex)
        {
            Log.Append(ex.Message);
            MainWindow.Instance.Toast("It could not be run: " + ex.Message, true);
        }
        finally
        {
            cts.Dispose();
            _running = null;
            LogTitle.Text = "Output";
            CancelButton.Visibility = Visibility.Collapsed;
            Busy.IsIndeterminate = false;
            Busy.Visibility = Visibility.Collapsed;
        }
    }
}
