using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CoreWatch.Controls;
using CoreWatch.Services;

namespace CoreWatch.Pages;

public partial class NetworkPage : UserControl, IPage
{
    AdapterInfo? _adapter;
    DateTime _socketsAt, _adaptersAt;
    bool _loading, _dnsBusy;
    string _adapterSignature = "", _dnsSignature = "";

    public NetworkPage()
    {
        InitializeComponent();
        var history = SystemMonitor.Instance.History;
        Chart.Maximum = 0;
        Chart.MinAutoMaximum = 64 * 1024;
        Chart.SetSeries((history.NetDown, Ui.Color("C.Net"), true), (history.NetUp, Ui.Color("C.Disk"), false));
    }

    public void OnShown()
    {
        _socketsAt = _adaptersAt = default;
    }

    public void OnTick(Snapshot s)
    {
        DownText.Text = Fmt.Rate(s.NetDown);
        UpText.Text = Fmt.Rate(s.NetUp);
        Chart.Refresh();
        ScaleText.Text = Fmt.Rate(Chart.EffectiveMaximum);

        var now = DateTime.Now;
        if ((now - _adaptersAt).TotalSeconds >= 10) { _adaptersAt = now; _ = LoadAdapters(); }
        if ((now - _socketsAt).TotalSeconds >= 3 && !_loading) { _socketsAt = now; _ = LoadSockets(); }
    }

    // ───────── Adapter and DNS ─────────
    async Task LoadAdapters()
    {
        var adapters = await Task.Run(NetworkService.Adapters);
        _adapter = adapters.FirstOrDefault();
        string signature = string.Join("|", adapters.Select(a => $"{a.Id}{a.IPv4}{a.Gateway}{a.Speed}{string.Join(",", a.Dns)}{a.DnsAutomatic}"));
        if (signature == _adapterSignature) return;
        _adapterSignature = signature;

        AdapterFacts.Children.Clear();
        if (_adapter is not { } a)
        {
            AdapterName.Text = "No connection";
            AdapterFacts.Children.Add(new TextBlock { Text = "No network adapter is connected right now.", Style = Ui.Style("T.Sub") });
            SubTitle.Text = "Not connected to a network.";
        }
        else
        {
            AdapterName.Text = a.Name;
            Ui.AddKeyValue(AdapterFacts, "Type", $"{a.Kind}  ·  {NetworkService.SpeedText(a.Speed)}");
            Ui.AddKeyValue(AdapterFacts, "Adapter", a.Description);
            Ui.AddKeyValue(AdapterFacts, "IPv4 address", a.IPv4.Length > 0 ? a.IPv4 : "None");
            if (a.IPv6.Length > 0) Ui.AddKeyValue(AdapterFacts, "IPv6 address", a.IPv6);
            Ui.AddKeyValue(AdapterFacts, "Router", a.Gateway.Length > 0 ? a.Gateway : "None");
            Ui.AddKeyValue(AdapterFacts, "Hardware address", a.Mac.Length > 0 ? a.Mac : "None");
            SubTitle.Text = $"Connected through {a.Name} ({a.Kind}, {NetworkService.SpeedText(a.Speed)}).";
        }

        var others = adapters.Skip(1).ToList();
        OtherAdapters.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OtherAdapters.Text = "Also active: " + string.Join("   ·   ", others.Select(o => $"{o.Name} ({(o.IPv4.Length > 0 ? o.IPv4 : o.Kind)})"));
        ShowDns();
    }

    void ShowDns()
    {
        var a = _adapter;
        string signature = a == null ? "" : $"{a.Id}{string.Join(",", a.Dns)}{a.DnsAutomatic}{NetworkService.HasDnsBackup(a)}{_dnsBusy}";
        if (signature == _dnsSignature && DnsButtons.Children.Count > 0) return;
        _dnsSignature = signature;

        DnsButtons.Children.Clear();
        if (a == null) { DnsText.Text = "No network adapter is connected."; return; }

        string servers = a.Dns.Count > 0 ? string.Join(" and ", a.Dns) : "none set";
        var match = NetworkService.DnsPresets.FirstOrDefault(p => p.Servers.SequenceEqual(a.Dns));
        DnsText.Text = a.DnsAutomatic ? $"Automatic: {a.Name} uses the servers your router hands out ({servers})."
            : match != null ? $"{a.Name} uses {match.Name} ({servers})."
            : $"{a.Name} uses servers set by hand ({servers}).";

        bool can = Sys.IsAdmin && !_dnsBusy;
        void Add(string label, string tip, bool current, Func<string?> work, string done)
        {
            var button = new Button { Style = Ui.Style(current ? "BtnPrimary" : "Btn"), Content = label, Margin = new Thickness(8, 0, 0, 0), ToolTip = tip, IsEnabled = can && !current };
            button.Click += async (_, _) => await ChangeDns(label, work, done);
            DnsButtons.Children.Add(button);
        }

        if (!Sys.IsAdmin)
        {
            var admin = new Button { Style = Ui.Style("Btn"), Content = "Restart as administrator to change" };
            admin.Click += (_, _) => { if (!Sys.RestartAsAdmin("network")) MainWindow.Instance.Toast("CoreWatch was not restarted.", true); };
            DnsButtons.Children.Add(admin);
            return;
        }
        Add("Automatic", "Use the servers your router hands out", a.DnsAutomatic, () => NetworkService.SetDns(a, null), "DNS is back to automatic.");
        foreach (var preset in NetworkService.DnsPresets)
            Add(preset.Name, preset.Tip, !a.DnsAutomatic && preset.Servers.SequenceEqual(a.Dns), () => NetworkService.SetDns(a, preset.Servers), $"DNS now goes through {preset.Name}.");
        if (NetworkService.HasDnsBackup(a))
            Add("Restore original", "Put back the setting from before CoreWatch changed it", false, () => NetworkService.RestoreDns(a), "The original DNS setting is back.");
    }

    async Task ChangeDns(string label, Func<string?> work, string done)
    {
        if (_dnsBusy) return;
        _dnsBusy = true;
        ShowDns();
        string? error;
        try { error = await Task.Run(work); }
        catch (Exception ex) { error = ex.Message; }
        _dnsBusy = false;
        _adapterSignature = "";
        await LoadAdapters();
        MainWindow.Instance.Toast(error == null ? done : $"DNS was not changed: {error}", error != null);
    }

    // ───────── Connections ─────────
    async Task LoadSockets()
    {
        _loading = true;
        try
        {
            var (connections, listeners) = await Task.Run(NetworkService.Sockets);
            var apps = NetworkService.ByApp(connections, listeners);
            Keep(AppTable, apps);
            Keep(ConnectionTable, connections);
            Keep(ListenerTable, listeners);
            ConnectionTitle.Text = $"Open connections ({connections.Count})";
            ListenerTitle.Text = $"Listening ports ({listeners.Count})";
        }
        finally { _loading = false; }
    }

    /// <summary>Swaps in fresh rows without losing the scroll position or the chosen sort order.</summary>
    static void Keep<T>(DataGrid grid, List<T> rows)
    {
        var sorts = grid.Items.SortDescriptions.ToList();
        var directions = grid.Columns.Select(c => c.SortDirection).ToList();
        var scroll = FindScroll(grid);
        double offset = scroll?.VerticalOffset ?? 0;
        grid.ItemsSource = rows;
        foreach (var sort in sorts) grid.Items.SortDescriptions.Add(sort);
        for (int i = 0; i < directions.Count; i++) grid.Columns[i].SortDirection = directions[i];
        scroll?.ScrollToVerticalOffset(offset);
    }

    static ScrollViewer? FindScroll(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScroll(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>Lets the page keep scrolling when an inner table has reached its end.</summary>
    void Inner_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DataGrid grid || FindScroll(grid) is not { } inner) return;
        bool atTop = inner.VerticalOffset <= 0 && e.Delta > 0;
        bool atBottom = inner.VerticalOffset >= inner.ScrollableHeight && e.Delta < 0;
        if (!atTop && !atBottom) return;
        e.Handled = true;
        Scroller.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
    }
}
