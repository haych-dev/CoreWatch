using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using CoreWatch.Controls;
using CoreWatch.Services;

namespace CoreWatch.Pages;

public partial class SettingsPage : UserControl, IPage
{
    readonly bool _ready;

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

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Ui.AddKeyValue(About, "Version", $"CoreWatch {version?.ToString(3)}");
        Ui.AddKeyValue(About, "Runtime", RuntimeInformation.FrameworkDescription);
        Ui.AddKeyValue(About, "Windows", $"{SystemMonitor.Instance.Info.OsName} {SystemMonitor.Instance.Info.OsVersion}");
        Ui.AddKeyValue(About, "Settings file", Path.Combine(Settings.Folder, "settings.json"));
        _ready = true;
    }

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
