using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CoreWatch.Controls;
using CoreWatch.Services;

namespace CoreWatch.Pages;

public partial class ReliabilityPage : UserControl, IPage
{
    bool _loaded;

    public ReliabilityPage() => InitializeComponent();

    public void OnShown()
    {
        if (!_loaded) _ = Reload();
    }

    async void Reload_Click(object sender, RoutedEventArgs e) => await Reload();

    async Task Reload()
    {
        _loaded = true;
        ReloadButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        ReliabilityReport report;
        try { report = await Task.Run(ReliabilityService.Load); }
        catch (Exception ex)
        {
            SubTitle.Text = "The event logs could not be read: " + ex.Message;
            Busy.Visibility = Visibility.Collapsed;
            ReloadButton.IsEnabled = true;
            return;
        }
        Busy.Visibility = Visibility.Collapsed;
        ReloadButton.IsEnabled = true;

        var events = report.Events;
        int Count(string kind) => events.Count(e => e.Kind == kind);
        int crashes = Count("App crash"), hangs = Count("App freeze"), screens = Count("Blue screen"), shutdowns = Count("Unexpected shutdown");

        SubTitle.Text = events.Count == 0
            ? $"Windows recorded no crashes, freezes or unexpected shutdowns in the last {ReliabilityService.Days} days."
            : $"{events.Count} {(events.Count == 1 ? "problem" : "problems")} recorded by Windows in the last {ReliabilityService.Days} days. Nothing here is changed, only read.";

        Tiles.Children.Clear();
        AddTile("App crashes", crashes, "B.Warn", "An app closed itself after an error");
        AddTile("App freezes", hangs, "B.Disk", "An app stopped responding and was closed");
        AddTile("Blue screens", screens, "B.Bad", "Windows itself stopped and restarted");
        AddTile("Unexpected shutdowns", shutdowns, "B.Bad", "Power was lost without shutting down");

        // One bar per day, oldest on the left
        var today = DateTime.Today;
        var days = Enumerable.Range(0, ReliabilityService.Days).Select(i => today.AddDays(i - ReliabilityService.Days + 1))
            .Select(day => (day, events.Count(e => e.Time.Date == day))).ToArray();
        Days.Show(days);
        int quiet = days.Count(d => d.Item2 == 0);
        DayText.Text = $"{quiet} of {days.Length} days without a problem";

        EventList.ItemsSource = events.Take(200).ToList();
        EventNote.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EventNote.Text = "Nothing to show. That is the result you want.";

        TopApps.Children.Clear();
        var top = events.Where(e => e.Kind is "App crash" or "App freeze")
            .GroupBy(e => e.Title.Replace(" stopped working", "").Replace(" stopped responding", ""), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Take(6).ToList();
        if (top.Count == 0) TopApps.Children.Add(new TextBlock { Text = "No app has crashed or frozen.", Style = Ui.Style("T.Sub") });
        foreach (var app in top) Ui.AddKeyValue(TopApps, app.Key, $"{app.Count()} {(app.Count() == 1 ? "time" : "times")}");

        BootList.Children.Clear();
        foreach (var boot in report.Boots.Take(8)) Ui.AddKeyValue(BootList, boot.When, boot.Text);
        if (report.Boots.Count > 1)
        {
            var average = Ui.AddKeyValue(BootList, "Average", $"{report.Boots.Average(b => b.Seconds):0} s");
            average.FontWeight = FontWeights.SemiBold;
        }
        BootNote.Visibility = report.BootNote != null ? Visibility.Visible : Visibility.Collapsed;
        BootNote.Text = report.BootNote ?? "";

        Footer.Text = "Start-up time is how long Windows took to reach a usable desktop, as measured by Windows itself." +
                      (report.Problems.Count > 0 ? "  Could not be read: " + string.Join("; ", report.Problems) + "." : "");
    }

    void AddTile(string label, int count, string tone, string hint)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = count.ToString(), Style = Ui.Style("T.Big"), Foreground = Ui.Brush(count == 0 ? "B.Good" : tone) });
        body.Children.Add(new TextBlock { Text = label, Style = Ui.Style("T.Card"), Margin = new Thickness(0, 2, 0, 0) });
        body.Children.Add(new TextBlock { Text = hint, Style = Ui.Style("T.Label"), Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
        Tiles.Children.Add(new Border { Style = Ui.Style("Card"), Margin = new Thickness(6, 0, 6, 0), Child = body });
    }

    void Inner_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer inner) return;
        bool atTop = inner.VerticalOffset <= 0 && e.Delta > 0;
        bool atBottom = inner.VerticalOffset >= inner.ScrollableHeight && e.Delta < 0;
        if (!atTop && !atBottom) return;
        e.Handled = true;
        Scroller.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
    }
}
