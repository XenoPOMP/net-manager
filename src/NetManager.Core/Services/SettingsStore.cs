using System.Text.Json;

namespace NetManager.Core.Services;

public sealed class AppSettings
{
    /// <summary>"en" or "ru"; null = follow the OS language.</summary>
    public string? Language { get; set; }
    /// <summary>"system", "light" or "dark"; null = follow Windows.</summary>
    public string? Theme { get; set; }
}

public sealed class SettingsStore(string? path = null)
{
    public string FilePath { get; } = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetManager", "settings.json");

    public AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch (JsonException) { return new(); }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
