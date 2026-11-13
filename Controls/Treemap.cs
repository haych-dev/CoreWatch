using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CoreWatch.Services;

namespace CoreWatch.Controls;

/// <summary>Squarified treemap of a folder's children. Click a folder tile to drill in.</summary>
public sealed class Treemap : FrameworkElement
{
    static readonly Color[] Palette =
    [
        Color.FromRgb(0x4F, 0xB6, 0xFF), Color.FromRgb(0xB5, 0x8C, 0xFF), Color.FromRgb(0x7B, 0xE0, 0x8A), Color.FromRgb(0x3E, 0xDB, 0xC8),
        Color.FromRgb(0xFF, 0xB4, 0x54), Color.FromRgb(0xFF, 0x7E, 0xB6), Color.FromRgb(0x8F, 0xA6, 0xFF), Color.FromRgb(0xE6, 0xD0, 0x6A),
    ];

    DirNode? _node;
    readonly List<(DirNode Node, Rect Rect, int Index)> _tiles = [];
    int _hover = -1;

    public event Action<DirNode>? NodeClicked;

    public Treemap()
    {
        ToolTipService.SetInitialShowDelay(this, 250);
        ToolTipService.SetBetweenShowDelay(this, 0);
    }

    bool _dirty = true;
    Size _layoutSize;

    public void Show(DirNode? node)
    {
        _node = node;
        _hover = -1;
        _dirty = true;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        InvalidateVisual();
    }

    void Layout()
    {
        _dirty = false;
        _layoutSize = RenderSize;
        _tiles.Clear();
        if (_node == null || ActualWidth < 20 || ActualHeight < 20) return;
        var items = _node.Children.Where(c => c.Size > 0).Take(80).ToList(); // already sorted by size, descending
        double total = items.Sum(i => (double)i.Size);
        if (total <= 0) return;

        var r = new Rect(0, 0, ActualWidth, ActualHeight);
        double scale = r.Width * r.Height / total;
        var areas = items.Select(i => i.Size * scale).ToList();

        int start = 0;
        while (start < items.Count)
        {
            double side = Math.Min(r.Width, r.Height);
            if (side <= 0.5) break;
            int end = start;
            double sum = 0, best = double.MaxValue, min = double.MaxValue, max = 0;
            while (end < items.Count)
            {
                double a = areas[end], nSum = sum + a, nMin = Math.Min(min, a), nMax = Math.Max(max, a);
                double worst = Math.Max(side * side * nMax / (nSum * nSum), nSum * nSum / (side * side * nMin));
                if (worst > best) break;
                best = worst; sum = nSum; min = nMin; max = nMax; end++;
            }

            double thickness = sum / side;
            if (r.Width >= r.Height)
            {
                double y = r.Y;
                for (int k = start; k < end; k++)
                {
                    double len = areas[k] / thickness;
                    _tiles.Add((items[k], new Rect(r.X, y, thickness, len), k));
                    y += len;
                }
                r = new Rect(r.X + thickness, r.Y, Math.Max(0, r.Width - thickness), r.Height);
            }
            else
            {
                double x = r.X;
                for (int k = start; k < end; k++)
                {
                    double len = areas[k] / thickness;
                    _tiles.Add((items[k], new Rect(x, r.Y, len, thickness), k));
                    x += len;
                }
                r = new Rect(r.X, r.Y + thickness, r.Width, Math.Max(0, r.Height - thickness));
            }
            start = end;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // hit-test surface
        if (_dirty || _layoutSize != RenderSize) Layout(); // layout here: the size is only final once rendering starts
        if (_tiles.Count == 0) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var faceLight = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var ink = new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x16));

        for (int t = 0; t < _tiles.Count; t++)
        {
            var (node, rect, index) = _tiles[t];
            var inner = new Rect(rect.X + 1, rect.Y + 1, Math.Max(0, rect.Width - 2), Math.Max(0, rect.Height - 2));
            if (inner.Width < 1 || inner.Height < 1) continue;

            Color baseColor = node.IsFile ? Color.FromRgb(0x6B, 0x74, 0x84) : Palette[index % Palette.Length];
            byte alpha = (byte)(t == _hover ? 255 : node.IsFile ? 150 : 205);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B)), null, inner, 4, 4);

            if (inner.Width > 64 && inner.Height > 22)
            {
                var name = new FormattedText(node.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 12, ink, dpi)
                { MaxTextWidth = inner.Width - 14, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(name, new Point(inner.X + 7, inner.Y + 5));
                if (inner.Height > 40)
                {
                    var size = new FormattedText(Fmt.Bytes(node.Size), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, faceLight, 11, ink, dpi)
                    { MaxTextWidth = inner.Width - 14, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                    dc.DrawText(size, new Point(inner.X + 7, inner.Y + 21));
                }
            }
        }
    }

    int HitIndex(Point p)
    {
        for (int i = 0; i < _tiles.Count; i++) if (_tiles[i].Rect.Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hit = HitIndex(e.GetPosition(this));
        if (hit == _hover) return;
        _hover = hit;
        if (hit >= 0)
        {
            var n = _tiles[hit].Node;
            double share = _node is { Size: > 0 } ? 100.0 * n.Size / _node.Size : 0;
            ToolTip = $"{n.Name}\n{Fmt.Bytes(n.Size)}  ·  {share:0.0}% of this folder" + (n.IsFile ? "" : $"  ·  {Fmt.Count(n.FileCount)} files");
            Cursor = n.IsFile ? Cursors.Arrow : Cursors.Hand;
        }
        else { ToolTip = null; Cursor = Cursors.Arrow; }
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = -1;
        ToolTip = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        int hit = HitIndex(e.GetPosition(this));
        if (hit >= 0) NodeClicked?.Invoke(_tiles[hit].Node);
    }
}
