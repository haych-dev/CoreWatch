using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CoreWatch.Controls;
using CoreWatch.Services;

namespace CoreWatch.Pages;

public sealed record LegendItem(string Name, string Value, Brush Brush, string Tip);
public sealed record ConsumerRow(string Name, string CountText, double Pct, string SizeText);

public partial class MemoryPage : UserControl, IPage
{
    readonly TextBlock _commit, _cached, _paged, _nonPaged, _pageFile, _counts;
    static readonly Color InUse = Color.FromRgb(0xB5, 0x8C, 0xFF), Modified = Color.FromRgb(0xFF, 0xB4, 0x54),
        Standby = Color.FromRgb(0x5A, 0x4B, 0x86), Free = Color.FromRgb(0x33, 0x38, 0x42);

    public bool WantsProcesses => true;

    public MemoryPage()
    {
        InitializeComponent();
        var mon = SystemMonitor.Instance;
        SubTitle.Text = $"{Fmt.Bytes(mon.Info.InstalledRam, 0)} installed";

        _commit = Ui.AddKeyValue(Stats, "Committed");
        _cached = Ui.AddKeyValue(Stats, "Cached");
        _paged = Ui.AddKeyValue(Stats, "Paged pool");
        _nonPaged = Ui.AddKeyValue(Stats, "Non-paged pool");
        _pageFile = Ui.AddKeyValue(Stats, "Page file in use");
        _counts = Ui.AddKeyValue(Stats, "Processes / threads");

        MemChart.SetSeries((mon.History.Mem, Ui.Color("C.Mem"), true));
        AdminChip.Visibility = Sys.IsAdmin ? Visibility.Collapsed : Visibility.Visible;
        StandbyButton.IsEnabled = Sys.IsAdmin;
    }

    public void OnTick(Snapshot s)
    {
        UsedText.Text = Fmt.Bytes(s.MemUsed);
        UsedDetail.Text = $"in use of {Fmt.Bytes(s.MemTotal)}  ·  {s.MemPct:0}%";
        AvailText.Text = $"{Fmt.Bytes(s.MemAvail)} available";
        MemChart.Refresh();

        if (s.HasMemLists)
        {
            double inUse = Math.Max(0, (double)s.MemTotal - s.MemAvail - s.Modified);
            Composition.Update((inUse, InUse), (s.Modified, Modified), (s.Standby, Standby), (s.FreeZero, Free));
            Legend.ItemsSource = new[]
            {
                new LegendItem("In use", Fmt.Bytes(inUse), new SolidColorBrush(InUse), "Memory actively used by apps, drivers and Windows"),
                new LegendItem("Modified", Fmt.Bytes(s.Modified), new SolidColorBrush(Modified), "Changed pages waiting to be written to disk before they can be reused"),
                new LegendItem("Standby", Fmt.Bytes(s.Standby), new SolidColorBrush(Standby), "Cached data that is instantly reusable when an app needs memory"),
                new LegendItem("Free", Fmt.Bytes(s.FreeZero), new SolidColorBrush(Free), "Memory holding nothing at all"),
            };
        }
        else
        {
            Composition.Update((s.MemUsed, InUse), (s.MemAvail, Free));
            Legend.ItemsSource = new[]
            {
                new LegendItem("In use", Fmt.Bytes(s.MemUsed), new SolidColorBrush(InUse), "Memory actively used"),
                new LegendItem("Available", Fmt.Bytes(s.MemAvail), new SolidColorBrush(Free), "Memory available to apps"),
            };
        }

        _commit.Text = $"{Fmt.Bytes(s.CommitTotal)} of {Fmt.Bytes(s.CommitLimit)}";
        _cached.Text = Fmt.Bytes(s.HasMemLists ? s.Standby + s.Modified : s.Cached);
        _paged.Text = Fmt.Bytes(s.KernelPaged);
        _nonPaged.Text = Fmt.Bytes(s.KernelNonpaged);
        ulong pageFileTotal = s.CommitLimit > s.MemTotal ? s.CommitLimit - s.MemTotal : 0;
        _pageFile.Text = pageFileTotal == 0 ? "No page file"
            : s.PageFilePct >= 0 ? $"{s.PageFilePct:0}% of {Fmt.Bytes(pageFileTotal)}" : Fmt.Bytes(pageFileTotal);
        _counts.Text = $"{Fmt.Count(s.ProcessCount)} / {Fmt.Count(s.ThreadCount)}";

        if (s.Processes is { Count: > 0 } procs)
        {
            var groups = procs.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key, Count: g.Count(), Bytes: g.Sum(p => p.PrivateWs)))
                .OrderByDescending(g => g.Bytes).Take(12).ToList();
            double max = Math.Max(1, groups[0].Bytes);
            Consumers.ItemsSource = groups.Select(g => new ConsumerRow(g.Name, g.Count > 1 ? $"×{g.Count}" : "", 100.0 * g.Bytes / max, Fmt.Bytes(g.Bytes))).ToList();
        }
    }

    /// <summary>Trims working sets and reports how much memory became available.</summary>
    public static async Task<string> RunTrim()
    {
        ulong before = RefreshService.AvailableMemory();
        int count = await Task.Run(MemoryOptimizer.TrimWorkingSets);
        await Task.Delay(1500);
        SystemMonitor.Instance.Poke();
        ulong after = RefreshService.AvailableMemory();
        return before > 0 && after > before
            ? $"Trimmed {count} processes. {Fmt.Bytes(after - before)} more memory is now available."
            : $"Trimmed {count} processes. No extra memory was reclaimed this time.";
    }

    async void Trim_Click(object sender, RoutedEventArgs e)
    {
        TrimButton.IsEnabled = false;
        ResultText.Text = "Trimming…";
        try { ResultText.Text = await RunTrim(); }
        finally { TrimButton.IsEnabled = true; }
    }

    async void Standby_Click(object sender, RoutedEventArgs e)
    {
        StandbyButton.IsEnabled = false;
        try
        {
            ulong before = SystemMonitor.Instance.Latest?.Standby ?? 0;
            bool ok = await Task.Run(MemoryOptimizer.PurgeStandbyList);
            await Task.Delay(1500);
            ulong after = SystemMonitor.Instance.Latest?.Standby ?? 0;
            ResultText.Text = ok
                ? $"Standby cache cleared{(before > after ? $": {Fmt.Bytes(before - after)} released" : "")}."
                : "Windows refused to clear the standby cache.";
        }
        finally { StandbyButton.IsEnabled = Sys.IsAdmin; }
    }
}
