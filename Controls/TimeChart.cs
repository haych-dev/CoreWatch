using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CoreWatch.Controls;

/// <summary>Line chart over a time range. Stretches without readings are left empty instead of being joined up.</summary>
public sealed class TimeChart : FrameworkElement
{
    List<(DateTime Time, double Value)> _points = [];
    DateTime _from, _to;

    public Color Color { get; set; } = Color.FromRgb(0x4F, 0xB6, 0xFF);
    /// <summary>Fixed vertical maximum. 0 scales to the data.</summary>
    public double Maximum { get; set; } = 100;
    public double MinAutoMaximum { get; set; } = 1;

    const double AxisHeight = 20;

    public void Show(IEnumerable<(DateTime Time, double Value)> points, DateTime from, DateTime to)
    {
        _points = points.Where(p => !double.IsNaN(p.Value)).ToList();
        (_from, _to) = (from, to);
        InvalidateVisual();
    }

    /// <summary>The vertical maximum in use, for the page's axis caption.</summary>
    public double EffectiveMaximum => Maximum > 0 ? Maximum : Math.Max(MinAutoMaximum, _points.Count > 0 ? _points.Max(p => p.Value) : 0) * 1.15;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight - AxisHeight;
        if (w < 40 || h < 20 || _to <= _from) return;

        var grid = new Pen(new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)), 1);
        grid.Freeze();
        for (int i = 0; i <= 4; i++)
        {
            double y = Math.Round(h * i / 4) + 0.5;
            dc.DrawLine(grid, new Point(0, y), new Point(w, y));
        }

        // Time labels along the bottom
        double span = (_to - _from).TotalSeconds;
        string format = span <= 26 * 3600 ? "HH:mm" : "d MMM";
        var face = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var dim = new SolidColorBrush(Color.FromRgb(0x62, 0x6A, 0x78));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        const int ticks = 5;
        for (int i = 0; i < ticks; i++)
        {
            var time = _from.AddSeconds(span * i / (ticks - 1));
            var text = new FormattedText(time.ToString(format, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 11, dim, dpi);
            double x = w * i / (ticks - 1);
            x = i == 0 ? 0 : i == ticks - 1 ? w - text.Width : x - text.Width / 2;
            dc.DrawText(text, new Point(x, h + 5));
        }

        if (_points.Count == 0) return;
        double max = EffectiveMaximum;
        if (max <= 0) return;

        // Average the readings that share a few pixels
        int buckets = Math.Clamp((int)(w / 3), 20, 700);
        var sum = new double[buckets];
        var count = new int[buckets];
        foreach (var (time, value) in _points)
        {
            int b = (int)((time - _from).TotalSeconds / span * buckets);
            if (b < 0 || b >= buckets) continue;
            sum[b] += value;
            count[b]++;
        }

        // Join neighbouring readings; start a new line where the gap is longer than a few minutes
        double bucketSeconds = span / buckets;
        int maxGap = Math.Max(2, (int)Math.Ceiling(Math.Max(180, bucketSeconds * 2.5) / bucketSeconds));
        var runs = new List<List<Point>>();
        List<Point>? run = null;
        int last = -1;
        for (int b = 0; b < buckets; b++)
        {
            if (count[b] == 0) continue;
            if (run == null || b - last > maxGap) runs.Add(run = []);
            double y = h - 1 - Math.Clamp(sum[b] / count[b] / max, 0, 1) * (h - 3);
            run.Add(new Point((b + 0.5) * w / buckets, y));
            last = b;
        }

        var pen = new Pen(new SolidColorBrush(Color), 1.6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        var fill = new LinearGradientBrush(Color.FromArgb(70, Color.R, Color.G, Color.B), Color.FromArgb(0, Color.R, Color.G, Color.B), 90);
        fill.Freeze();
        var dot = new SolidColorBrush(Color);

        foreach (var points in runs)
        {
            if (points.Count == 1)
            {
                dc.DrawEllipse(dot, null, points[0], 1.6, 1.6);
                continue;
            }
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(points[0].X, h), true, true);
                foreach (var p in points) g.LineTo(p, false, false);
                g.LineTo(new Point(points[^1].X, h), false, false);
            }
            area.Freeze();
            dc.DrawGeometry(fill, null, area);

            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(points[0], false, false);
                for (int i = 1; i < points.Count; i++) g.LineTo(points[i], true, true);
            }
            line.Freeze();
            dc.DrawGeometry(null, pen, line);
        }
    }
}

/// <summary>One bar per day (for example problems per day on the Reliability page). Newest day is on the right.</summary>
public sealed class DayBars : FrameworkElement
{
    (DateTime Day, int Count)[] _days = [];
    public Color Color { get; set; } = Color.FromRgb(0xFF, 0xB4, 0x54);

    public void Show((DateTime Day, int Count)[] days)
    {
        _days = days;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight - 20;
        int n = _days.Length;
        if (n == 0 || w < 40 || h < 20) return;

        int max = Math.Max(1, _days.Max(d => d.Count));
        double step = w / n, bar = Math.Max(2, step - 4);
        var track = new SolidColorBrush(Color.FromRgb(0x1D, 0x20, 0x26));
        var fill = new SolidColorBrush(Color);
        var face = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var dim = new SolidColorBrush(Color.FromRgb(0x62, 0x6A, 0x78));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int i = 0; i < n; i++)
        {
            double x = i * step + (step - bar) / 2;
            dc.DrawRoundedRectangle(track, null, new Rect(x, 0, bar, h), 3, 3);
            if (_days[i].Count > 0)
            {
                double bh = Math.Max(4, h * _days[i].Count / max);
                dc.DrawRoundedRectangle(fill, null, new Rect(x, h - bh, bar, bh), 3, 3);
            }
            if (i % 7 == (n - 1) % 7)
            {
                var text = new FormattedText(_days[i].Day.ToString("d MMM", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 11, dim, dpi);
                dc.DrawText(text, new Point(Math.Clamp(x + bar / 2 - text.Width / 2, 0, w - text.Width), h + 5));
            }
        }
    }
}
