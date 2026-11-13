using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CoreWatch.Controls;

/// <summary>Small helpers for building repeated UI pieces in code.</summary>
public static class Ui
{
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    public static Color Color(string key) => (Color)Application.Current.FindResource(key);
    public static Style Style(string key) => (Style)Application.Current.FindResource(key);

    /// <summary>Warning tone for a "how full is it" percentage.</summary>
    public static Brush UsageBrush(double pct, string normalKey = "B.Accent") =>
        Brush(pct >= 92 ? "B.Bad" : pct >= 82 ? "B.Warn" : normalKey);

    /// <summary>Adds a "label … value" row to a panel and returns the value text so it can be updated.</summary>
    public static TextBlock AddKeyValue(Panel panel, string label, string value = "—")
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, Style = Style("T.Label"), Margin = new Thickness(0, 0, 16, 0) });
        var v = new TextBlock { Text = value, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right };
        Grid.SetColumn(v, 1);
        grid.Children.Add(v);
        panel.Children.Add(grid);
        return v;
    }
}

/// <summary>Card with a ring gauge, a centred percentage and a detail line.</summary>
public sealed class GaugeCard : Border
{
    readonly RingGauge _gauge = new() { Width = 112, Height = 112, Thickness = 9 };
    readonly TextBlock _value = new() { FontSize = 25, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _unit = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) };
    readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeights.SemiBold };
    readonly TextBlock _detail = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
    readonly TextBlock _detail2 = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
    readonly Ellipse _dot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) };

    public GaugeCard()
    {
        Style = Ui.Style("Card");
        Padding = new Thickness(16, 14, 16, 16);
        _value.FontFamily = (FontFamily)Application.Current.FindResource("F.Display");
        _unit.Foreground = _detail2.Foreground = Ui.Brush("B.Faint");
        _detail.Foreground = Ui.Brush("B.Dim");

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(_dot);
        header.Children.Add(_title);

        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(_value);
        center.Children.Add(_unit);
        var dial = new Grid { Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        dial.Children.Add(_gauge);
        dial.Children.Add(center);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(dial);
        stack.Children.Add(_detail);
        stack.Children.Add(_detail2);
        Child = stack;
    }

    public string Title { get => _title.Text; set => _title.Text = value; }
    public string Unit { get => _unit.Text; set => _unit.Text = value; }

    public Color Color
    {
        get => _gauge.Color;
        set { _gauge.Color = value; _dot.Fill = new SolidColorBrush(value); }
    }

    public void Set(double percent, string detail, string detail2 = "", string? centerText = null)
    {
        _gauge.AnimateTo(percent);
        _value.Text = centerText ?? Math.Round(percent).ToString("0") + "%";
        _detail.Text = detail;
        _detail2.Text = detail2;
    }
}
