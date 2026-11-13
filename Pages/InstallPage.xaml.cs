using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CoreWatch.Controls;
using CoreWatch.Services;

namespace CoreWatch.Pages;

public partial class InstallPage : UserControl, IPage
{
    readonly List<CatalogApp> _apps = WingetService.BuildCatalog();
    bool _loaded, _busy, _stop;

    public InstallPage()
    {
        InitializeComponent();
        foreach (var app in _apps) app.PropertyChanged += App_Changed;
        BuildGroups();
        if (WingetService.Exe == null)
        {
            SummaryTitle.Text = "winget is not available on this PC";
            SummaryDetail.Text = "Install \"App Installer\" from the Microsoft Store, then reopen CoreWatch.";
            UpdateButton.IsEnabled = false;
        }
    }

    public void OnShown()
    {
        if (_loaded || WingetService.Exe == null) return;
        _loaded = true;
        _ = LoadInstalled();
    }

    async Task LoadInstalled()
    {
        var installed = await Task.Run(() => WingetService.InstalledIds());
        foreach (var app in _apps.Where(a => installed.Contains(a.Id))) app.Installed = true;
        _checked = true;
        UpdateSummary();
    }

    bool _checked;

    void BuildGroups()
    {
        Groups.Children.Clear();
        string filter = SearchBox.Text.Trim();
        foreach (var group in _apps.GroupBy(a => a.Category))
        {
            var items = group.Where(a => filter.Length == 0 || a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                         || a.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                         || a.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (items.Count == 0) continue;
            Groups.Children.Add(new TextBlock { Text = group.Key, Style = Ui.Style("T.Card"), Margin = new Thickness(2, 22, 2, 8) });
            Groups.Children.Add(new Border
            {
                Style = Ui.Style("Card"),
                Padding = new Thickness(14, 14, 4, 4),
                Child = new ItemsControl
                {
                    ItemsSource = items, Focusable = false,
                    ItemTemplate = (DataTemplate)Resources["AppTile"],
                    ItemsPanel = (ItemsPanelTemplate)Resources["TileGrid"],
                },
            });
        }
        if (Groups.Children.Count == 0)
            Groups.Children.Add(new TextBlock { Text = $"No apps match \"{filter}\".", Style = Ui.Style("T.Sub"), Margin = new Thickness(2, 22, 0, 0) });
    }

    void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsInitialized) BuildGroups();
    }

    void App_Changed(object? sender, PropertyChangedEventArgs e) => UpdateSummary();

    void UpdateSummary()
    {
        if (WingetService.Exe == null) return;
        int selected = _apps.Count(a => a.Selected && !a.Installed), installed = _apps.Count(a => a.Installed);
        if (!_busy)
        {
            SummaryTitle.Text = selected == 0 ? "Nothing selected" : $"{selected} {(selected == 1 ? "app" : "apps")} selected";
            SummaryDetail.Text = !_checked ? "Checking which apps are already installed…" : $"{_apps.Count} apps in the catalogue  ·  {installed} already installed";
        }
        InstallButton.IsEnabled = !_busy && selected > 0;
        InstallButton.Style = Ui.Style(selected > 0 ? "BtnPrimary" : "Btn");
        UpdateButton.IsEnabled = !_busy;
    }

    void SetBusy(bool busy, bool canStop = false)
    {
        _busy = busy;
        _stop = false;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Busy.IsIndeterminate = busy;
        StopButton.Visibility = busy && canStop ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = true;
        if (busy) { LogPanel.Visibility = Visibility.Visible; Log.Clear(); }
        UpdateSummary();
    }

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        var targets = _apps.Where(a => a.Selected && !a.Installed).ToList();
        if (_busy || targets.Count == 0) return;
        string names = string.Join("\n", targets.Take(12).Select(a => "•  " + a.Name)) + (targets.Count > 12 ? $"\n•  and {targets.Count - 12} more" : "");
        bool ok = await MainWindow.Instance.Confirm($"Install {targets.Count} {(targets.Count == 1 ? "app" : "apps")}?",
            names + "\n\nEach one is downloaded from its publisher and installed silently. Installing accepts each app's licence terms.", "Install");
        if (!ok) return;

        SetBusy(true, canStop: targets.Count > 1);
        int done = 0;
        var failed = new List<string>();
        try
        {
            for (int i = 0; i < targets.Count && !_stop; i++)
            {
                var app = targets[i];
                SummaryTitle.Text = $"Installing {app.Name}…";
                SummaryDetail.Text = $"App {i + 1} of {targets.Count}";
                app.Failed = false;
                app.Busy = true;
                app.Status = "Installing…";
                Log.Append($"▶ {app.Name}  ({app.Id})");
                int code;
                try { code = await WingetService.Run(WingetService.InstallArgs(app.Id), Log.Append); }
                catch (Exception ex) { Log.Append(ex.Message); code = -1; }
                app.Busy = false;
                // 0x8A150061: winget reports the package is already installed.
                if (code == 0 || code == unchecked((int)0x8A150061)) { app.Installed = true; done++; }
                else { app.Failed = true; app.Status = "Failed"; failed.Add(app.Name); Log.Append($"{app.Name} did not install (code 0x{code:X8})."); }
            }
        }
        finally { SetBusy(false); }

        if (failed.Count > 0) MainWindow.Instance.Toast($"Installed {done}. Could not install: {string.Join(", ", failed)}. See the output for why.", true);
        else MainWindow.Instance.Toast($"Installed {done} {(done == 1 ? "app" : "apps")}.");
    }

    void Stop_Click(object sender, RoutedEventArgs e)
    {
        _stop = true;
        StopButton.IsEnabled = false;
    }

    async void UpdateAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        bool ok = await MainWindow.Instance.Confirm("Update every app winget can update?",
            "This updates all installed apps that have a newer version available, not only the ones on this page. Apps that are open may be closed by their installers.", "Update all");
        if (!ok) return;

        SetBusy(true);
        SummaryTitle.Text = "Updating apps…";
        SummaryDetail.Text = "This can take several minutes.";
        int code;
        try { code = await WingetService.Run(WingetService.UpgradeAllArgs, Log.Append); }
        catch (Exception ex) { Log.Append(ex.Message); code = -1; }
        finally { SetBusy(false); }
        if (code == 0) MainWindow.Instance.Toast("Apps are up to date.");
        else MainWindow.Instance.Toast("Some apps could not be updated. See the output for details.", true);
    }
}
