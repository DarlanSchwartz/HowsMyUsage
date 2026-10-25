using System.Text.Json;

namespace Usage;

sealed class WidgetSettings
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool AlwaysOnTop { get; set; }
    public string Size { get; set; } = "Large";
    public bool PositionLocked { get; set; }
    public bool ShowCodex { get; set; } = true;
    public bool ShowGemini { get; set; } = true;
    public bool ShowClaude { get; set; } = true;
    public bool ProviderEnabled(int index) => index switch
    {
        0 => ShowCodex, 1 => ShowGemini, 2 => ShowClaude,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
    public void SetProviderEnabled(int index, bool enabled)
    {
        switch (index)
        {
            case 0: ShowCodex = enabled; break;
            case 1: ShowGemini = enabled; break;
            case 2: ShowClaude = enabled; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
    static string FilePath => Path.Combine(AppContext.BaseDirectory, "widget-settings.json");

    public static WidgetSettings Load()
    {
        try { return JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this));
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
