using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CoreWatch.Controls;

/// <summary>Read-only console pane that shows a command's output as it arrives. Safe to call from any thread.</summary>
public sealed class OutputLog : Border
{
    const int MaxLines = 500;
    readonly List<string> _lines = [];
    readonly TextBox _box = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        FontSize = 12,
        MinHeight = 120,
        MaxHeight = 280,
        Padding = new Thickness(0),
    };
    bool _pending;

    public OutputLog()
    {
        Style = Ui.Style("Card");
        Background = Ui.Brush("B.CardAlt");
        Padding = new Thickness(14, 10, 6, 10);
        _box.FontFamily = (FontFamily)Application.Current.FindResource("F.Mono");
        _box.Foreground = Ui.Brush("B.Dim");
        Child = _box;
    }

    public void Clear()
    {
        lock (_lines) _lines.Clear();
        Refresh();
    }

    public void Append(string line)
    {
        lock (_lines)
        {
            // A progress line ("[====  42.0%  ]", "  12%") replaces the previous one instead of piling up.
            if (IsProgress(line) && _lines.Count > 0 && IsProgress(_lines[^1])) _lines[^1] = line;
            else _lines.Add(line);
            if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
            if (_pending) return;
            _pending = true;
        }
        Dispatcher.BeginInvoke(Refresh);
    }

    static bool IsProgress(string line)
    {
        string t = line.Trim();
        return t.EndsWith('%') || (t.StartsWith('[') && t.Contains('%')) || t.StartsWith("Verification ", StringComparison.Ordinal);
    }

    void Refresh()
    {
        string text;
        lock (_lines) { _pending = false; text = string.Join(Environment.NewLine, _lines); }
        _box.Text = text;
        _box.ScrollToEnd();
    }
}
