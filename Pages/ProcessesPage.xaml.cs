using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CoreWatch.Services;

namespace CoreWatch.Pages;

public sealed class ProcRow : INotifyPropertyChanged
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string? Path { get; set; }

    double _cpu;
    long _mem, _gpu;
    int _threads, _handles;
    ImageSource? _icon;

    public double Cpu { get => _cpu; set { if (Math.Abs(_cpu - value) < 0.05) return; _cpu = value; Raise(nameof(Cpu)); Raise(nameof(CpuText)); } }
    public long Mem { get => _mem; set { if (_mem == value) return; _mem = value; Raise(nameof(Mem)); Raise(nameof(MemText)); } }
    public long Gpu { get => _gpu; set { if (_gpu == value) return; _gpu = value; Raise(nameof(Gpu)); Raise(nameof(GpuText)); } }
    public int Threads { get => _threads; set { if (_threads == value) return; _threads = value; Raise(nameof(Threads)); } }
    public int Handles { get => _handles; set { if (_handles == value) return; _handles = value; Raise(nameof(Handles)); } }
    public ImageSource? Icon { get => _icon; set { _icon = value; Raise(nameof(Icon)); } }

    public string CpuText => _cpu < 0.05 ? "0%" : _cpu.ToString("0.0") + "%";
    public string MemText => Fmt.Bytes(_mem);
    public string GpuText => _gpu > 0 ? Fmt.Bytes(_gpu) : "";

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class ProcessesPage : UserControl, IPage
{
    readonly ObservableCollection<ProcRow> _rows = [];
    readonly Dictionary<int, ProcRow> _byPid = new();
    readonly ListCollectionView _view;
    string _filter = "";

    public bool WantsProcesses => true;

    public ProcessesPage()
    {
        InitializeComponent();
        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_rows);
        _view.IsLiveSorting = true;
        foreach (var p in new[] { nameof(ProcRow.Cpu), nameof(ProcRow.Mem), nameof(ProcRow.Gpu), nameof(ProcRow.Threads), nameof(ProcRow.Handles) })
            _view.LiveSortingProperties.Add(p);
        _view.SortDescriptions.Add(new SortDescription(nameof(ProcRow.Mem), ListSortDirection.Descending));
        _view.Filter = o => o is ProcRow r && (_filter.Length == 0 || r.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) || r.Pid.ToString().StartsWith(_filter));
        Table.ItemsSource = _view;
        MemColumn.SortDirection = ListSortDirection.Descending;
    }

    public void OnTick(Snapshot s)
    {
        if (s.Processes == null) return;
        var seen = new HashSet<int>(s.Processes.Count);
        List<ProcRow>? added = null;

        foreach (var p in s.Processes)
        {
            seen.Add(p.Pid);
            if (!_byPid.TryGetValue(p.Pid, out var row) || row.Name != p.Name)
            {
                if (row != null) _rows.Remove(row); // PID was reused by a different program
                row = new ProcRow { Pid = p.Pid, Name = p.Name };
                _byPid[p.Pid] = row;
                Apply(row, p);
                _rows.Add(row);
                (added ??= []).Add(row);
            }
            else Apply(row, p);
        }

        foreach (var pid in _byPid.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _rows.Remove(_byPid[pid]);
            _byPid.Remove(pid);
        }

        if (added != null) LoadIcons(added);
        SubTitle.Text = $"{s.Processes.Count} running  ·  {Fmt.Count(s.ThreadCount)} threads  ·  CPU {Fmt.Pct(s.CpuTotal)}  ·  Memory {Fmt.Pct(s.MemPct)}";

        static void Apply(ProcRow row, ProcSample p)
        {
            row.Cpu = p.Cpu;
            row.Mem = p.PrivateWs;
            row.Gpu = p.GpuMem;
            row.Threads = p.Threads;
            row.Handles = p.Handles;
        }
    }

    void LoadIcons(List<ProcRow> rows)
    {
        Task.Run(() =>
        {
            foreach (var row in rows)
            {
                var path = Native.GetProcessPath(row.Pid);
                if (path == null) continue;
                var icon = IconCache.Get(path);
                Dispatcher.BeginInvoke(() => { row.Path = path; row.Icon = icon; });
            }
        });
    }

    ProcRow? Selected => Table.SelectedItem as ProcRow;

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = Search.Text.Trim();
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
    }

    void Table_SelectionChanged(object sender, SelectionChangedEventArgs e) => EndButton.IsEnabled = Selected != null;

    void Table_Sorting(object sender, DataGridSortingEventArgs e)
    {
        // Numbers are most useful largest-first, so start those columns descending.
        if (e.Column.SortDirection != null || e.Column.SortMemberPath == nameof(ProcRow.Name)) return;
        e.Handled = true;
        foreach (var c in Table.Columns) c.SortDirection = null;
        e.Column.SortDirection = ListSortDirection.Descending;
        _view.SortDescriptions.Clear();
        _view.SortDescriptions.Add(new SortDescription(e.Column.SortMemberPath, ListSortDirection.Descending));
    }

    void Table_RightDown(object sender, MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d != null && d is not DataGridRow) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        if (d is DataGridRow row) row.IsSelected = true;
    }

    async void EndTask_Click(object sender, RoutedEventArgs e) => await End(false);
    async void EndTree_Click(object sender, RoutedEventArgs e) => await End(true);

    async Task End(bool tree)
    {
        if (Selected is not { } row) return;
        bool ok = await MainWindow.Instance.Confirm(
            tree ? $"End {row.Name} and everything it started?" : $"End {row.Name}?",
            $"PID {row.Pid}. Any unsaved work in this app will be lost.", tree ? "End tree" : "End task", danger: true);
        if (!ok) return;
        try
        {
            using var p = Process.GetProcessById(row.Pid);
            p.Kill(tree);
            MainWindow.Instance.Toast($"Ended {row.Name} (PID {row.Pid}).");
        }
        catch (Exception ex)
        {
            MainWindow.Instance.Toast($"Could not end {row.Name}: {ex.Message}", true);
        }
    }

    void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var path = row.Path ?? Native.GetProcessPath(row.Pid);
        if (path != null) Sys.OpenInExplorer(path);
        else MainWindow.Instance.Toast("Windows does not expose the path of this process.", true);
    }

    void CopyName_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) try { Clipboard.SetText(row.Name); } catch { }
    }
}
