using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CoreWatch.Pages;
using CoreWatch.Services;

namespace CoreWatch;

/// <summary>Implemented by every page hosted in the main window.</summary>
public interface IPage
{
    /// <summary>True when the page needs the per-process list in each snapshot.</summary>
    bool WantsProcesses => false;
    void OnShown() { }
    void OnTick(Snapshot s) { }
}

public partial class MainWindow : Window
{
    readonly Dictionary<string, (RadioButton Button, Func<UserControl> Create)> _routes;
    readonly Dictionary<string, UserControl> _pages = new();
    IPage? _current;
    TaskCompletionSource<bool>? _dialog;
    DispatcherTimer? _toastTimer;

    public static MainWindow Instance => (MainWindow)Application.Current.MainWindow;

    public MainWindow()
    {
        InitializeComponent();

        _routes = new()
        {
            ["overview"] = (NavDashboard, () => new DashboardPage()),
            ["performance"] = (NavPerformance, () => new PerformancePage()),
            ["memory"] = (NavMemory, () => new MemoryPage()),
            ["graphics"] = (NavGpu, () => new GpuPage()),
            ["processes"] = (NavProcesses, () => new ProcessesPage()),
            ["storage"] = (NavStorage, () => new StoragePage()),
            ["cleaner"] = (NavCleaner, () => new CleanerPage()),
            ["startup"] = (NavStartup, () => new StartupPage()),
            ["tweaks"] = (NavTweaks, () => new TweaksPage()),
            ["registry"] = (NavRegistry, () => new RegistryPage()),
            ["apps"] = (NavApps, () => new AppsPage()),
            ["install"] = (NavInstall, () => new InstallPage()),
            ["toolbox"] = (NavToolbox, () => new ToolboxPage()),
            ["settings"] = (NavSettings, () => new SettingsPage()),
        };
        foreach (var (key, route) in _routes)
            route.Button.Checked += (_, _) => Show(key);

        if (Sys.IsAdmin)
        {
            AdminDot.Fill = (Brush)FindResource("B.Good");
            AdminText.Text = "Administrator";
        }

        SystemMonitor.Instance.Updated += OnSnapshot;
        StateChanged += (_, _) => ApplyWindowState();
        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
        Loaded += (_, _) =>
        {
            Navigate(App.StartPage is { } p && _routes.ContainsKey(p) ? p : "overview");
            if (App.ShotsFolder != null) _ = CaptureAllPages(App.ShotsFolder);
            if (App.DevTweak is { } dev) _ = ToggleTweakAndExit(dev.Id, dev.Folder);
            if (App.DevRegistry is { } reg) _ = RegistryActionAndExit(reg.Category, reg.Folder);
            if (App.DevRefreshRun is { } refreshFolder) _ = RefreshAndExit(refreshFolder);
        };
    }

    /// <summary>Switches to a page by route key (also used by pages to link to each other).</summary>
    public void Navigate(string key)
    {
        if (!_routes.TryGetValue(key, out var route)) return;
        if (route.Button.IsChecked == true) Show(key);
        else route.Button.IsChecked = true;
    }

    public T? GetPage<T>(string key) where T : UserControl => _pages.TryGetValue(key, out var p) ? p as T : null;

    void Show(string key)
    {
        if (!_pages.TryGetValue(key, out var page)) _pages[key] = page = _routes[key].Create();
        if (ReferenceEquals(Host.Content, page)) return;

        Host.Content = page;
        _current = page as IPage;
        SystemMonitor.Instance.WantProcesses = _current?.WantsProcesses ?? false;
        _current?.OnShown();
        if (SystemMonitor.Instance.Latest is { } s) _current?.OnTick(s);
        SystemMonitor.Instance.Poke();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        HostShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    void OnSnapshot(Snapshot s)
    {
        MiniStats.Text = $"CPU {Fmt.Pct(s.CpuTotal)}   RAM {Fmt.Pct(s.MemPct)}" + (s.PrimaryGpu is { } g ? $"   GPU {Fmt.Pct(g.Utilization)}" : "");
        if (WindowState != WindowState.Minimized) _current?.OnTick(s);
    }

    // ───────── Toast ─────────
    public void Toast(string message, bool isError = false)
    {
        ToastText.Text = message;
        ToastIcon.Text = isError ? "" : "";
        ToastIcon.Foreground = (Brush)FindResource(isError ? "B.Warn" : "B.Good");
        ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(isError ? 6 : 4) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
        };
        _toastTimer.Start();
    }

    // ───────── Confirm dialog ─────────
    public Task<bool> Confirm(string title, string body, string okText = "OK", bool danger = false)
    {
        _dialog?.TrySetResult(false);
        _dialog = new TaskCompletionSource<bool>();
        DialogTitle.Text = title;
        DialogBody.Text = body;
        DialogOk.Content = okText;
        DialogOk.Style = (Style)FindResource(danger ? "BtnDanger" : "BtnPrimary");
        Dialog.Visibility = Visibility.Visible;
        Dialog.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        DialogCancel.Focus();
        return _dialog.Task;
    }

    void CloseDialog(bool result)
    {
        Dialog.Visibility = Visibility.Collapsed;
        _dialog?.TrySetResult(result);
        _dialog = null;
    }

    void DialogOk_Click(object sender, RoutedEventArgs e) => CloseDialog(true);
    void DialogCancel_Click(object sender, RoutedEventArgs e) => CloseDialog(false);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Dialog.Visibility == Visibility.Visible) { CloseDialog(false); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    // ───────── Window chrome ─────────
    void ApplyWindowState()
    {
        bool max = WindowState == WindowState.Maximized;
        Root.Margin = max ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = max ? "" : "";
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ───────── Developer: render every page to PNG and exit ─────────
    async Task CaptureAllPages(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var key in _routes.Keys)
        {
            Navigate(key);
            await Task.Delay(key is "overview" ? 4500 : key is "storage" or "cleaner" ? 9000 : key is "install" ? 20000 : 3200);
            Capture(Root, Path.Combine(folder, key + ".png"));
            // Pages taller than the window also get their whole scrolling content in one image.
            if (Host.Content is UserControl { Content: ScrollViewer { Content: FrameworkElement body } } && body.ActualHeight > Root.ActualHeight)
                Capture(body, Path.Combine(folder, key + "-full.png"));
        }
        Close();
    }

    // ───────── Developer: flip one tweak as a click would, record the result and exit ─────────
    async Task ToggleTweakAndExit(string id, string folder)
    {
        Directory.CreateDirectory(folder);
        Navigate("tweaks");
        await Task.Delay(1500);
        string result = GetPage<TweaksPage>("tweaks") is { } page ? await page.DevToggle(id) : "Tweaks page missing";
        await Task.Delay(900); // let the switch and toast settle
        File.AppendAllText(Path.Combine(folder, "toggle.log"), result + Environment.NewLine);
        Capture(Root, Path.Combine(folder, $"toggle-{DateTime.Now:HHmmss}.png"));
        Close();
    }

    // ───────── Developer: fix one registry category (or restore the newest backup), record the result and exit ─────────
    async Task RegistryActionAndExit(string? category, string folder)
    {
        Directory.CreateDirectory(folder);
        Navigate("registry");
        await Task.Delay(1200);
        string result = GetPage<RegistryPage>("registry") is not { } page ? "Registry page missing"
            : category != null ? await page.DevFix(category) : await page.DevRestoreLatest();
        await Task.Delay(900);
        File.AppendAllText(Path.Combine(folder, "registry.log"), result + Environment.NewLine);
        string stamp = $"{(category != null ? "fix" : "restore")}-{DateTime.Now:HHmmss}";
        Capture(Root, Path.Combine(folder, stamp + ".png"));
        if (Host.Content is UserControl { Content: ScrollViewer { Content: FrameworkElement body } }) Capture(body, Path.Combine(folder, stamp + "-full.png"));
        Close();
    }

    // ───────── Developer: press Refresh PC on Overview, record the result and exit ─────────
    async Task RefreshAndExit(string folder)
    {
        try
        {
            Navigate("overview");
            await Task.Delay(4000); // let the monitor settle so the memory baseline is real
            var report = GetPage<DashboardPage>("overview") is { } page ? await page.RunRefresh(preview: false) : null;
            await Task.Delay(900);
            var lines = new List<string> { report?.Headline ?? "Refresh did not run" };
            if (report != null) lines.AddRange(report.Steps.Select(s => $"{(s.Done ? "done   " : "skipped")} {s.Name}: {s.Result}"));
            Directory.CreateDirectory(folder);
            File.WriteAllLines(Path.Combine(folder, "refresh.log"), lines);
            Capture(Root, Path.Combine(folder, "refresh.png"));
        }
        finally { Close(); }
    }

    void Capture(FrameworkElement element, string file)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var area = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        var bmp = new RenderTargetBitmap((int)(area.Width * dpi.DpiScaleX), (int)(area.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Background, null, area);
            dc.DrawRectangle(new VisualBrush(element), null, area);
        }
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
