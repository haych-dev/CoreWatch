using System.Windows;
using System.Windows.Controls;
using CoreWatch.Services;

namespace CoreWatch.Pages;

public partial class StartupPage : UserControl, IPage
{
    bool _loaded;

    public StartupPage() => InitializeComponent();

    public void OnShown()
    {
        if (!_loaded) _ = Reload();
    }

    async Task Reload()
    {
        _loaded = true;
        var entries = await Task.Run(StartupService.Load);
        EntryList.ItemsSource = entries;
        Empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummary(entries);
    }

    void UpdateSummary(IReadOnlyCollection<StartupEntry> entries)
    {
        int on = entries.Count(e => e.Enabled);
        SubTitle.Text = $"{entries.Count} apps launch when you sign in  ·  {on} enabled, {entries.Count - on} disabled";
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await Reload();

    void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: StartupEntry entry } box) return;
        bool want = !entry.Enabled;
        string? error = StartupService.SetEnabled(entry, want);
        box.IsChecked = entry.Enabled; // reflect what actually happened
        if (error != null) MainWindow.Instance.Toast(error, true);
        else MainWindow.Instance.Toast($"{entry.Name} will {(want ? "start" : "no longer start")} when you sign in.");
        if (EntryList.ItemsSource is List<StartupEntry> all) UpdateSummary(all);
    }
}
