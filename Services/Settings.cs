using System.IO;
using System.Text.Json;

namespace CoreWatch.Services;

public sealed class Settings
{
    public int RefreshMs { get; set; } = 1000;
    public bool SkipRecentTempFiles { get; set; } = true;
    public bool ConfirmBeforeCleaning { get; set; } = true;
    public List<string> CleanerSelection { get; set; } = [];
    public string LastScanPath { get; set; } = "";

    // Background monitoring
    public bool CloseToTray { get; set; }
    public bool AlertsEnabled { get; set; } = true;
    /// <summary>Alert thresholds; 0 switches that alert off.</summary>
    public int AlertDiskFreePct { get; set; } = 10;
    public int AlertMemPct { get; set; } = 90;
    public int AlertGpuTemp { get; set; } = 85;
    public int AlertCpuPct { get; set; }
    public bool RecordHistory { get; set; } = true;

    // Scheduled maintenance: "off", "daily" or "weekly"
    public string AutoRefresh { get; set; } = "off";
    public DateTime LastAutoRefresh { get; set; }

    // Update check
    public bool CheckForUpdates { get; set; } = true;
    public DateTime LastUpdateCheck { get; set; }
    public string LatestVersion { get; set; } = "";

    public string LastDuplicatePath { get; set; } = "";

    public static Settings Current { get; private set; } = Load();

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoreWatch");
    static string FilePath => Path.Combine(Folder, "settings.json");

    static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
