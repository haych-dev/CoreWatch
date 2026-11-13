using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CoreWatch.Controls;
using CoreWatch.Services;

namespace CoreWatch.Pages;

public sealed record BackupRow(RegBackup Backup, string Title, string Detail, bool Locked);

public partial class RegistryPage : UserControl, IPage
{
    const int Preview = 5; // issues shown per category before "Show all"

    List<RegIssue> _issues = [];
    readonly HashSet<string> _expanded = [];
    bool _busy, _scanned;

    public RegistryPage()
    {
        InitializeComponent();
        BuildGroups();
        LoadBackups();
    }

    public void OnShown()
    {
        if (App.DevAnalyze && !_scanned) _ = ScanAsync();
    }

    // ───────── Category cards ─────────
    void BuildGroups()
    {
        Groups.Children.Clear();
        foreach (var category in RegistryScanner.Categories)
        {
            var items = _issues.Where(i => i.Category == category).ToList();
            var panel = new StackPanel();

            var header = new Grid { Margin = new Thickness(0, 10, 0, 10) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            bool clean = _scanned && items.Count == 0;
            header.Children.Add(new TextBlock
            {
                Text = clean ? "" : items.Count > 0 ? "" : "",
                Style = Ui.Style("T.Icon"), FontSize = 17, Margin = new Thickness(2, 0, 16, 0),
                Foreground = Ui.Brush(clean ? "B.Good" : items.Count > 0 ? "B.Warn" : "B.Faint"),
            });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = category.Name, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = category.Description, Style = Ui.Style("T.Label"), Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(text, 1);
            header.Children.Add(text);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (items.Any(i => i.CanSelect))
            {
                var toggle = new Button { Content = "Select all", Style = Ui.Style("BtnGhost"), Foreground = Ui.Brush("B.Accent"), Tag = category, Margin = new Thickness(0, 0, 10, 0) };
                toggle.Click += ToggleCategory_Click;
                right.Children.Add(toggle);
            }
            right.Children.Add(new TextBlock
            {
                Text = !_scanned ? "" : items.Count == 0 ? "Nothing found" : $"{items.Count} found",
                Style = Ui.Style(items.Count > 0 ? "T.Value" : "T.Label"), FontSize = 13, MinWidth = 96, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(right, 2);
            header.Children.Add(right);
            panel.Children.Add(header);

            if (items.Count > 0)
            {
                bool expanded = _expanded.Contains(category.Id);
                panel.Children.Add(new Border { Height = 1, Background = Ui.Brush("B.Line") });
                panel.Children.Add(new ItemsControl
                {
                    ItemsSource = expanded ? items : items.Take(Preview).ToList(),
                    ItemTemplate = (DataTemplate)Resources["IssueTemplate"],
                    Focusable = false, Margin = new Thickness(0, 4, 0, 4),
                });
                if (items.Count > Preview)
                {
                    var more = new Button
                    {
                        Content = expanded ? "Show fewer" : $"Show all {items.Count}", Style = Ui.Style("BtnGhost"), Foreground = Ui.Brush("B.Accent"),
                        HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(24, 0, 0, 8), Tag = category,
                    };
                    more.Click += Expand_Click;
                    panel.Children.Add(more);
                }
            }

            Groups.Children.Add(new Border { Style = Ui.Style("Card"), Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 10, 0, 0), Child = panel });
        }
    }

    void Expand_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not RegCategory category) return;
        if (!_expanded.Remove(category.Id)) _expanded.Add(category.Id);
        BuildGroups();
    }

    void ToggleCategory_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not RegCategory category) return;
        var items = _issues.Where(i => i.Category == category && i.CanSelect).ToList();
        bool select = items.Any(i => !i.Selected);
        foreach (var i in items) i.Selected = select;
    }

    void Issue_Changed(object? sender, PropertyChangedEventArgs e) => UpdateSummary();

    void UpdateSummary()
    {
        if (!_scanned) return;
        int selected = _issues.Count(i => i.Selected && i.CanSelect), locked = _issues.Count(i => i.Locked);

        TotalText.Text = _issues.Count.ToString();
        TotalText.Visibility = Visibility.Visible;
        TotalLabel.Text = _issues.Count == 0 ? "leftover entries. The registry is tidy." : _issues.Count == 1 ? "leftover entry found" : "leftover entries found";
        TotalDetail.Text = _issues.Count == 0
            ? "Nothing points to a missing program or file."
            : $"{selected} selected to remove" + (locked > 0 ? $"  ·  {locked} need administrator rights" : "");

        FixButton.IsEnabled = !_busy && selected > 0;
        FixButton.Style = Ui.Style(selected > 0 ? "BtnPrimary" : "Btn");
        ScanButton.Style = Ui.Style(selected > 0 ? "Btn" : "BtnPrimary");
        ScanButton.Content = "Scan again";

        AdminBar.Visibility = locked > 0 ? Visibility.Visible : Visibility.Collapsed;
        AdminText.Text = $"{locked} {(locked == 1 ? "entry is" : "entries are")} in the part of the registry shared by all users and can only be removed as administrator.";
    }

    void SetBusy(bool busy, string? label = null)
    {
        _busy = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Busy.IsIndeterminate = busy;
        ScanButton.IsEnabled = !busy;
        if (busy) FixButton.IsEnabled = false;
        if (label != null) { TotalLabel.Text = label; TotalDetail.Text = "This takes a moment."; TotalText.Visibility = Visibility.Collapsed; }
    }

    // ───────── Scan ─────────
    async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    async Task ScanAsync()
    {
        if (_busy) return;
        SetBusy(true, "Scanning the registry…");
        try
        {
            var found = await Task.Run(() => RegistryScanner.Scan());
            foreach (var old in _issues) old.PropertyChanged -= Issue_Changed;
            foreach (var issue in found)
            {
                issue.Selected = issue.CanSelect;
                issue.PropertyChanged += Issue_Changed;
            }
            _issues = found;
            _scanned = true;
        }
        catch (Exception ex) { MainWindow.Instance.Toast("The scan failed: " + ex.Message, true); }
        finally
        {
            SetBusy(false);
            BuildGroups();
            UpdateSummary();
        }
    }

    // ───────── Fix ─────────
    async void Fix_Click(object sender, RoutedEventArgs e) => await FixSelected(confirm: true);

    async Task<RegFixResult?> FixSelected(bool confirm)
    {
        if (_busy) return null;
        var targets = _issues.Where(i => i.Selected && i.CanSelect).ToList();
        if (targets.Count == 0) return null;

        if (confirm)
        {
            string breakdown = string.Join("\n", targets.GroupBy(t => t.Category).Select(g => $"•  {g.Key.Name}: {g.Count()}"));
            bool ok = await MainWindow.Instance.Confirm($"Remove {targets.Count} registry {(targets.Count == 1 ? "entry" : "entries")}?",
                breakdown + "\n\nA backup is saved first. You can put everything back from the Backups list on this page.", "Remove", danger: true);
            if (!ok) return null;
        }

        SetBusy(true, "Saving a backup and removing…");
        RegFixResult result;
        try { result = await Task.Run(() => RegistryFixer.Fix(targets)); }
        catch (Exception ex) { result = new RegFixResult(0, targets.Count, null, ex.Message); }
        finally { SetBusy(false); }

        LoadBackups();
        if (result.Error != null) MainWindow.Instance.Toast(result.Error, true);
        else if (result.Failed > 0) MainWindow.Instance.Toast($"Removed {result.Removed}. {result.Failed} could not be removed because Windows protects them. A backup was saved.", true);
        else MainWindow.Instance.Toast($"Removed {result.Removed} {(result.Removed == 1 ? "entry" : "entries")}. A backup was saved.");
        await ScanAsync();
        return result;
    }

    /// <summary>Developer: scans, selects one category only and fixes it, exactly as the Fix button would after its confirmation.</summary>
    public async Task<string> DevFix(string categoryId)
    {
        await ScanAsync();
        foreach (var issue in _issues) issue.Selected = issue.CanSelect && issue.Category.Id == categoryId;
        int selected = _issues.Count(i => i.Selected);
        var result = await FixSelected(confirm: false);
        return result == null
            ? $"fix {categoryId}: nothing selected"
            : $"fix {categoryId}: selected {selected}, removed {result.Removed}, failed {result.Failed}, error {result.Error ?? "none"}, left after rescan {_issues.Count(i => i.Category.Id == categoryId)}, backup {result.BackupFile}";
    }

    /// <summary>Developer: restores the newest backup, exactly as its Restore button would after its confirmation.</summary>
    public async Task<string> DevRestoreLatest()
    {
        await ScanAsync();
        if ((BackupList.ItemsSource as List<BackupRow>)?.FirstOrDefault() is not { } row) return "restore: no backup found";
        string? error = await RestoreBackup(row, confirm: false);
        return $"restore: {row.Backup.Entries} entries from {System.IO.Path.GetFileName(row.Backup.File)}, error {error ?? "none"}, found by rescan {_issues.Count}";
    }

    // ───────── Backups ─────────
    void LoadBackups()
    {
        var rows = RegistryFixer.ListBackups().Select(b => new BackupRow(b,
            b.When.ToString("d MMMM yyyy, HH:mm"),
            $"{b.Entries} {(b.Entries == 1 ? "entry" : "entries")}  ·  {Fmt.Bytes(b.Size)}",
            b.NeedsAdmin && !Sys.IsAdmin)).ToList();
        BackupList.ItemsSource = rows;
        NoBackups.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is BackupRow row) await RestoreBackup(row, confirm: true);
    }

    async Task<string?> RestoreBackup(BackupRow row, bool confirm)
    {
        if (_busy) return "busy";
        if (row.Locked)
        {
            MainWindow.Instance.Toast("This backup contains entries shared by all users. Restart CoreWatch as administrator to restore it.", true);
            return "needs administrator";
        }
        if (confirm)
        {
            bool ok = await MainWindow.Instance.Confirm($"Restore {row.Backup.Entries} registry {(row.Backup.Entries == 1 ? "entry" : "entries")}?",
                $"Backup from {row.Title}.\n\nEach entry is written back exactly as it was when it was removed.", "Restore");
            if (!ok) return "cancelled";
        }

        SetBusy(true, "Restoring…");
        string? error;
        try { error = await Task.Run(() => RegistryFixer.Restore(row.Backup.File)); }
        finally { SetBusy(false); }

        if (error != null) MainWindow.Instance.Toast("Restore failed: " + error, true);
        else MainWindow.Instance.Toast($"Restored {row.Backup.Entries} {(row.Backup.Entries == 1 ? "entry" : "entries")}.");
        if (_scanned) await ScanAsync();
        else { TotalLabel.Text = "Not scanned yet"; TotalDetail.Text = "Scan to look for leftover entries. Nothing is changed until you press Fix."; }
        return error;
    }

    void ShowBackup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is BackupRow row) Sys.OpenInExplorer(row.Backup.File);
    }

    void OpenRegedit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is RegIssue issue) RegistryFixer.OpenInRegedit(issue.FullKey);
    }

    void Admin_Click(object sender, RoutedEventArgs e)
    {
        if (!Sys.RestartAsAdmin("registry")) MainWindow.Instance.Toast("CoreWatch was not restarted.", true);
    }
}
