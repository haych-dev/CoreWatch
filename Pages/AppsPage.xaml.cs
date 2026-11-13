using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CoreWatch.Services;

namespace CoreWatch.Pages;

public partial class AppsPage : UserControl, IPage
{
    List<InstalledApp> _apps = [];
    ICollectionView? _view;
    string _filter = "";
    bool _loaded;

    public AppsPage() => InitializeComponent();

    public void OnShown()
    {
        if (!_loaded) _ = Reload();
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
        SubTitle.Text = $"{_apps.Count} desktop apps  ·  {Fmt.Bytes(_apps.Sum(a => a.SizeBytes))} reported in total";
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
}
