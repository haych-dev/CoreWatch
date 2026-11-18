using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CoreWatch.Controls;
using CoreWatch.Services;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace CoreWatch.Pages;

public partial class DuplicatesPage : UserControl, IPage
{
    const int MaxGroups = 150, MaxOld = 200;

    List<DupGroup> _groups = [];
    List<OldFile> _old = [];
    CancellationTokenSource? _cts;
    int _oldMonths = 12;
    bool _ready, _devStarted;

    public DuplicatesPage()
    {
        InitializeComponent();
        PathBox.Text = Settings.Current.LastDuplicatePath is { Length: > 0 } last && Directory.Exists(last)
            ? last : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _ready = true;
    }

    public void OnShown()
    {
        if (App.DevDuplicatePath == null || _devStarted) return;
        _devStarted = true;
        PathBox.Text = App.DevDuplicatePath;
        _ = StartScan();
    }

    // ───────── Scan ─────────
    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder to search" };
        if (Directory.Exists(PathBox.Text)) dlg.InitialDirectory = PathBox.Text;
        if (dlg.ShowDialog() == true) PathBox.Text = dlg.FolderName;
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
        if (!Directory.Exists(path)) { MainWindow.Instance.Toast("That folder does not exist.", true); return; }
        Settings.Current.LastDuplicatePath = path;

        long minSize = SizePanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string tag && long.TryParse(tag, out long v) ? v : 1 << 20;
        _cts = new CancellationTokenSource();
        var progress = new FinderProgress();
        ScanButton.Content = "Cancel";
        ScanButton.Style = Ui.Style("Btn");
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = true;
        Results.Visibility = Visibility.Collapsed;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            long total = Interlocked.Read(ref progress.HashTotal), hashed = Interlocked.Read(ref progress.Hashed);
            ProgressBar.IsIndeterminate = total == 0;
            if (total > 0) ProgressBar.Value = 100.0 * hashed / total;
            ProgressText.Text = total > 0
                ? $"{progress.Stage}  {Fmt.Bytes(hashed)} of {Fmt.Bytes(total)}"
                : $"{progress.Stage}  {Fmt.Count(Interlocked.Read(ref progress.Files))} files  ·  {Fmt.Bytes(Interlocked.Read(ref progress.Bytes))}";
            ProgressPath.Text = progress.Current;
        };
        timer.Start();

        try
        {
            var token = _cts.Token;
            var started = DateTime.Now;
            var (groups, old) = await Task.Run(() => FileFinder.Scan(path, minSize, progress, token));
            if (token.IsCancellationRequested) { MainWindow.Instance.Toast("Search cancelled.", true); return; }

            foreach (var file in _groups.SelectMany(g => g.Files)) file.PropertyChanged -= File_Changed;
            (_groups, _old) = (groups, old);
            foreach (var file in _groups.SelectMany(g => g.Files)) file.PropertyChanged += File_Changed;
            Results.Visibility = Visibility.Visible;
            ShowResults();
            MainWindow.Instance.Toast($"Checked {Fmt.Count(Interlocked.Read(ref progress.Files))} files in {(DateTime.Now - started).TotalSeconds:0.0}s: " +
                                      $"{_groups.Count} {(_groups.Count == 1 ? "set" : "sets")} of duplicates.");
        }
        catch (Exception ex) { MainWindow.Instance.Toast("The search failed: " + ex.Message, true); }
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

    // ───────── Results ─────────
    List<OldFile> OldShown() => _old.Where(o => o.Changed < DateTime.Now.AddMonths(-_oldMonths)).ToList();

    void ShowResults()
    {
        bool dup = TabDup.IsChecked == true;
        var oldShown = OldShown();
        TabDup.Content = $"Duplicate files ({_groups.Count})";
        TabOld.Content = $"Large old files ({oldShown.Count})";
        DupActions.Visibility = GroupList.Visibility = dup ? Visibility.Visible : Visibility.Collapsed;
        OldActions.Visibility = dup ? Visibility.Collapsed : Visibility.Visible;
        OldCard.Visibility = !dup && oldShown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (dup)
        {
            GroupList.ItemsSource = _groups.Take(MaxGroups).ToList();
            Summary.Text = _groups.Count == 0 ? "No duplicates found in this folder."
                : $"{_groups.Count} {(_groups.Count == 1 ? "set" : "sets")} of identical files. Keeping one copy of each would free {Fmt.Bytes(_groups.Sum(g => g.Wasted))}.";
            MoreNote.Visibility = _groups.Count > MaxGroups ? Visibility.Visible : Visibility.Collapsed;
            MoreNote.Text = $"Showing the {MaxGroups} sets that waste the most space.";
            UpdateDelete();
        }
        else
        {
            OldList.ItemsSource = oldShown.Take(MaxOld).ToList();
            Summary.Text = oldShown.Count == 0 ? $"No files of {Fmt.Bytes(FileFinder.OldMinSize, 0)} or more have gone that long without a change."
                : $"{oldShown.Count} {(oldShown.Count == 1 ? "file" : "files")} of {Fmt.Bytes(FileFinder.OldMinSize, 0)} or more, {Fmt.Bytes(oldShown.Sum(o => o.Size))} in total. The date is when each file was last changed, not when it was last opened.";
            MoreNote.Visibility = oldShown.Count > MaxOld ? Visibility.Visible : Visibility.Collapsed;
            MoreNote.Text = $"Showing the {MaxOld} largest.";
        }
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (_ready) ShowResults();
    }

    void Age_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out int months)) return;
        _oldMonths = months;
        ShowResults();
    }

    void File_Changed(object? sender, PropertyChangedEventArgs e) => UpdateDelete();

    List<DupFile> Selected() => _groups.SelectMany(g => g.Files).Where(f => f.Selected).ToList();

    void UpdateDelete()
    {
        var selected = Selected();
        DeleteButton.IsEnabled = selected.Count > 0;
        DeleteButton.Style = Ui.Style(selected.Count > 0 ? "BtnPrimary" : "Btn");
        DeleteButton.Content = selected.Count > 0 ? $"Move {selected.Count} to Recycle Bin ({Fmt.Bytes(selected.Sum(f => f.Group.Size))})" : "Move to Recycle Bin";
    }

    void SelectExtra_Click(object sender, RoutedEventArgs e)
    {
        // Files are ordered oldest first, so the first one in each set is the copy that stays.
        foreach (var group in _groups)
            for (int i = 0; i < group.Files.Count; i++) group.Files[i].Selected = i > 0;
    }

    void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var file in Selected()) file.Selected = false;
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string path) Sys.OpenInExplorer(path);
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        // A set where every copy is ticked keeps its oldest copy: this page never removes the last one.
        var targets = new List<DupFile>();
        int spared = 0;
        foreach (var group in _groups)
        {
            var picked = group.Files.Where(f => f.Selected).ToList();
            if (picked.Count == 0) continue;
            if (picked.Count == group.Files.Count) { picked.RemoveAt(0); spared++; }
            targets.AddRange(picked);
        }
        if (targets.Count == 0) { MainWindow.Instance.Toast("Every ticked file is the last copy of its set, so nothing was removed.", true); return; }

        bool ok = await MainWindow.Instance.Confirm($"Move {targets.Count} {(targets.Count == 1 ? "file" : "files")} to the Recycle Bin?",
            $"{Fmt.Bytes(targets.Sum(f => f.Group.Size))} in total. One copy of each set stays where it is." +
            (spared > 0 ? $"\n\nIn {spared} {(spared == 1 ? "set" : "sets")} you ticked every copy; the oldest one is kept." : "") +
            "\n\nYou can restore them from the Recycle Bin until it is emptied.", "Move to bin", danger: true);
        if (!ok) return;

        DeleteButton.IsEnabled = false;
        var (done, failed) = await Task.Run(() =>
        {
            var removed = new List<DupFile>();
            int errors = 0;
            foreach (var file in targets)
            {
                try
                {
                    // Check again that another copy is still on disk before this one goes.
                    if (!file.Group.Files.Any(other => other != file && !removed.Contains(other) && File.Exists(other.Path))) { errors++; continue; }
                    FileSystem.DeleteFile(file.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    removed.Add(file);
                }
                catch { errors++; }
            }
            return (removed, errors);
        });

        long freed = done.Sum(f => f.Group.Size);
        foreach (var file in done)
        {
            file.PropertyChanged -= File_Changed;
            file.Group.Files.Remove(file);
        }
        _groups = _groups.Where(g => g.Files.Count > 1).ToList();
        ShowResults();
        MainWindow.Instance.Toast($"Moved {done.Count} {(done.Count == 1 ? "file" : "files")} ({Fmt.Bytes(freed)}) to the Recycle Bin." +
                                  (failed > 0 ? $" {failed} could not be moved." : ""), failed > 0);
    }

    async void OldDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not OldFile file) return;
        bool ok = await MainWindow.Instance.Confirm($"Move {file.Name} to the Recycle Bin?",
            $"{file.SizeText}, last changed {file.ChangedText}\n{file.Path}\n\nYou can restore it from the Recycle Bin until the bin is emptied.", "Move to bin", danger: true);
        if (!ok) return;
        try
        {
            await Task.Run(() => FileSystem.DeleteFile(file.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin));
            _old.Remove(file);
            ShowResults();
            MainWindow.Instance.Toast($"Moved {file.Name} ({file.SizeText}) to the Recycle Bin.");
        }
        catch (Exception ex) { MainWindow.Instance.Toast("Could not move the file: " + ex.Message, true); }
    }
}
