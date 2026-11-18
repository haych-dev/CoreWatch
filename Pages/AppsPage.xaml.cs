using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CoreWatch.Controls;
using CoreWatch.Services;

namespace CoreWatch.Pages;

public partial class AppsPage : UserControl, IPage
{
    List<InstalledApp> _apps = [];
    List<StoreApp> _store = [];
    ICollectionView? _view;
    string _filter = "";
    bool _loaded, _storeLoaded, _ready, _removing;

    public AppsPage()
    {
        InitializeComponent();
        _ready = true;
    }

    public void OnShown()
    {
        if (!_loaded) _ = Reload();
        if (App.DevTab?.Contains("preinstalled") == true) TabStore.IsChecked = true;
    }

    async Task Reload()
    {
        _loaded = true;
        _apps = await Task.Run(AppsService.Load);
        _view = CollectionViewSource.GetDefaultView(_apps);
        _view.Filter = o => o is InstalledApp a && (_filter.Length == 0
            || a.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
            || a.Publisher.Contains(_filter, StringComparison.OrdinalIgnoreCase));
        _view.SortDescriptions.Add(new SortDescription(nameof(InstalledApp.SizeBytes), ListSortDirection.Descending));
        Table.ItemsSource = _view;
        SizeColumn.SortDirection = ListSortDirection.Descending;
        ShowSubTitle();
    }

    void ShowSubTitle()
    {
        SubTitle.Text = TabStore.IsChecked == true
            ? "Apps that came with Windows or the PC and that you can do without."
            : _loaded && _apps.Count > 0 ? $"{_apps.Count} desktop apps  ·  {Fmt.Bytes(_apps.Sum(a => a.SizeBytes))} reported in total" : "Loading installed apps…";
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        bool store = TabStore.IsChecked == true;
        DesktopView.Visibility = SearchBox.Visibility = store ? Visibility.Collapsed : Visibility.Visible;
        StoreView.Visibility = store ? Visibility.Visible : Visibility.Collapsed;
        ShowSubTitle();
        if (store && !_storeLoaded) _ = LoadStore();
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = Search.Text.Trim();
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view?.Refresh();
    }

    void Table_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (_view == null || e.Column.SortDirection != null || e.Column.SortMemberPath is not (nameof(InstalledApp.SizeBytes) or nameof(InstalledApp.Installed))) return;
        e.Handled = true;
        foreach (var c in Table.Columns) c.SortDirection = null;
        e.Column.SortDirection = ListSortDirection.Descending;
        _view.SortDescriptions.Clear();
        _view.SortDescriptions.Add(new SortDescription(e.Column.SortMemberPath, ListSortDirection.Descending));
    }

    async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not InstalledApp app) return;
        bool ok = await MainWindow.Instance.Confirm($"Uninstall {app.Name}?",
            $"This opens the uninstaller provided by {(app.Publisher.Length > 0 ? app.Publisher : "the app")}. Follow its steps to finish removing the app.",
            "Open uninstaller", danger: true);
        if (!ok) return;
        if (AppsService.Uninstall(app)) MainWindow.Instance.Toast($"Uninstaller for {app.Name} started. Refresh this page when it finishes.");
        else MainWindow.Instance.Toast("The uninstaller could not be started.", true);
    }

    // ───────── Preinstalled apps ─────────
    async Task LoadStore()
    {
        _storeLoaded = true;
        StoreBusy.Visibility = Visibility.Visible;
        try
        {
            foreach (var app in _store) app.PropertyChanged -= Store_Changed;
            _store = await Task.Run(AppxService.List);
            foreach (var app in _store) app.PropertyChanged += Store_Changed;
            StoreList.ItemsSource = _store;
            StoreCard.Visibility = _store.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StoreTitle.Text = "Windows did not hand over the list of apps";
            StoreDetail.Text = ex.Message;
            StoreBusy.Visibility = Visibility.Collapsed;
            _storeLoaded = false;
            return;
        }
        StoreBusy.Visibility = Visibility.Collapsed;
        ShowRemoved();
        UpdateStoreSummary();
    }

    void ShowRemoved()
    {
        // Something the user has since reinstalled no longer needs a "Get it back" button.
        var removed = AppxService.Removed().Where(r => !_store.Any(a => a.PackageName.Equals(r.PackageName, StringComparison.OrdinalIgnoreCase))).ToList();
        RemovedList.ItemsSource = removed;
        RemovedPanel.Visibility = removed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Store_Changed(object? sender, PropertyChangedEventArgs e) => UpdateStoreSummary();

    void UpdateStoreSummary()
    {
        int selected = _store.Count(a => a.Selected);
        if (!_removing)
        {
            StoreTitle.Text = _store.Count == 0 ? "No removable preinstalled apps found"
                : selected == 0 ? $"{_store.Count} preinstalled {(_store.Count == 1 ? "app" : "apps")} you can remove" : $"{selected} of {_store.Count} selected";
            StoreDetail.Text = "Removing one only affects your account, and each can be installed again from the Microsoft Store.";
        }
        RemoveButton.IsEnabled = !_removing && selected > 0;
        RemoveButton.Style = Ui.Style(selected > 0 ? "BtnPrimary" : "Btn");
    }

    async void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var targets = _store.Where(a => a.Selected).ToList();
        if (_removing || targets.Count == 0) return;
        string names = string.Join("\n", targets.Take(12).Select(a => "•  " + a.Name + (a.Note != null ? $"  ({a.Note})" : ""))) + (targets.Count > 12 ? $"\n•  and {targets.Count - 12} more" : "");
        bool ok = await MainWindow.Instance.Confirm($"Remove {targets.Count} {(targets.Count == 1 ? "app" : "apps")}?",
            names + "\n\nThey are removed for your account, along with their settings. To get one back, use \"Get it back\" here or search for it in the Microsoft Store.", "Remove", danger: true);
        if (!ok) return;

        _removing = true;
        StoreBusy.Visibility = Visibility.Visible;
        UpdateStoreSummary();
        int done = 0;
        var failed = new List<string>();
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var app = targets[i];
                StoreTitle.Text = $"Removing {app.Name}…";
                StoreDetail.Text = $"App {i + 1} of {targets.Count}";
                app.Busy = true;
                string? error = await Task.Run(() => AppxService.Remove(app));
                app.Busy = false;
                if (error == null) done++;
                else failed.Add(app.Name);
            }
        }
        finally { _removing = false; }

        await LoadStore();
        if (failed.Count > 0) MainWindow.Instance.Toast($"Removed {done}. Could not remove: {string.Join(", ", failed)}.", true);
        else MainWindow.Instance.Toast($"Removed {done} {(done == 1 ? "app" : "apps")}.");
    }

    void Restore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is RemovedApp app) Sys.ShellOpen(AppxService.StoreLink(app.FamilyName));
    }
}
