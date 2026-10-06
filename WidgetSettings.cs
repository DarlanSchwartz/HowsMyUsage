using System.Text.Json;

namespace Usage;

sealed class WidgetSettings
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool AlwaysOnTop { get; set; }
    public string Size { get; set; } = "Large";
    public bool PositionLocked { get; set; }
    static string FilePath => Path.Combine(AppContext.BaseDirectory, "widget-settings.json");

    public static bool IsSystemDrive(string path) => string.Equals(Path.GetPathRoot(Path.GetFullPath(path)), "C:\\", StringComparison.OrdinalIgnoreCase);
    public static WidgetSettings Load()
    {
        try { return JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save()
    {
        if (IsSystemDrive(FilePath)) return;
        try
        {
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this));
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
