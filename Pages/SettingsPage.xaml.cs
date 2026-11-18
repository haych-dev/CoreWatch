using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using CoreWatch.Controls;
using CoreWatch.Services;

namespace CoreWatch.Pages;

public partial class SettingsPage : UserControl, IPage
{
    readonly bool _ready;
    readonly StackPanel _alertList = new(), _runList = new();
    readonly TextBlock _nextRun = new() { Style = Ui.Style("T.Label"), Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _updateStatus = new() { Style = Ui.Style("T.Label"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
    int _group;

    public SettingsPage()
    {
        InitializeComponent();
        var s = Settings.Current;

        foreach (RadioButton rb in RatePanel.Children)
            rb.IsChecked = (string)rb.Tag == s.RefreshMs.ToString();
        if (!RatePanel.Children.OfType<RadioButton>().Any(r => r.IsChecked == true))
            ((RadioButton)RatePanel.Children[1]).IsChecked = true;

        SkipRecent.IsChecked = s.SkipRecentTempFiles;
        ConfirmClean.IsChecked = s.ConfirmBeforeCleaning;

        if (Sys.IsAdmin)
        {
            AdminText.Text = "CoreWatch is running as administrator. System-wide cleaning, standby cache clearing and all-user startup entries are unlocked.";
            AdminButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            AdminText.Text = "Running as a standard user. Restart elevated to clean Windows Update and system temp files, clear the standby cache and change all-user startup entries.";
        }

        BuildBackground(s);
        BuildAlerts(s);
        BuildMaintenance(s);
        BuildUpdates(s);

        Ui.AddKeyValue(About, "Version", $"CoreWatch {UpdateService.Current.ToString(3)}");
        Ui.AddKeyValue(About, "Runtime", RuntimeInformation.FrameworkDescription);
        Ui.AddKeyValue(About, "Windows", $"{SystemMonitor.Instance.Info.OsName} {SystemMonitor.Instance.Info.OsVersion}");
        Ui.AddKeyValue(About, "Settings file", Path.Combine(Settings.Folder, "settings.json"));

        AlertService.Recent.CollectionChanged += (_, _) => ShowAlerts();
        MaintenanceService.Finished += _ => ShowRuns();
        UpdateService.Changed += ShowUpdateStatus;
        _ready = true;
    }

    public void OnShown()
    {
        ShowAlerts();
        ShowRuns();
        ShowUpdateStatus();
    }

    // ───────── Building blocks ─────────
    StackPanel AddCard(string title)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, Style = Ui.Style("T.Card"), Margin = new Thickness(0, 0, 0, 10) });
        Extra.Children.Add(new Border { Style = Ui.Style("Card"), Margin = new Thickness(0, 12, 0, 0), Child = body });
        return body;
    }

    static void AddLine(Panel panel) => panel.Children.Add(new Border { Height = 1, Background = Ui.Brush("B.Line"), Margin = new Thickness(0, 10, 0, 10) });

    static StackPanel Caption(string title, string sub)
    {
        var text = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 560, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title });
        if (sub.Length > 0) text.Children.Add(new TextBlock { Text = sub, Style = Ui.Style("T.Label"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
        return text;
    }

    static CheckBox AddSwitch(Panel panel, string title, string sub, bool value, Action<bool> changed)
    {
        var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        row.Children.Add(Caption(title, sub));
        var box = new CheckBox { Style = Ui.Style("Switch"), IsChecked = value, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        box.Click += (_, _) => changed(box.IsChecked == true);
        row.Children.Add(box);
        panel.Children.Add(row);
        return box;
    }

    /// <summary>A row with a segmented choice on the right.</summary>
    void AddChoice<T>(Panel panel, string title, string sub, (string Label, T Value)[] options, T current, Action<T> changed) where T : notnull
    {
        var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        row.Children.Add(Caption(title, sub));
        var segments = new StackPanel { Orientation = Orientation.Horizontal };
        string group = "set" + _group++;
        foreach (var (label, value) in options)
        {
            var rb = new RadioButton { Style = Ui.Style("Seg"), GroupName = group, Content = label, IsChecked = EqualityComparer<T>.Default.Equals(value, current) };
            rb.Checked += (_, _) => { if (_ready) { changed(value); Settings.Current.Save(); } };
            segments.Children.Add(rb);
        }
        row.Children.Add(new Border
        {
            Background = Ui.Brush("B.CardAlt"), CornerRadius = new CornerRadius(8), Padding = new Thickness(3), Child = segments,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(row);
    }

    static void AddListRow(Panel panel, string left, string title, string detail)
    {
        var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock { Text = left, Style = Ui.Style("T.Label"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
        if (detail.Length > 0) text.Children.Add(new TextBlock { Text = detail, Style = Ui.Style("T.Label"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 1, 0, 0) });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        panel.Children.Add(row);
    }

    // ───────── Background ─────────
    void BuildBackground(Settings s)
    {
        var card = AddCard("Running in the background");
        bool autoStart = false;
        try { autoStart = AutoStart.Enabled; } catch { }
        AddSwitch(card, "Start with Windows", "Opens CoreWatch quietly in the notification area when you sign in, so alerts and scheduled maintenance work without opening it.", autoStart, on =>
        {
            try { AutoStart.Enabled = on; }
            catch (Exception ex) { MainWindow.Instance.Toast("Windows did not accept the change: " + ex.Message, true); }
        });
        AddLine(card);
        AddSwitch(card, "Keep running when the window is closed", "Closing the window hides CoreWatch next to the clock instead of exiting. Right-click the icon there to exit.", s.CloseToTray, on =>
        {
            s.CloseToTray = on;
            s.Save();
        });
        AddLine(card);
        AddSwitch(card, "Record history", $"Saves one averaged reading a minute for the History page. Kept for {MetricLog.KeepDays} days on this PC only.", s.RecordHistory, on =>
        {
            s.RecordHistory = on;
            s.Save();
        });
    }

    // ───────── Alerts ─────────
    void BuildAlerts(Settings s)
    {
        var card = AddCard("Alerts");
        AddSwitch(card, "Notify me when something needs attention", "Shows a Windows notification while CoreWatch is running, including when it is hidden next to the clock.", s.AlertsEnabled, on =>
        {
            s.AlertsEnabled = on;
            s.Save();
        });
        AddLine(card);
        AddChoice(card, "A drive is running out of space", "When free space on a fixed drive falls below this.",
            [("Off", 0), ("5%", 5), ("10%", 10), ("15%", 15), ("20%", 20)], s.AlertDiskFreePct, v => s.AlertDiskFreePct = v);
        AddChoice(card, "Memory stays nearly full", "When memory in use stays above this for a minute.",
            [("Off", 0), ("85%", 85), ("90%", 90), ("95%", 95)], s.AlertMemPct, v => s.AlertMemPct = v);
        AddChoice(card, "The graphics card runs hot", "When a GPU that reports its temperature stays above this for 30 seconds.",
            [("Off", 0), ("80 °C", 80), ("85 °C", 85), ("90 °C", 90)], s.AlertGpuTemp, v => s.AlertGpuTemp = v);
        AddChoice(card, "The processor stays busy", "When CPU load stays above this for 5 minutes.",
            [("Off", 0), ("90%", 90), ("95%", 95)], s.AlertCpuPct, v => s.AlertCpuPct = v);
        AddLine(card);
        card.Children.Add(new TextBlock { Text = "Recent alerts", Style = Ui.Style("T.Label"), Margin = new Thickness(0, 0, 0, 4) });
        card.Children.Add(_alertList);
    }

    void ShowAlerts()
    {
        _alertList.Children.Clear();
        if (AlertService.Recent.Count == 0)
            _alertList.Children.Add(new TextBlock { Text = "No alerts yet.", Foreground = Ui.Brush("B.Faint"), Margin = new Thickness(0, 4, 0, 0) });
        foreach (var alert in AlertService.Recent.Take(6)) AddListRow(_alertList, alert.When, alert.Title, alert.Detail);
    }

    // ───────── Scheduled maintenance ─────────
    void BuildMaintenance(Settings s)
    {
        var card = AddCard("Scheduled maintenance");
        AddChoice(card, "Run Refresh PC automatically", "The same safe refresh as the button on Overview. It waits until the PC has been idle for a few minutes, and only runs while CoreWatch is open or in the notification area.",
            [("Off", "off"), ("Daily", "daily"), ("Weekly", "weekly")], s.AutoRefresh is "daily" or "weekly" ? s.AutoRefresh : "off", v =>
            {
                s.AutoRefresh = v;
                // Start the interval from now, so switching it on does not trigger a run straight away.
                if (v != "off") s.LastAutoRefresh = DateTime.Now;
                ShowRuns();
            });
        card.Children.Add(_nextRun);
        AddLine(card);
        card.Children.Add(new TextBlock { Text = "Recent runs", Style = Ui.Style("T.Label"), Margin = new Thickness(0, 0, 0, 4) });
        card.Children.Add(_runList);
    }

    void ShowRuns()
    {
        _nextRun.Text = MaintenanceService.NextDue() is { } due
            ? due <= DateTime.Now.AddMinutes(1) ? "Next run: as soon as the PC is idle." : $"Next run: after {due:d MMM, HH:mm}, once the PC is idle."
            : "Scheduled refresh is off.";
        _runList.Children.Clear();
        var runs = MaintenanceService.History();
        if (runs.Count == 0)
            _runList.Children.Add(new TextBlock { Text = "No refresh has been recorded yet.", Foreground = Ui.Brush("B.Faint"), Margin = new Thickness(0, 4, 0, 0) });
        foreach (var run in runs.Take(5)) AddListRow(_runList, run.When, run.Headline, run.Kind);
    }

    // ───────── Updates ─────────
    void BuildUpdates(Settings s)
    {
        var card = AddCard("Updates");
        AddSwitch(card, "Check for new versions", "Asks GitHub once a day whether a newer CoreWatch has been released. Nothing is downloaded or installed automatically.", s.CheckForUpdates, on =>
        {
            s.CheckForUpdates = on;
            s.Save();
        });
        AddLine(card);
        var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        var text = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 560, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = $"You have CoreWatch {UpdateService.Current.ToString(3)}" });
        text.Children.Add(_updateStatus);
        row.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var download = new Button { Style = Ui.Style("BtnPrimary"), Content = "Open download page", Margin = new Thickness(0, 0, 10, 0), Tag = "download" };
        download.Click += (_, _) => Sys.ShellOpen(UpdateService.ReleasesUrl);
        var check = new Button { Style = Ui.Style("Btn"), Content = "Check now" };
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            _updateStatus.Text = "Asking GitHub…";
            string? error = await UpdateService.Check();
            check.IsEnabled = true;
            ShowUpdateStatus();
            if (error != null) { _updateStatus.Text = "The check did not work: " + error; MainWindow.Instance.Toast("Could not check for updates.", true); }
        };
        buttons.Children.Add(download);
        buttons.Children.Add(check);
        row.Children.Add(buttons);
        card.Children.Add(row);
        _download = download;
    }

    Button? _download;

    void ShowUpdateStatus()
    {
        var s = Settings.Current;
        bool newer = UpdateService.UpdateAvailable;
        if (_download != null) _download.Visibility = newer ? Visibility.Visible : Visibility.Collapsed;
        _updateStatus.Text = s.LastUpdateCheck == default ? "Not checked yet."
            : newer ? $"Version {UpdateService.Parse(s.LatestVersion)?.ToString(3)} is available. Last checked {s.LastUpdateCheck:d MMM, HH:mm}."
            : $"That is the newest version. Last checked {s.LastUpdateCheck:d MMM, HH:mm}.";
    }

    // ───────── Existing options ─────────
    void Rate_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out int ms)) return;
        Settings.Current.RefreshMs = ms;
        Settings.Current.Save();
        SystemMonitor.Instance.Poke();
    }

    void Option_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.SkipRecentTempFiles = SkipRecent.IsChecked == true;
        Settings.Current.ConfirmBeforeCleaning = ConfirmClean.IsChecked == true;
        Settings.Current.Save();
    }

    void Admin_Click(object sender, RoutedEventArgs e)
    {
        if (!Sys.RestartAsAdmin()) MainWindow.Instance.Toast("Elevation was cancelled.", true);
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Settings.Folder);
        Sys.OpenInExplorer(Settings.Folder);
    }
}
