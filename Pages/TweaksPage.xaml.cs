using System.Windows;
using System.Windows.Controls;
using CoreWatch.Controls;
using CoreWatch.Services;

namespace CoreWatch.Pages;

public partial class TweaksPage : UserControl, IPage
{
    readonly List<Tweak> _tweaks = TweakService.Build();
    bool _loaded, _busy;
    Task? _refresh;

    public TweaksPage()
    {
        InitializeComponent();

        foreach (var group in _tweaks.GroupBy(t => t.Group))
        {
            Groups.Children.Add(new TextBlock { Text = group.Key, Style = Ui.Style("T.Card"), Margin = new Thickness(2, 22, 2, 8) });
            Groups.Children.Add(new Border
            {
                Style = Ui.Style("Card"),
                Padding = new Thickness(18, 4, 18, 4),
                Child = new ItemsControl { ItemsSource = group.ToList(), ItemTemplate = (DataTemplate)Resources["TweakTemplate"], Focusable = false },
            });
        }
    }

    public void OnShown()
    {
        if (_loaded) return;
        _loaded = true;
        _refresh = RefreshAll();
    }

    async Task RefreshAll()
    {
        var states = await Task.Run(() => _tweaks.Select(t => TweakService.Read(t)).ToList());
        for (int i = 0; i < _tweaks.Count; i++) _tweaks[i].Update(states[i]);
        UpdateSummary();
    }

    List<Tweak> RecommendedTargets() => _tweaks.Where(t => t.Recommended && t.Available && t.IsOn && !t.Locked).ToList();
    List<Tweak> UndoTargets() => _tweaks.Where(t => t.Touched && !t.Locked).ToList();

    void UpdateSummary()
    {
        var shown = _tweaks.Where(t => t.Available).ToList();
        int off = shown.Count(t => !t.IsOn), locked = shown.Count(t => t.Locked);
        int recommendedLeft = shown.Count(t => t.Recommended && t.IsOn);
        int recommendedLocked = shown.Count(t => t.Recommended && t.IsOn && t.Locked);

        OffCount.Text = off.ToString();
        OffLabel.Text = $"of {shown.Count} features switched off";
        OffDetail.Text = recommendedLeft == 0
            ? "Everything on the recommended list is already off."
            : $"{recommendedLeft} recommended {(recommendedLeft == 1 ? "feature is" : "features are")} still on" +
              (recommendedLocked > 0 ? $", {recommendedLocked} of them {(recommendedLocked == 1 ? "needs" : "need")} administrator rights." : ".");

        RecommendedButton.IsEnabled = !_busy && RecommendedTargets().Count > 0;
        RecommendedButton.Style = Ui.Style(RecommendedTargets().Count > 0 ? "BtnPrimary" : "Btn");
        UndoButton.IsEnabled = !_busy && UndoTargets().Count > 0;

        AdminBar.Visibility = locked > 0 ? Visibility.Visible : Visibility.Collapsed;
        AdminText.Text = $"{locked} of these change settings for the whole PC and need administrator rights.";
    }

    /// <summary>Changes one feature, then re-reads Windows so the switch shows what actually happened.</summary>
    async Task<string?> Apply(Tweak tweak, bool on)
    {
        tweak.SetBusy(true);
        string? error = null;
        try
        {
            error = await Task.Run(() => TweakService.Apply(tweak, on));
            var state = await Task.Run(() => TweakService.Read(tweak));
            tweak.Update(state);
            if (error == null && state.IsOn != on) error = "Windows did not accept the change.";
        }
        catch (Exception ex) { error = ex.Message; }
        finally { tweak.SetBusy(false); }
        return error;
    }

    async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: Tweak tweak }) await Toggle(tweak);
    }

    async Task<string?> Toggle(Tweak tweak)
    {
        if (_busy) { tweak.SetBusy(false); return "busy"; } // snaps the switch back while a batch is running
        bool want = !tweak.IsOn;
        string? error = await Apply(tweak, want);
        UpdateSummary();
        if (error != null) MainWindow.Instance.Toast($"{tweak.Name}: {error}", true);
        else MainWindow.Instance.Toast($"{tweak.Name} switched {(want ? "back on" : "off")}." + (tweak.Note != null ? $" {tweak.Note}." : ""));
        return error;
    }

    /// <summary>Developer: flips one feature exactly as a click on its switch would and reports what happened.</summary>
    public async Task<string> DevToggle(string id)
    {
        if (_refresh != null) await _refresh;
        var tweak = _tweaks.FirstOrDefault(t => t.Id == id);
        if (tweak == null) return $"{id}: unknown tweak";
        if (!tweak.Available || tweak.Locked) return $"{id}: {(tweak.Locked ? "needs administrator" : "not present on this PC")}";
        bool before = tweak.IsOn;
        string? error = await Toggle(tweak);
        return $"{id}: {(before ? "on" : "off")} -> {(tweak.IsOn ? "on" : "off")}, switched-off-here={tweak.Touched}, error={error ?? "none"}";
    }

    async Task ApplyMany(List<Tweak> targets, bool on)
    {
        _busy = true;
        Busy.Visibility = Visibility.Visible;
        Busy.IsIndeterminate = true;
        UpdateSummary();

        int done = 0;
        var failed = new List<string>();
        try
        {
            foreach (var tweak in targets)
            {
                if (await Apply(tweak, on) == null) done++;
                else failed.Add(tweak.Name);
            }
        }
        finally
        {
            _busy = false;
            Busy.IsIndeterminate = false;
            Busy.Visibility = Visibility.Collapsed;
            UpdateSummary();
        }

        bool signOut = targets.Any(t => t.Note != null);
        if (failed.Count > 0) MainWindow.Instance.Toast($"{done} switched {(on ? "back on" : "off")}. Could not change: {string.Join(", ", failed)}.", true);
        else MainWindow.Instance.Toast($"{done} {(done == 1 ? "feature" : "features")} switched {(on ? "back on" : "off")}." + (signOut ? " Sign out to finish." : ""));
    }

    static string Names(List<Tweak> tweaks) => string.Join("\n", tweaks.Select(t => "•  " + t.Name));

    async void Recommended_Click(object sender, RoutedEventArgs e)
    {
        var targets = RecommendedTargets();
        if (_busy || targets.Count == 0) return;
        bool ok = await MainWindow.Instance.Confirm($"Switch off {targets.Count} recommended {(targets.Count == 1 ? "feature" : "features")}?",
            Names(targets) + "\n\nMost people never notice these are gone. You can switch any of them back on here.", "Switch off");
        if (ok) await ApplyMany(targets, false);
    }

    async void Undo_Click(object sender, RoutedEventArgs e)
    {
        var targets = UndoTargets();
        if (_busy || targets.Count == 0) return;
        bool ok = await MainWindow.Instance.Confirm($"Switch {targets.Count} {(targets.Count == 1 ? "feature" : "features")} back on?",
            Names(targets) + "\n\nEach one goes back to the setting it had before CoreWatch changed it.", "Undo changes");
        if (ok) await ApplyMany(targets, true);
    }

    void Admin_Click(object sender, RoutedEventArgs e)
    {
        if (!Sys.RestartAsAdmin("tweaks")) MainWindow.Instance.Toast("CoreWatch was not restarted.", true);
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string exe) Sys.ShellOpen(exe);
    }
}
