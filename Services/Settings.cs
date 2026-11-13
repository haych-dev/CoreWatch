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
