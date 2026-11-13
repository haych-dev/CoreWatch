using System.Windows.Controls;
using CoreWatch.Controls;
using CoreWatch.Services;

namespace CoreWatch.Pages;

public partial class PerformancePage : UserControl, IPage
{
    readonly TextBlock _util, _speed, _processes, _threads, _handles, _uptime;

    public PerformancePage()
    {
        InitializeComponent();
        var mon = SystemMonitor.Instance;
        var h = mon.History;
        SubTitle.Text = mon.Info.CpuName;

        _util = Ui.AddKeyValue(CpuStats, "Utilization");
        _speed = Ui.AddKeyValue(CpuStats, "Speed");
        Ui.AddKeyValue(CpuStats, "Base speed", $"{mon.Info.BaseMhz / 1000:0.00} GHz");
        Ui.AddKeyValue(CpuStats, "Cores", $"{mon.Info.PhysicalCores} cores, {mon.Info.LogicalCores} threads");
        _processes = Ui.AddKeyValue(CpuStats, "Processes");
        _threads = Ui.AddKeyValue(CpuStats, "Threads");
        _handles = Ui.AddKeyValue(CpuStats, "Handles");
        _uptime = Ui.AddKeyValue(CpuStats, "Up time");

        CpuChart.SetSeries((h.Cpu, Ui.Color("C.Cpu"), true));
        NetChart.SetSeries((h.NetDown, Ui.Color("C.Net"), true), (h.NetUp, Ui.Color("C.Cpu"), false));
        DiskChart.SetSeries((h.DiskRead, Ui.Color("C.Disk"), true), (h.DiskWrite, Ui.Color("C.Vram"), false));
    }

    public void OnTick(Snapshot s)
    {
        CpuPct.Text = Fmt.Pct(s.CpuTotal);
        CpuSpeed.Text = $"{s.CpuMhz / 1000:0.00} GHz";
        ChartSpan.Text = $"Last {History.Size * Settings.Current.RefreshMs / 1000} seconds";
        _util.Text = $"{s.CpuTotal:0.0}%";
        _speed.Text = $"{s.CpuMhz / 1000:0.00} GHz";
        _processes.Text = Fmt.Count(s.ProcessCount);
        _threads.Text = Fmt.Count(s.ThreadCount);
        _handles.Text = Fmt.Count(s.HandleCount);
        _uptime.Text = Fmt.Duration(s.Uptime);
        CpuChart.Refresh();

        Cores.Update(s.CpuCores);
        if (s.CpuCores.Length > 0)
        {
            int busy = s.CpuCores.Count(c => c >= 50);
            CoreSummary.Text = $"Busiest {s.CpuCores.Max():0}%  ·  {busy} of {s.CpuCores.Length} above 50%";
        }

        NetDown.Text = Fmt.Rate(s.NetDown);
        NetUp.Text = Fmt.Rate(s.NetUp);
        NetAdapter.Text = s.NetAdapter.Length == 0 ? "No active connection"
            : s.NetAdapter + (s.NetAddress.Length > 0 ? "  ·  " + s.NetAddress : "") + (s.NetLinkSpeed > 0 ? "  ·  " + LinkSpeed(s.NetLinkSpeed) : "");
        NetChart.Refresh();
        NetScale.Text = "Scale: " + Fmt.Rate(NetChart.EffectiveMaximum);

        DiskRead.Text = Fmt.Rate(s.DiskRead);
        DiskWrite.Text = Fmt.Rate(s.DiskWrite);
        DiskActive.Text = $"Active time {s.DiskActive:0}%";
        DiskChart.Refresh();
        DiskScale.Text = "Scale: " + Fmt.Rate(DiskChart.EffectiveMaximum);
    }

    static string LinkSpeed(long bps) => bps >= 1_000_000_000 ? $"{bps / 1e9:0.#} Gbps" : $"{bps / 1e6:0} Mbps";
}
