using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CoreWatch.Services;

namespace CoreWatch.Controls;

/// <summary>Scrolling line chart over one or more history buffers. Newest sample sits at the right edge.</summary>
public sealed class LineChart : FrameworkElement
{
    readonly List<(RingBuffer Data, Color Color, bool Fill)> _series = [];

    /// <summary>Fixed vertical maximum. 0 means auto-scale to the visible data.</summary>
    public double Maximum { get; set; } = 100;
    public double MinAutoMaximum { get; set; } = 1;
    public bool ShowGrid { get; set; } = true;
    public double StrokeThickness { get; set; } = 1.6;

    public void SetSeries(params (RingBuffer Data, Color Color, bool Fill)[] series)
    {
        _series.Clear();
        _series.AddRange(series);
        InvalidateVisual();
    }

    public void Refresh() => InvalidateVisual();

    /// <summary>The vertical maximum currently used (useful for auto-scaled axis labels).</summary>
    public double EffectiveMaximum
    {
        get
        {
            if (Maximum > 0) return Maximum;
            double m = MinAutoMaximum;
            foreach (var s in _series) m = Math.Max(m, s.Data.Max());
            return m * 1.15;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;

        if (ShowGrid)
        {
            var grid = new Pen(new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)), 1);
            grid.Freeze();
            for (int i = 1; i < 4; i++)
            {
                double y = Math.Round(h * i / 4) + 0.5;
                dc.DrawLine(grid, new Point(0, y), new Point(w, y));
            }
        }

        double max = EffectiveMaximum;
        if (max <= 0) return;

        foreach (var (data, color, fill) in _series)
        {
            int n = data.Count;
            if (n < 2) continue;
            double step = w / (data.Capacity - 1);
            double x0 = w - (n - 1) * step;

            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(new Point(x0, Y(data[0])), false, false);
                for (int i = 1; i < n; i++) g.LineTo(new Point(x0 + i * step, Y(data[i])), true, true);
            }
            line.Freeze();

            if (fill)
            {
                var area = new StreamGeometry();
                using (var g = area.Open())
                {
                    g.BeginFigure(new Point(x0, h), true, true);
                    for (int i = 0; i < n; i++) g.LineTo(new Point(x0 + i * step, Y(data[i])), false, false);
                    g.LineTo(new Point(w, h), false, false);
                }
                area.Freeze();
                var brush = new LinearGradientBrush(Color.FromArgb(70, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), 90);
                brush.Freeze();
                dc.DrawGeometry(brush, null, area);
            }

            var pen = new Pen(new SolidColorBrush(color), StrokeThickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, line);
        }

        double Y(double v) => h - 1 - Math.Clamp(v / max, 0, 1) * (h - 3);
    }
}

/// <summary>270° arc gauge. Value is 0–100 and animates between updates.</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Color Color { get; set; } = Color.FromRgb(0x4F, 0xB6, 0xFF);
    public double Thickness { get; set; } = 9;

    public void AnimateTo(double value)
    {
        value = Math.Clamp(value, 0, 100);
        var anim = new DoubleAnimation(value, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        BeginAnimation(ValueProperty, anim);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 10) return;
        double r = (size - Thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);

        var track = new Pen(new SolidColorBrush(Color.FromRgb(0x26, 0x2A, 0x32)), Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, track, Arc(c, r, 270));

        double v = Math.Clamp(Value, 0, 100);
        if (v > 0.4)
        {
            var pen = new Pen(new SolidColorBrush(Color), Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, pen, Arc(c, r, 270 * v / 100));
        }
    }

    static Geometry Arc(Point c, double r, double sweep)
    {
        const double start = 135;
        Point P(double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(start), false, false);
            ctx.ArcTo(P(start + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        return g;
    }
}

/// <summary>Grid of per-core load tiles.</summary>
public sealed class CoreTiles : FrameworkElement
{
    double[] _values = [];
    public Color Color { get; set; } = Color.FromRgb(0x4F, 0xB6, 0xFF);

    public void Update(double[] values)
    {
        _values = values;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        int n = _values.Length;
        double w = ActualWidth, h = ActualHeight;
        if (n == 0 || w < 10 || h < 10) return;

        int rows = n <= 8 ? 1 : n <= 32 ? 2 : n <= 64 ? 4 : 6;
        int cols = (int)Math.Ceiling(n / (double)rows);
        const double gap = 6;
        double tw = (w - gap * (cols - 1)) / cols, th = (h - gap * (rows - 1)) / rows;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var track = new SolidColorBrush(Color.FromRgb(0x1D, 0x20, 0x26));
        var fill = new SolidColorBrush(Color.FromArgb(215, Color.R, Color.G, Color.B));
        var text = new SolidColorBrush(Color.FromRgb(0xEC, 0xEE, 0xF2));
        var dim = new SolidColorBrush(Color.FromRgb(0x62, 0x6A, 0x78));
        var face = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        for (int i = 0; i < n; i++)
        {
            var rect = new Rect((i % cols) * (tw + gap), (i / cols) * (th + gap), tw, th);
            var clip = new RectangleGeometry(rect, 5, 5);
            dc.DrawGeometry(track, null, clip);
            double v = Math.Clamp(_values[i], 0, 100);
            dc.PushClip(clip);
            dc.DrawRectangle(fill, null, new Rect(rect.X, rect.Bottom - rect.Height * v / 100, rect.Width, rect.Height * v / 100));
            dc.Pop();

            if (tw >= 30 && th >= 34)
            {
                var pct = new FormattedText(Math.Round(v).ToString("0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 12.5, text, dpi);
                dc.DrawText(pct, new Point(rect.X + (tw - pct.Width) / 2, rect.Y + (th - pct.Height) / 2 - 1));
                var idx = new FormattedText(i.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 9, dim, dpi);
                dc.DrawText(idx, new Point(rect.X + 5, rect.Y + 3));
            }
        }
    }
}

/// <summary>Horizontal stacked bar (e.g. memory composition).</summary>
public sealed class SegmentBar : FrameworkElement
{
    (double Value, Color Color)[] _segments = [];

    public void Update(params (double Value, Color Color)[] segments)
    {
        _segments = segments;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 2) return;
        double total = _segments.Sum(s => Math.Max(0, s.Value));
        var clip = new RectangleGeometry(new Rect(0, 0, w, h), h / 2, h / 2);
        dc.PushClip(clip);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x26, 0x2A, 0x32)), null, new Rect(0, 0, w, h));
        if (total > 0)
        {
            double x = 0;
            foreach (var (value, color) in _segments)
            {
                double sw = w * Math.Max(0, value) / total;
                if (sw <= 0) continue;
                dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(x, 0, Math.Max(0, sw - 2), h));
                x += sw;
            }
        }
        dc.Pop();
    }
}
