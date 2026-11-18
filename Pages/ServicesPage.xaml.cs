using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CoreWatch.Services;

namespace CoreWatch.Pages;

public partial class ServicesPage : UserControl, IPage
{
    List<ServiceItem> _services = [];
    ICollectionView? _view;
    string _filter = "", _mode = "all";
    bool _loaded, _ready;
    ServiceItem? _selected;

    public ServicesPage()
    {
        InitializeComponent();
        AdminBar.Visibility = Sys.IsAdmin ? Visibility.Collapsed : Visibility.Visible;
        _ready = true;
    }

    public void OnShown()
    {
        if (!_loaded) _ = Reload();
    }

    async Task Reload()
    {
        _loaded = true;
        try { _services = await Task.Run(ServiceManager.List); }
        catch (Exception ex)
        {
            SubTitle.Text = "Windows did not hand over the list of services: " + ex.Message;
            return;
        }
        _view = CollectionViewSource.GetDefaultView(_services);
        _view.Filter = o => o is ServiceItem s
            && (_filter.Length == 0 || s.Search.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            && (_mode switch { "running" => s.Running, "third" => s.ThirdParty, "changed" => s.Changed, _ => true });
        Table.ItemsSource = _view;
        UpdateSummary();
    }

    void UpdateSummary()
    {
        int running = _services.Count(s => s.Running), changed = _services.Count(s => s.Changed);
        SubTitle.Text = $"{_services.Count} services  ·  {running} running  ·  {_services.Count(s => s.ThirdParty)} not from Windows" +
                        (changed > 0 ? $"  ·  {changed} changed here" : "") +
                        ". Changing a startup type keeps the original, so it can be undone exactly.";
    }

    void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string mode }) return;
        _mode = mode;
        _view?.Refresh();
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = Search.Text.Trim();
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view?.Refresh();
    }

    void Table_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = Table.SelectedItem as ServiceItem;
        ShowDetail();
    }

    void ShowDetail()
    {
        var s = _selected;
        Detail.Visibility = s == null ? Visibility.Collapsed : Visibility.Visible;
        if (s == null) return;

        DetailName.Text = s.DisplayName;
        DetailText.Text = s.Description.Length > 0 ? s.Description : "This service has no description.";
        DetailText.ToolTip = s.Description.Length > 160 ? s.Description : null;

        bool can = Sys.IsAdmin && !s.Protected && !s.Busy;
        StartButton.IsEnabled = can && !s.Running && s.Start != "4";
        StopButton.IsEnabled = can && s.Running;
        UndoButton.Visibility = s.Changed ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.IsEnabled = can;
        foreach (RadioButton rb in StartPanel.Children)
        {
            rb.IsChecked = (string)rb.Tag == s.Start;
            rb.IsEnabled = can;
        }
        DetailNote.Text = s.ProtectedReason
            ?? (s.Changed ? $"Was {ServiceManager.StartName(s.Original!).ToLowerInvariant()} before CoreWatch changed it." : $"Runs as {(s.Account.Length > 0 ? s.Account : "an unknown account")}.");
    }

    /// <summary>Runs one change off the UI thread, re-reads the service and reports the outcome.</summary>
    async Task Run(ServiceItem s, Func<string?> work, string done)
    {
        s.SetBusy(true);
        ShowDetail();
        string? error;
        try
        {
            error = await Task.Run(work);
            await Task.Run(() => ServiceManager.Refresh(s));
        }
        catch (Exception ex) { error = ex.Message; }
        finally { s.SetBusy(false); }
        ShowDetail();
        UpdateSummary();
        MainWindow.Instance.Toast(error == null ? done : $"{s.DisplayName}: {error}", error != null);
    }

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { } s) await Run(s, () => ServiceManager.Control(s, true), $"{s.DisplayName} started.");
    }

    async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } s) return;
        bool ok = await MainWindow.Instance.Confirm($"Stop {s.DisplayName}?",
            "Apps and Windows features that rely on this service stop working until it is started again. Its startup type is not changed, so it comes back after a restart.", "Stop", danger: true);
        if (ok) await Run(s, () => ServiceManager.Control(s, false), $"{s.DisplayName} stopped.");
    }

    async void StartType_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } s || sender is not RadioButton { Tag: string start }) return;
        if (start == s.Start) return;
        if (start == "4")
        {
            bool ok = await MainWindow.Instance.Confirm($"Disable {s.DisplayName}?",
                "It will no longer start, even when an app asks for it. Anything that relies on it stops working until you change this back.\n\nCoreWatch remembers the current setting, so \"Undo change\" puts it back exactly.", "Disable", danger: true);
            if (!ok) { ShowDetail(); return; }
        }
        await Run(s, () => ServiceManager.SetStart(s, start), $"{s.DisplayName} is now set to {ServiceManager.StartName(start).ToLowerInvariant()}.");
    }

    async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { Original: { } original } s)
            await Run(s, () => ServiceManager.Undo(s), $"{s.DisplayName} is back to {ServiceManager.StartName(original).ToLowerInvariant()}.");
    }

    void Admin_Click(object sender, RoutedEventArgs e)
    {
        if (!Sys.RestartAsAdmin("services")) MainWindow.Instance.Toast("CoreWatch was not restarted.", true);
    }
}
