using System.Text.Json;
using System.Text.Json.Serialization;

namespace TurboDL.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON. A missing or unreadable file gives defaults.</summary>
public sealed class SettingsStore(string? path = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurboDL", "settings.json");

    public string FilePath { get; } = path ?? DefaultPath;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return Normalize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings());
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A broken settings file shouldn't stop the app; keep a copy for diagnosis and start fresh.
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch (IOException) { }
        }
        return new AppSettings();
    }

    /// <summary>Writes to a temp file first so a crash mid-save never leaves half a file.</summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Normalize(settings), Json));
        File.Move(temp, FilePath, overwrite: true);
    }

    public static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, Json), Json)!;

    private static AppSettings Normalize(AppSettings s)
    {
        s.DefaultConnections = Math.Clamp(s.DefaultConnections, 1, 32);
        s.MaxConcurrent = Math.Clamp(s.MaxConcurrent, 1, 20);
        s.MaxSolving = Math.Clamp(s.MaxSolving, 1, AppSettings.MaxSolvingLimit);
        s.SpeedLimitKBps = Math.Max(0, s.SpeedLimitKBps);
        s.MinCatchSizeKB = Math.Max(0, s.MinCatchSizeKB);
        s.Categories ??= [];
        s.Schedule ??= new ScheduleSettings();
        s.Schedule.Days ??= [];
        return s;
    }
}
