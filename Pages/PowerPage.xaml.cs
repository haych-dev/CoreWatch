using System.Windows;
using System.Windows.Controls;
using CoreWatch.Controls;
using CoreWatch.Services;

namespace CoreWatch.Pages;

public partial class PowerPage : UserControl, IPage
{
    DateTime _batteryAt;
    bool _busy;
    string _batterySignature = "";

    public PowerPage() => InitializeComponent();

    public void OnShown()
    {
        _batteryAt = default;
        _ = LoadPlans();
    }

    public void OnTick(Snapshot s)
    {
        if ((DateTime.Now - _batteryAt).TotalSeconds < 15) return;
        _batteryAt = DateTime.Now;
        _ = LoadBattery();
    }

    async Task LoadBattery()
    {
        var b = await Task.Run(PowerService.Battery);
        string signature = $"{b.Present}{b.PluggedIn}{b.Charging}{b.Percent}{b.Remaining?.TotalMinutes:0}{b.FullCapacity}";
        if (signature == _batterySignature) return;
        _batterySignature = signature;

        BatteryDial.Visibility = ReportButton.Visibility = BatteryUsageButton.Visibility = b.Present ? Visibility.Visible : Visibility.Collapsed;
        NoBatteryIcon.Visibility = b.Present ? Visibility.Collapsed : Visibility.Visible;
        BatteryFacts.Children.Clear();

        if (!b.Present)
        {
            BatteryTitle.Text = "This PC has no battery";
            BatteryDetail.Text = "It runs on mains power, so there is no charge or battery health to show. Power plans still apply.";
            SubTitle.Text = "Running on mains power.";
            return;
        }

        Gauge.Color = Ui.Color(b.Percent <= 15 && !b.PluggedIn ? "C.Bad" : b.Percent <= 30 && !b.PluggedIn ? "C.Warn" : "C.Good");
        Gauge.AnimateTo(b.Percent);
        GaugeText.Text = b.Percent + "%";
        BatteryTitle.Text = b.Charging ? "Charging" : b.PluggedIn ? "Plugged in, not charging" : "On battery";
        BatteryDetail.Text = b.PluggedIn ? b.Percent >= 99 ? "The battery is full." : "Windows decides when to top the battery up."
            : b.Remaining is { } left ? $"About {Fmt.Duration(left)} left at the current rate." : "Windows is still working out how long is left.";
        SubTitle.Text = $"Battery at {b.Percent}%, {BatteryTitle.Text.ToLowerInvariant()}.";

        if (b.Health is { } health)
        {
            var value = Ui.AddKeyValue(BatteryFacts, "Battery health", $"{health:0}% of its original capacity");
            value.Foreground = Ui.Brush(health >= 80 ? "B.Good" : health >= 60 ? "B.Warn" : "B.Bad");
            Ui.AddKeyValue(BatteryFacts, "Holds now", $"{b.FullCapacity / 1000.0:0.0} Wh");
            Ui.AddKeyValue(BatteryFacts, "Held when new", $"{b.DesignCapacity / 1000.0:0.0} Wh");
        }
        else Ui.AddKeyValue(BatteryFacts, "Battery health", "Not reported by this battery");
        if (b.Cycles is { } cycles) Ui.AddKeyValue(BatteryFacts, "Charge cycles", cycles.ToString());
        if (b.Name.Length > 0) Ui.AddKeyValue(BatteryFacts, "Battery", b.Name);
    }

    async Task LoadPlans()
    {
        try
        {
            var plans = await Task.Run(PowerService.Plans);
            PlanList.ItemsSource = plans;
            PlanNote.Visibility = plans.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PlanNote.Text = "Windows did not list any power plans.";
        }
        catch (Exception ex)
        {
            PlanList.ItemsSource = null;
            PlanNote.Visibility = Visibility.Visible;
            PlanNote.Text = "Power plans could not be read: " + ex.Message;
        }
    }

    async void Plan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as Button)?.Tag is not PowerPlan plan) return;
        _busy = true;
        try
        {
            string? error = await Task.Run(() => PowerService.SetActive(plan));
            await LoadPlans();
            MainWindow.Instance.Toast(error == null ? $"Now using the {plan.Name} plan." : $"The plan was not changed: {error}", error != null);
        }
        finally { _busy = false; }
    }

    async void Report_Click(object sender, RoutedEventArgs e)
    {
        ReportButton.IsEnabled = false;
        try
        {
            var (file, error) = await Task.Run(PowerService.BatteryReport);
            if (file != null) Sys.ShellOpen(file);
            else MainWindow.Instance.Toast("Windows could not write the report: " + error, true);
        }
        finally { ReportButton.IsEnabled = true; }
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string target) Sys.ShellOpen(target);
    }
}
