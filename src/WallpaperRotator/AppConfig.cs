using System.Text.Json;
using System.Text.Json.Serialization;

namespace WallpaperRotator;

public sealed class AppConfig
{
    public RotationSchedule Schedule { get; set; } = RotationSchedule.Daily;
    public AutomaticRotationMode RotationMode { get; set; } = AutomaticRotationMode.Random;
    public int IntervalHours { get; set; } = 24;
    public int MinimumWidth { get; set; } = 3840;
    public int MinimumHeight { get; set; } = 2160;
    public int HistoryLimit { get; set; } = 10;
    public int MaximumFileMegabytes { get; set; } = 25;
    public int MaximumAttempts { get; set; } = 5;
    public int WallhavenWeight { get; set; } = 70;
    public int NasaWeight { get; set; } = 30;
    public string WallhavenCategories { get; set; } = "110";
    public bool AllowSketchy { get; set; }
    public bool AllowNsfw { get; set; }
    public bool NsfwOnly { get; set; }
    public string WallhavenQuery { get; set; } = "";
    public string[] NasaQueries { get; set; } = ["space", "earth", "nebula", "galaxy", "moon"];

    public void Normalize()
    {
        if (!Enum.IsDefined(Schedule)) Schedule = RotationSchedule.Daily;
        if (!Enum.IsDefined(RotationMode)) RotationMode = AutomaticRotationMode.Random;
        // WinForms Timer.Interval is an Int32 number of milliseconds (about 24.8 days maximum).
        IntervalHours = Math.Clamp(IntervalHours, 1, 24 * 24);
        MinimumWidth = Math.Clamp(MinimumWidth, 800, 16384);
        MinimumHeight = Math.Clamp(MinimumHeight, 600, 8640);
        HistoryLimit = Math.Clamp(HistoryLimit, 1, 100);
        MaximumFileMegabytes = Math.Clamp(MaximumFileMegabytes, 1, 100);
        MaximumAttempts = Math.Clamp(MaximumAttempts, 1, 10);
        WallhavenWeight = Math.Clamp(WallhavenWeight, 0, 1000);
        NasaWeight = Math.Clamp(NasaWeight, 0, 1000);
        if (WallhavenWeight + NasaWeight == 0) WallhavenWeight = 1;
        if (WallhavenCategories is not ("100" or "010" or "001" or "110" or "101" or "011" or "111")) WallhavenCategories = "110";
        NasaQueries = NasaQueries.Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).Take(20).ToArray();
        if (NasaQueries.Length == 0) NasaQueries = ["space"];
        WallhavenQuery = WallhavenQuery.Trim();
    }
}

public sealed class ConfigStore(AppPaths paths, DiagnosticLog log)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly SemaphoreSlim gate = new(1, 1);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public async Task<AppConfig> LoadAsync()
    {
        await gate.WaitAsync();
        try
        {
        paths.EnsureCreated();
        AppConfig config;
        try
        {
            config = File.Exists(paths.Config)
                ? JsonSerializer.Deserialize<AppConfig>(await File.ReadAllTextAsync(paths.Config), JsonOptions) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.Write("Configuration was invalid; safe defaults were loaded.", ex);
            config = new();
        }
        config.Normalize();
        await SaveUnsafeAsync(config);
        return config;
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(AppConfig config)
    {
        await gate.WaitAsync();
        try { await SaveUnsafeAsync(config); }
        finally { gate.Release(); }
    }

    private async Task SaveUnsafeAsync(AppConfig config)
    {
        config.Normalize();
        paths.EnsureCreated();
        var temp = $"{paths.Config}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(config, JsonOptions));
            File.Move(temp, paths.Config, true);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }
}
