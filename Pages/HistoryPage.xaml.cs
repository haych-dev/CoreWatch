using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CoreWatch.Controls;
using CoreWatch.Services;

namespace CoreWatch.Pages;

public partial class HistoryPage : UserControl, IPage
{
    sealed record Metric(string Title, string ColorKey, Func<MetricPoint, double> Pick, Func<double, string> Format, double Maximum, double MinAuto = 1);

    static readonly Metric[] Metrics =
    [
        new("Processor", "C.Cpu", p => p.Cpu, Fmt.Pct, 100),
        new("Memory", "C.Mem", p => p.Mem, Fmt.Pct, 100),
        new("Graphics", "C.Gpu", p => p.Gpu, Fmt.Pct, 100),
        new("Video memory", "C.Vram", p => p.Vram, Fmt.Pct, 100),
        new("Graphics temperature", "C.Warn", p => p.GpuTemp, v => $"{v:0} °C", 0, 60),
        new("Download", "C.Net", p => p.Down, Fmt.Rate, 0, 64 * 1024),
        new("Upload", "C.Disk", p => p.Up, Fmt.Rate, 0, 64 * 1024),
    ];

    int _hours = 24;
    bool _ready;
    DateTime _loaded;

    public HistoryPage()
    {
        InitializeComponent();
        _ready = true;
    }

    public void OnShown() => _ = Reload();

    public void OnTick(Snapshot s)
    {
        // A new reading lands once a minute
        if ((DateTime.Now - _loaded).TotalSeconds >= 60) _ = Reload();
    }

    void Range_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out int hours)) return;
        _hours = hours;
        _ = Reload();
    }

    async Task Reload()
    {
        _loaded = DateTime.Now;
        DateTime to = DateTime.Now, from = to.AddHours(-_hours);
        var points = App.DevHistoryDemo ? Demo(from, to) : await Task.Run(() => MetricLog.Load(from));

        Charts.Children.Clear();
        EmptyCard.Visibility = points.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (points.Count == 0)
        {
            SubTitle.Text = "CPU, memory, graphics and network readings over hours and days.";
            EmptyText.Text = Settings.Current.RecordHistory
                ? "Recording has started. The first reading appears after a minute; leave CoreWatch running to build up a picture."
                : "Recording is switched off. Turn on \"Record history\" in Settings to start.";
            return;
        }

        double covered = points.Count / 60.0;
        SubTitle.Text = $"{Fmt.Count(points.Count)} readings in this period, covering about {(covered >= 1.5 ? $"{covered:0} hours" : $"{points.Count} minutes")} of running time.";

        foreach (var metric in Metrics)
        {
            var values = points.Select(p => (p.Time, Value: metric.Pick(p))).Where(p => !double.IsNaN(p.Value)).ToList();
            if (values.Count == 0) continue;
            double avg = values.Average(v => v.Value), peak = values.Max(v => v.Value);

            var chart = new TimeChart { Height = 150, Color = Ui.Color(metric.ColorKey), Maximum = metric.Maximum, MinAutoMaximum = metric.MinAuto };
            chart.Show(values, from, to);

            var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(Ui.Color(metric.ColorKey)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 9, 0) });
            title.Children.Add(new TextBlock { Text = metric.Title, Style = Ui.Style("T.Card") });
            header.Children.Add(title);
            header.Children.Add(new TextBlock
            {
                Text = $"Average {metric.Format(avg)}   ·   Peak {metric.Format(peak)}" + (metric.Maximum == 0 ? $"   ·   Scale 0 to {metric.Format(chart.EffectiveMaximum)}" : ""),
                Style = Ui.Style("T.Label"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            });

            var body = new StackPanel();
            body.Children.Add(header);
            body.Children.Add(chart);
            Charts.Children.Add(new Border { Style = Ui.Style("Card"), Margin = new Thickness(0, 0, 0, 12), Child = body });
        }
    }

    /// <summary>Developer: made-up readings with a few gaps, to check the charts without waiting for real data.</summary>
    static List<MetricPoint> Demo(DateTime from, DateTime to)
    {
        var list = new List<MetricPoint>();
        var random = new Random(7);
        for (var t = from; t < to; t = t.AddMinutes(1))
        {
            double hour = t.TimeOfDay.TotalHours;
            if (hour is < 7.5 or > 23.2 || (t.DayOfYear % 6 == 0 && hour is > 12 and < 17)) continue; // PC off
            double busy = 0.5 + 0.5 * Math.Sin(hour / 24 * Math.PI * 4 + t.DayOfYear);
            list.Add(new MetricPoint(t, 8 + 45 * busy * random.NextDouble(), 38 + 30 * busy + 4 * random.NextDouble(),
                60 * busy * random.NextDouble(), 42 + 30 * busy + 2 * random.NextDouble(), 20 + 45 * busy,
                4e6 * busy * random.NextDouble(), 6e5 * busy * random.NextDouble()));
        }
        return list;
    }
}
