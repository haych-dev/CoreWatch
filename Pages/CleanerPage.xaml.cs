using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CoreWatch.Controls;
using CoreWatch.Services;

namespace CoreWatch.Pages;

public partial class CleanerPage : UserControl, IPage
{
    /// <summary>Bytes found by the most recent analysis (shown as an insight on the Overview page).</summary>
    public static long LastScanBytes { get; private set; }

    readonly List<CleanRule> _rules = CleanerService.BuildRules();
    bool _busy, _scanned;

    public CleanerPage()
    {
        InitializeComponent();

        var saved = Settings.Current.CleanerSelection;
        foreach (var rule in _rules)
        {
            rule.Selected = !rule.Locked && (saved.Count > 0 ? saved.Contains(rule.Id) : rule.DefaultOn);
            rule.PropertyChanged += Rule_Changed;
        }

        var titles = new Dictionary<string, string>
        {
            ["Windows"] = "Windows",
            ["Browsers"] = "Browsers",
            ["Apps"] = "Apps and developer tools",
        };
        foreach (var group in _rules.GroupBy(r => r.Group))
        {
            var header = new Grid { Margin = new Thickness(2, 22, 2, 8) };
            header.Children.Add(new TextBlock { Text = titles.GetValueOrDefault(group.Key, group.Key), Style = Ui.Style("T.Card") });
            var toggle = new Button { Content = "Select all", Style = Ui.Style("BtnGhost"), Foreground = Ui.Brush("B.Accent"), HorizontalAlignment = HorizontalAlignment.Right, Tag = group.Key, Margin = new Thickness(0, -5, 0, -5) };
            toggle.Click += ToggleGroup_Click;
            header.Children.Add(toggle);
            Groups.Children.Add(header);

            Groups.Children.Add(new Border
            {
                Style = Ui.Style("Card"),
                Padding = new Thickness(18, 6, 18, 6),
                Child = new ItemsControl { ItemsSource = group.ToList(), ItemTemplate = (DataTemplate)Resources["RuleTemplate"], Focusable = false },
            });
        }
    }

    void ToggleGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string key) return;
        var rules = _rules.Where(r => r.Group == key && !r.Locked).ToList();
        bool select = rules.Any(r => !r.Selected);
        foreach (var r in rules) r.Selected = select;
    }

    void Rule_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CleanRule.Selected)) return;
        Settings.Current.CleanerSelection = _rules.Where(r => r.Selected).Select(r => r.Id).ToList();
        UpdateSummary();
    }

    void UpdateSummary()
    {
        if (!_scanned) return;
        var selected = _rules.Where(r => r.Selected && r.Bytes > 0).ToList();
        long bytes = selected.Sum(r => r.Bytes), files = selected.Sum(r => r.Files);
        long everything = _rules.Where(r => r.Bytes > 0).Sum(r => r.Bytes);
        LastScanBytes = bytes;

        TotalText.Text = Fmt.Bytes(bytes);
        TotalText.Visibility = Visibility.Visible;
        TotalLabel.Text = bytes > 0 ? "ready to clean" : "Nothing to clean";
        TotalDetail.Text = bytes > 0
            ? $"{Fmt.Count(files)} items in {selected.Count} selected {(selected.Count == 1 ? "category" : "categories")}" +
              (everything > bytes ? $"  ·  {Fmt.Bytes(everything - bytes)} more in unselected ones" : "")
            : everything > 0 ? $"{Fmt.Bytes(everything)} found in categories you have not selected." : "Your selected categories are already clean.";
        CleanButton.IsEnabled = !_busy && bytes > 0;
        CleanButton.Style = Ui.Style(bytes > 0 ? "BtnPrimary" : "Btn");
        AnalyzeButton.Style = Ui.Style(bytes > 0 ? "Btn" : "BtnPrimary");
        AnalyzeButton.Content = "Analyze again";
    }

    void SetBusy(bool busy, string? label = null)
    {
        _busy = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Busy.IsIndeterminate = busy;
        AnalyzeButton.IsEnabled = !busy;
        if (busy) CleanButton.IsEnabled = false;
        if (label != null) { TotalLabel.Text = label; TotalDetail.Text = "This usually takes a few seconds."; }
    }

    async void Analyze_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    /// <summary>Starts an analysis (also triggered from the Overview page).</summary>
    public void StartScan() => _ = ScanAsync();

    public void OnShown()
    {
        if (App.DevAnalyze && !_scanned) StartScan();
    }

    async Task ScanAsync()
    {
        if (_busy) return;
        SetBusy(true, "Analyzing…");
        try
        {
            await Task.WhenAll(_rules.Select(async rule =>
            {
                var (bytes, files) = await Task.Run(() => CleanerService.Scan(rule, CancellationToken.None));
                rule.Files = files;
                rule.Bytes = bytes;
            }));
            _scanned = true;
        }
        finally
        {
            SetBusy(false);
            UpdateSummary();
        }
    }

    async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var targets = _rules.Where(r => r.Selected && r.Bytes > 0).ToList();
        if (targets.Count == 0) return;
        long expected = targets.Sum(r => r.Bytes);

        if (Settings.Current.ConfirmBeforeCleaning)
        {
            string names = string.Join(", ", targets.Take(4).Select(r => r.Name)) + (targets.Count > 4 ? $" and {targets.Count - 4} more" : "");
            bool ok = await MainWindow.Instance.Confirm($"Permanently delete {Fmt.Bytes(expected)}?",
                $"{names}.\n\nThese files are deleted for good, not moved to the Recycle Bin. Files that are in use are skipped.", "Clean now", danger: true);
            if (!ok) return;
        }

        SetBusy(true, "Cleaning…");
        long freed = 0, skipped = 0;
        try
        {
            foreach (var rule in targets)
            {
                var result = await Task.Run(() => CleanerService.Clean(rule, CancellationToken.None));
                freed += result.Freed;
                skipped += result.Skipped;
            }
        }
        finally { SetBusy(false); }

        MainWindow.Instance.Toast($"Freed {Fmt.Bytes(freed)}." + (skipped > 0 ? $" {Fmt.Count(skipped)} files were in use and skipped." : ""));
        await ScanAsync();
    }
}
