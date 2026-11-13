using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CoreWatch.Controls;
using CoreWatch.Services;

namespace CoreWatch.Pages;

public sealed record DriveRow(string Title, string Detail, double Pct, Brush Bar, string Path);

public sealed record Insight(string Glyph, Brush Tone, string Title, string Detail, string? Route)
{
    public Visibility ChevronVisibility => Route == null ? Visibility.Collapsed : Visibility.Visible;
}

public partial class DashboardPage : UserControl, IPage
{
    readonly TextBlock _cpuName, _gpuName, _ram, _board, _os, _uptime;
    string _driveSignature = "", _insightSignature = "";
    int _tick;

    public DashboardPage()
    {
        InitializeComponent();
        var mon = SystemMonitor.Instance;
        var h = mon.History;

        _cpuName = Ui.AddKeyValue(SystemPanel, "Processor", mon.Info.CpuName);
        _gpuName = Ui.AddKeyValue(SystemPanel, "Graphics");
        _ram = Ui.AddKeyValue(SystemPanel, "Memory", Fmt.Bytes(mon.Info.InstalledRam, 0) + " installed");
        _board = Ui.AddKeyValue(SystemPanel, "Motherboard", mon.Info.Board.Length > 0 ? mon.Info.Board : "—");
        _os = Ui.AddKeyValue(SystemPanel, "Windows", $"{mon.Info.OsName} {mon.Info.OsVersion}");
        _uptime = Ui.AddKeyValue(SystemPanel, "Uptime");

        NetDownChart.SetSeries((h.NetDown, Ui.Color("C.Net"), true));
        NetUpChart.SetSeries((h.NetUp, Ui.Color("C.Cpu"), true));
        DiskReadChart.SetSeries((h.DiskRead, Ui.Color("C.Disk"), true));
        DiskWriteChart.SetSeries((h.DiskWrite, Ui.Color("C.Vram"), true));
    }

    bool _previewed;

    public void OnShown()
    {
        _tick = 0;
        if (App.DevRefreshPreview && !_previewed) { _previewed = true; _ = RunRefresh(preview: true); }
    }

    public void OnTick(Snapshot s)
    {
        var mon = SystemMonitor.Instance;
        var gpu = s.PrimaryGpu;
        SubTitle.Text = $"{mon.Info.MachineName}  ·  {mon.Info.OsName}  ·  up {Fmt.Duration(s.Uptime)}";

        CpuGauge.Set(s.CpuTotal, $"{s.CpuMhz / 1000:0.00} GHz", $"{s.ProcessCount} processes");
        MemGauge.Set(s.MemPct, $"{Fmt.Gb(s.MemUsed)} of {Fmt.Gb(s.MemTotal)} GB", $"{Fmt.Bytes(s.MemAvail)} available");

        if (gpu != null)
        {
            string sensors = gpu.Temperature is { } t ? $"{t:0}°C" + (gpu.PowerWatts is { } w ? $"  ·  {w:0} W" : "") : gpu.Vendor;
            GpuGauge.Set(gpu.Utilization, ShortGpuName(gpu.Name), sensors);
            VramGauge.Set(gpu.DedicatedPct, $"{Fmt.Gb(gpu.DedicatedUsed)} of {Fmt.Gb(gpu.DedicatedTotal)} GB", $"{Fmt.Bytes(gpu.SharedUsed)} shared");
            _gpuName.Text = gpu.Name;
        }
        else
        {
            GpuGauge.Set(0, "No GPU detected", "", "—");
            VramGauge.Set(0, "—", "", "—");
        }

        string sysRoot = System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var sysDrive = s.Drives.FirstOrDefault(d => d.Name.Equals(sysRoot, StringComparison.OrdinalIgnoreCase)) ?? s.Drives.FirstOrDefault();
        if (sysDrive != null)
        {
            DiskGauge.Title = $"System drive ({sysDrive.Name.TrimEnd('\\')})";
            DiskGauge.Color = Ui.Color(sysDrive.UsedPct >= 92 ? "C.Bad" : "C.Disk");
            DiskGauge.Set(sysDrive.UsedPct, $"{Fmt.Bytes(sysDrive.Free)} free", $"of {Fmt.Bytes(sysDrive.Total)}");
        }

        // Activity chart (series are bound lazily because GPU history only exists once a GPU is seen)
        if (_tick == 0)
        {
            var series = new List<(RingBuffer, Color, bool)> { (mon.History.Cpu, Ui.Color("C.Cpu"), true), (mon.History.Mem, Ui.Color("C.Mem"), false) };
            if (gpu != null) series.Add((mon.History.Gpu(gpu.Luid).Util, Ui.Color("C.Gpu"), false));
            ActivityChart.SetSeries([.. series]);
        }
        ActivityChart.Refresh();
        LegendCpu.Text = $"CPU {Fmt.Pct(s.CpuTotal)}";
        LegendMem.Text = $"Memory {Fmt.Pct(s.MemPct)}";
        LegendGpu.Text = gpu != null ? $"GPU {Fmt.Pct(gpu.Utilization)}" : "GPU —";
        ChartSpan.Text = $"Last {History.Size * Settings.Current.RefreshMs / 1000} seconds";

        NetDown.Text = Fmt.Rate(s.NetDown);
        NetUp.Text = Fmt.Rate(s.NetUp);
        DiskRead.Text = Fmt.Rate(s.DiskRead);
        DiskWrite.Text = Fmt.Rate(s.DiskWrite);
        NetDownChart.Refresh();
        NetUpChart.Refresh();
        DiskReadChart.Refresh();
        DiskWriteChart.Refresh();

        _uptime.Text = Fmt.Duration(s.Uptime);

        string sig = string.Join("|", s.Drives.Select(d => $"{d.Name}{d.Free >> 26}"));
        if (sig != _driveSignature)
        {
            _driveSignature = sig;
            DriveList.ItemsSource = s.Drives.Select(ToRow).ToList();
        }

        if (_tick % 5 == 0) UpdateInsights(s);
        _tick++;
    }

    public static DriveRow ToRow(DriveSample d)
    {
        string letter = d.Name.TrimEnd('\\');
        string label = d.Label.Length > 0 ? d.Label : d.Type == System.IO.DriveType.Removable ? "Removable drive" : "Local disk";
        return new DriveRow($"{label} ({letter})", $"{Fmt.Bytes(d.Free)} free of {Fmt.Bytes(d.Total)}", d.UsedPct, Ui.UsageBrush(d.UsedPct, "B.Disk"), d.Name);
    }

    static string ShortGpuName(string name) =>
        name.Replace("NVIDIA ", "").Replace("GeForce ", "").Replace("AMD ", "").Replace("(TM)", "").Replace("(R)", "").Replace("Intel ", "").Trim();

    void UpdateInsights(Snapshot s)
    {
        var list = new List<Insight>();
        Brush bad = Ui.Brush("B.Bad"), warn = Ui.Brush("B.Warn"), info = Ui.Brush("B.Accent"), good = Ui.Brush("B.Good");

        foreach (var d in s.Drives.Where(d => d.UsedPct >= 82).OrderByDescending(d => d.UsedPct))
            list.Add(new Insight("\uEDA2", d.UsedPct >= 92 ? bad : warn, $"Drive {d.Name.TrimEnd('\\')} is {d.UsedPct:0}% full",
                $"Only {Fmt.Bytes(d.Free)} left. See what is taking the space.", "storage"));

        if (s.MemPct >= 85)
            list.Add(new Insight("\uE950", s.MemPct >= 93 ? bad : warn, $"Memory is {s.MemPct:0}% used",
                "Close heavy apps or trim working sets to free some up.", "memory"));

        if (s.CommitLimit > 0 && 100.0 * s.CommitTotal / s.CommitLimit >= 90)
            list.Add(new Insight("\uE7BA", warn, "Commit charge is near its limit",
                $"{Fmt.Bytes(s.CommitTotal)} of {Fmt.Bytes(s.CommitLimit)} committed. Apps may fail to allocate memory.", "memory"));

        if (s.PrimaryGpu is { Temperature: >= 83 } hot)
            list.Add(new Insight("\uE7F4", warn, $"GPU is running hot ({hot.Temperature:0}°C)", "Check airflow and fan curves.", "graphics"));

        if (s.PrimaryGpu is { DedicatedPct: >= 90 } full)
            list.Add(new Insight("\uE7F4", warn, $"GPU memory is {full.DedicatedPct:0}% used", "See which apps are holding video memory.", "graphics"));

        var bin = new Native.SHQUERYRBINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.SHQUERYRBINFO>() };
        if (Native.SHQueryRecycleBin(null, ref bin) == 0 && bin.i64Size >= 500L << 20)
            list.Add(new Insight("\uE74D", info, $"Recycle Bin holds {Fmt.Bytes(bin.i64Size)}", $"{Fmt.Count(bin.i64NumItems)} deleted items can be removed for good.", "cleaner"));

        if (CleanerPage.LastScanBytes > 100L << 20)
            list.Add(new Insight("\uEA99", info, $"{Fmt.Bytes(CleanerPage.LastScanBytes)} of junk found", "Review and clean it in Cleaner.", "cleaner"));

        if (s.Uptime.TotalDays >= 7)
            list.Add(new Insight("\uE72C", info, $"Running for {(int)s.Uptime.TotalDays} days", "A restart clears leaked memory and finishes pending updates.", null));

        if (list.Count == 0)
            list.Add(new Insight("\uE930", good, "Everything looks healthy", "No storage, memory or temperature warnings right now.", null));
        if (list.Count < 4)
            list.Add(new Insight("\uE7E8", Ui.Brush("B.Dim"), "Review startup apps", "Fewer apps at sign-in means a faster boot.", "startup"));

        string sig = string.Join("|", list.Take(5).Select(i => i.Title));
        if (sig == _insightSignature) return;
        _insightSignature = sig;
        InsightList.ItemsSource = list.Take(5).ToList();
    }

    void Insight_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string route) MainWindow.Instance.Navigate(route);
    }

    void ScanJunk_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.Instance.Navigate("cleaner");
        MainWindow.Instance.GetPage<CleanerPage>("cleaner")?.StartScan();
    }

    async void FreeMemory_Click(object sender, RoutedEventArgs e)
    {
        FreeMemoryButton.IsEnabled = false;
        try
        {
            string result = await MemoryPage.RunTrim();
            MainWindow.Instance.Toast(result);
        }
        finally { FreeMemoryButton.IsEnabled = true; }
    }

    // ───────── One-click refresh ─────────
    async void Refresh_Click(object sender, RoutedEventArgs e) => await RunRefresh(preview: false);

    /// <summary>Runs the refresh and shows its progress and results in the card under the header.</summary>
    public async Task<RefreshReport?> RunRefresh(bool preview)
    {
        RefreshReport? report = null;
        if (!RefreshButton.IsEnabled) return null;
        RefreshButton.IsEnabled = FreeMemoryButton.IsEnabled = false;
        RefreshCard.Visibility = Visibility.Visible;
        RefreshClose.Visibility = Visibility.Collapsed;
        RefreshSteps.ItemsSource = null;
        RefreshBusy.Visibility = Visibility.Visible;
        RefreshBusy.IsIndeterminate = true;
        RefreshIcon.Text = "";
        RefreshIcon.Foreground = Ui.Brush("B.Accent");
        RefreshTitle.Text = "Refreshing your PC…";
        try
        {
            report = await RefreshService.Run(stage => RefreshDetail.Text = stage, preview);
            RefreshIcon.Text = "";
            RefreshIcon.Foreground = Ui.Brush("B.Good");
            RefreshTitle.Text = preview ? "Refresh preview" : "Refresh complete";
            RefreshDetail.Text = report.Headline;
            Brush done = Ui.Brush("B.Good"), skipped = Ui.Brush("B.Faint");
            RefreshSteps.ItemsSource = report.Steps.Select(s => new RefreshRow(s.Done ? "" : "", s.Done ? done : skipped, s.Name, s.Result)).ToList();
        }
        catch (Exception ex)
        {
            RefreshIcon.Text = "";
            RefreshIcon.Foreground = Ui.Brush("B.Warn");
            RefreshTitle.Text = "Refresh stopped";
            RefreshDetail.Text = ex.Message;
        }
        finally
        {
            RefreshBusy.IsIndeterminate = false;
            RefreshBusy.Visibility = Visibility.Collapsed;
            RefreshClose.Visibility = Visibility.Visible;
            RefreshButton.IsEnabled = FreeMemoryButton.IsEnabled = true;
        }
        return report;
    }

    void RefreshClose_Click(object sender, RoutedEventArgs e) => RefreshCard.Visibility = Visibility.Collapsed;
}

public sealed record RefreshRow(string Mark, Brush Tone, string Name, string Result);
