using System.Windows;
using System.Windows.Controls;
using CoreWatch.Controls;
using CoreWatch.Services;

namespace CoreWatch.Pages;

public partial class GpuCard : UserControl
{
    readonly TextBlock _dedicated, _shared, _temp, _fan, _power;

    public GpuCard(GpuSample g)
    {
        InitializeComponent();
        NameText.Text = g.Name;
        VendorText.Text = g.Vendor;
        KindText.Text = g.IsDiscrete ? "Discrete" : "Integrated";

        _dedicated = Ui.AddKeyValue(Stats, "Dedicated");
        _shared = Ui.AddKeyValue(Stats, "Shared");
        _temp = Ui.AddKeyValue(Stats, "Temperature");
        _fan = Ui.AddKeyValue(Stats, "Fan");
        _power = Ui.AddKeyValue(Stats, "Power draw");

        var h = SystemMonitor.Instance.History.Gpu(g.Luid);
        Chart.SetSeries((h.Util, Ui.Color("C.Gpu"), true), (h.Vram, Ui.Color("C.Vram"), false));
    }

    public void Update(GpuSample g)
    {
        UtilGauge.AnimateTo(g.Utilization);
        UtilText.Text = Fmt.Pct(g.Utilization);
        VramGauge.AnimateTo(g.DedicatedPct);
        VramText.Text = Fmt.Pct(g.DedicatedPct);
        DriverText.Text = g.DriverVersion is { Length: > 0 } d ? "Driver " + d : "";

        _dedicated.Text = $"{Fmt.Bytes(g.DedicatedUsed)} of {Fmt.Bytes(g.DedicatedTotal)}";
        _shared.Text = $"{Fmt.Bytes(g.SharedUsed)} of {Fmt.Bytes(g.SharedTotal)}";
        _temp.Text = g.Temperature is { } t ? $"{t:0} °C" : "Not reported";
        _temp.Foreground = Ui.Brush(g.Temperature >= 83 ? "B.Warn" : "B.Text");
        _fan.Text = g.FanPercent is { } f ? $"{f:0}%" : "Not reported";
        _power.Text = g.PowerWatts is { } w ? $"{w:0} W" : "Not reported";
        Chart.Refresh();
    }
}

public partial class GpuPage : UserControl, IPage
{
    readonly Dictionary<string, GpuCard> _cards = new(StringComparer.OrdinalIgnoreCase);

    public bool WantsProcesses => true;

    public GpuPage() => InitializeComponent();

    public void OnTick(Snapshot s)
    {
        foreach (var g in s.Gpus.OrderByDescending(g => g.DedicatedTotal))
        {
            if (!_cards.TryGetValue(g.Luid, out var card))
            {
                _cards[g.Luid] = card = new GpuCard(g);
                Cards.Children.Add(card);
            }
            card.Update(g);
        }
        EmptyCard.Visibility = s.Gpus.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (s.Processes == null) return;
        var groups = s.Processes.Where(p => p.GpuMem > 0)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Count: g.Count(), Bytes: g.Sum(p => p.GpuMem)))
            .OrderByDescending(g => g.Bytes).ToList();

        ProcEmpty.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (groups.Count == 0) { ProcList.ItemsSource = null; ProcSummary.Text = ""; return; }

        double max = Math.Max(1, groups[0].Bytes);
        // Per-app figures are committed allocations; surfaces shared between apps are counted for each of them.
        ProcSummary.Text = $"{groups.Count} apps  ·  committed per app, shared surfaces count for each";
        ProcList.ItemsSource = groups.Take(14)
            .Select(g => new ConsumerRow(g.Name, g.Count > 1 ? $"×{g.Count}" : "", 100.0 * g.Bytes / max, Fmt.Bytes(g.Bytes))).ToList();
    }
}
