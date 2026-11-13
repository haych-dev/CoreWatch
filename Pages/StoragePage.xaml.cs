using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CoreWatch.Controls;
using CoreWatch.Services;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace CoreWatch.Pages;

public sealed record VolumeCard(string Title, string PctText, double Pct, Brush Bar, string Detail, string Format, string Path);
public sealed record PhysRow(string Name, string Kind, string SizeText, string Health, Brush HealthBrush);
public sealed record CrumbItem(string Name, DirNode Node, Visibility Chevron);
public sealed record NodeRow(DirNode Node, string Glyph, Brush GlyphBrush, string Name, double Pct, string ShareText, string SizeText, string Tip);
public sealed record FileRow(string Path, string Name, string Folder, string SizeText, long Size);

public partial class StoragePage : UserControl, IPage
{
    string _driveSignature = "";
    UniformGrid? _drivePanel;
    DirNode? _root, _current;
    CancellationTokenSource? _cts;
    readonly ObservableCollection<FileRow> _files = [];
    bool _physLoaded;

    public StoragePage()
    {
        InitializeComponent();
        FileList.ItemsSource = _files;
        Map.NodeClicked += n => { if (!n.IsFile) ShowNode(n); else Sys.OpenInExplorer(n.Path); };
        PathBox.Text = Settings.Current.LastScanPath is { Length: > 0 } last && Directory.Exists(last)
            ? last : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public void OnShown()
    {
        if (_physLoaded) return;
        _physLoaded = true;
        Task.Run(LoadPhysicalDisks);
        if (App.DevScanPath != null)
        {
            PathBox.Text = App.DevScanPath;
            _ = StartScan();
        }
    }

    public void OnTick(Snapshot s)
    {
        string sig = string.Join("|", s.Drives.Select(d => $"{d.Name}{d.Free >> 26}"));
        if (sig == _driveSignature) return;
        _driveSignature = sig;

        DriveCards.ItemsSource = s.Drives.Select(d =>
        {
            var row = DashboardPage.ToRow(d);
            return new VolumeCard(row.Title, $"{d.UsedPct:0}%", d.UsedPct, row.Bar, $"{Fmt.Bytes(d.Free)} free of {Fmt.Bytes(d.Total)}",
                $"{d.Format}  ·  {(d.Type == DriveType.Removable ? "Removable" : "Fixed")}", d.Name);
        }).ToList();
        if (_drivePanel != null) _drivePanel.Columns = Math.Clamp(s.Drives.Count, 2, 4);

        long total = s.Drives.Sum(d => d.Total), free = s.Drives.Sum(d => d.Free);
        SubTitle.Text = $"{s.Drives.Count} volumes  ·  {Fmt.Bytes(total - free)} used of {Fmt.Bytes(total)}  ·  {Fmt.Bytes(free)} free";
    }

    void DrivePanel_Loaded(object sender, RoutedEventArgs e)
    {
        _drivePanel = (UniformGrid)sender;
        _drivePanel.Columns = Math.Clamp(DriveCards.Items.Count, 2, 4);
    }

    void LoadPhysicalDisks()
    {
        var rows = new List<PhysRow>();
        try
        {
            using var q = new System.Management.ManagementObjectSearcher(@"\\.\root\Microsoft\Windows\Storage",
                "SELECT FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk");
            foreach (var o in q.Get())
            {
                int media = Convert.ToInt32(o["MediaType"] ?? 0), bus = Convert.ToInt32(o["BusType"] ?? 0), health = Convert.ToInt32(o["HealthStatus"] ?? 0);
                string busName = bus switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 10 => "SAS", 8 => "RAID", 12 => "SD", 3 => "ATA", _ => "" };
                string mediaName = media switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => bus == 17 ? "SSD" : "Disk" };
                rows.Add(new PhysRow(
                    (o["FriendlyName"] as string ?? "Disk").Trim(),
                    $"{busName} {mediaName}".Trim(),
                    Fmt.Bytes(Convert.ToInt64(o["Size"] ?? 0L), 0),
                    health switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" },
                    Ui.Brush(health switch { 0 => "B.Good", 1 => "B.Warn", 2 => "B.Bad", _ => "B.Faint" })));
            }
        }
        catch { }
        Dispatcher.BeginInvoke(() =>
        {
            PhysList.ItemsSource = rows;
            PhysCard.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    // ───────── Analyzer ─────────
    void AnalyzeDrive_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string path) return;
        PathBox.Text = path;
        AnalyzerCard.BringIntoView();
        _ = StartScan();
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder to analyze" };
        if (Directory.Exists(PathBox.Text)) dlg.InitialDirectory = PathBox.Text;
        if (dlg.ShowDialog() == true) { PathBox.Text = dlg.FolderName; _ = StartScan(); }
    }

    void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = StartScan();
    }

    void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) _cts.Cancel();
        else _ = StartScan();
    }

    async Task StartScan()
    {
        if (_cts != null) return;
        string path = PathBox.Text.Trim().Trim('"');
        if (path.Length == 2 && path[1] == ':') path += "\\";
        if (!Directory.Exists(path))
        {
            MainWindow.Instance.Toast("That folder does not exist.", true);
            return;
        }
        Settings.Current.LastScanPath = path;

        _cts = new CancellationTokenSource();
        var progress = new ScanProgress();
        ScanButton.Content = "Cancel";
        ScanButton.Style = Ui.Style("Btn");
        ProgressPanel.Visibility = Visibility.Visible;
        ResultPanel.Visibility = Visibility.Collapsed;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            ProgressText.Text = $"{Fmt.Count(Interlocked.Read(ref progress.Files))} files  ·  {Fmt.Bytes(Interlocked.Read(ref progress.Bytes))}";
            ProgressPath.Text = progress.Current;
        };
        timer.Start();

        try
        {
            var token = _cts.Token;
            var started = DateTime.Now;
            var (root, largest) = await Task.Run(() => DiskScanner.Scan(path, progress, token));
            if (token.IsCancellationRequested)
            {
                MainWindow.Instance.Toast("Scan cancelled.", true);
                return;
            }
            _root = root;
            _files.Clear();
            foreach (var f in largest)
                _files.Add(new FileRow(f.Path, Path.GetFileName(f.Path), Path.GetDirectoryName(f.Path) ?? "", Fmt.Bytes(f.Size), f.Size));
            ResultPanel.Visibility = Visibility.Visible;
            ShowNode(root);
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => AnalyzerCard.BringIntoView());
            MainWindow.Instance.Toast($"Scanned {Fmt.Count(root.FileCount)} files ({Fmt.Bytes(root.Size)}) in {(DateTime.Now - started).TotalSeconds:0.0}s.");
        }
        catch (Exception ex)
        {
            MainWindow.Instance.Toast("Scan failed: " + ex.Message, true);
        }
        finally
        {
            timer.Stop();
            _cts.Dispose();
            _cts = null;
            ScanButton.Content = "Scan";
            ScanButton.Style = Ui.Style("BtnPrimary");
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    void ShowNode(DirNode node)
    {
        _current = node;
        Map.Show(node);

        var chain = new List<DirNode>();
        for (var n = node; n != null; n = n.Parent) chain.Add(n);
        chain.Reverse();
        Crumbs.ItemsSource = chain.Select((n, i) => new CrumbItem(n.Name, n, i == chain.Count - 1 ? Visibility.Collapsed : Visibility.Visible)).ToList();
        CurrentSummary.Text = $"{Fmt.Bytes(node.Size)}  ·  {Fmt.Count(node.FileCount)} files";

        double max = node.Children.Count > 0 ? Math.Max(1, node.Children[0].Size) : 1;
        Brush folder = Ui.Brush("B.Disk"), file = Ui.Brush("B.Faint");
        NodeList.ItemsSource = node.Children.Where(c => c.Size > 0).Take(250).Select(c => new NodeRow(
            c, c.IsFile ? "\uE7C3" : "\uE8B7", c.IsFile ? file : folder, c.Name,
            100.0 * c.Size / max,
            node.Size > 0 ? $"{100.0 * c.Size / node.Size:0.0}%" : "",
            Fmt.Bytes(c.Size),
            c.IsFile ? c.Path : $"{c.Path}\n{Fmt.Count(c.FileCount)} files")).ToList();
    }

    void Crumb_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DirNode n) ShowNode(n);
    }

    void Node_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not DirNode n) return;
        if (n.IsFile) Sys.OpenInExplorer(n.Path);
        else ShowNode(n);
    }

    void OpenCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_current != null) Sys.OpenInExplorer(_current.Path);
    }

    void FileOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is FileRow f) Sys.OpenInExplorer(f.Path);
    }

    async void FileDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not FileRow f) return;
        bool ok = await MainWindow.Instance.Confirm($"Move {f.Name} to the Recycle Bin?",
            $"{f.SizeText}\n{f.Path}\n\nYou can restore it from the Recycle Bin until the bin is emptied.", "Move to bin", danger: true);
        if (!ok) return;
        try
        {
            await Task.Run(() => FileSystem.DeleteFile(f.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin));
            _files.Remove(f);
            MainWindow.Instance.Toast($"Moved {f.Name} ({f.SizeText}) to the Recycle Bin.");
        }
        catch (Exception ex)
        {
            MainWindow.Instance.Toast("Could not delete the file: " + ex.Message, true);
        }
    }

    /// <summary>Lets the page keep scrolling when an inner list has reached its end.</summary>
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
