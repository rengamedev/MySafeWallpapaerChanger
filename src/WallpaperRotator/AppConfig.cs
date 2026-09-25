using System.Text.Json;
using System.Text.Json.Serialization;

namespace WallpaperRotator;

public sealed class AppConfig
{
    private static readonly string[] ValidCategories = ["100", "010", "001", "110", "101", "011", "111"];

    public RotationSchedule Schedule { get; set; } = RotationSchedule.Daily;
    public AutomaticRotationMode RotationMode { get; set; } = AutomaticRotationMode.Random;
    public WallpaperStyle WallpaperStyle { get; set; } = WallpaperStyle.Fill;
    public bool ShowSuccessNotifications { get; set; } = true;
    public int IntervalHours { get; set; } = 24;
    public int MinimumWidth { get; set; } = 3840;
    public int MinimumHeight { get; set; } = 2160;

    // One image across all monitors: Windows "Span" style and only images shaped like the whole desktop.
    public bool Panorama { get; set; }
    public int PanoramaWidth { get; set; } = 7680;
    public int PanoramaHeight { get; set; } = 2160;
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

    // The last choice in the manual selection dialog. It never changes automatic rotation settings.
    public string ManualQuery { get; set; } = "";
    public string ManualCategories { get; set; } = "111";
    public WallpaperContentMode ManualContentMode { get; set; } = WallpaperContentMode.Sfw;

    public void Normalize()
    {
        if (!Enum.IsDefined(Schedule)) Schedule = RotationSchedule.Daily;
        if (!Enum.IsDefined(RotationMode)) RotationMode = AutomaticRotationMode.Random;
        if (!Enum.IsDefined(WallpaperStyle)) WallpaperStyle = WallpaperStyle.Fill;
        if (!Enum.IsDefined(ManualContentMode)) ManualContentMode = WallpaperContentMode.Sfw;
        // WinForms Timer.Interval is an Int32 number of milliseconds (about 24.8 days maximum).
        IntervalHours = Math.Clamp(IntervalHours, 1, 24 * 24);
        MinimumWidth = Math.Clamp(MinimumWidth, 800, 16384);
        MinimumHeight = Math.Clamp(MinimumHeight, 600, 8640);
        PanoramaWidth = Math.Clamp(PanoramaWidth, 1600, 32768);
        PanoramaHeight = Math.Clamp(PanoramaHeight, 600, 8640);
        if (PanoramaHeight > PanoramaWidth) (PanoramaWidth, PanoramaHeight) = (PanoramaHeight, PanoramaWidth);
        HistoryLimit = Math.Clamp(HistoryLimit, 1, 100);
        MaximumFileMegabytes = Math.Clamp(MaximumFileMegabytes, 1, 100);
        MaximumAttempts = Math.Clamp(MaximumAttempts, 1, 10);
        WallhavenWeight = Math.Clamp(WallhavenWeight, 0, 1000);
        NasaWeight = Math.Clamp(NasaWeight, 0, 1000);
        if (WallhavenWeight + NasaWeight == 0) WallhavenWeight = 1;
        if (!ValidCategories.Contains(WallhavenCategories)) WallhavenCategories = "110";
        if (!ValidCategories.Contains(ManualCategories)) ManualCategories = "111";
        NasaQueries = (NasaQueries ?? []).Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).Take(20).ToArray();
        if (NasaQueries.Length == 0) NasaQueries = ["space"];
        WallhavenQuery = (WallhavenQuery ?? "").Trim();
        ManualQuery = (ManualQuery ?? "").Trim();
    }

    [JsonIgnore] public int RequiredWidth => Panorama ? PanoramaWidth : MinimumWidth;
    [JsonIgnore] public int RequiredHeight => Panorama ? PanoramaHeight : MinimumHeight;

    /// <summary>
    /// Span only for an image shaped like the desktop, so an ordinary image from history is not cropped to a strip.
    /// An image of unknown size gets the style the current mode would install.
    /// </summary>
    public WallpaperStyle StyleFor((int Width, int Height)? size)
    {
        if (!Panorama) return WallpaperStyle;
        return size is not { } s || ImageRequirements.IsNearAspect(s.Width, s.Height, (double)PanoramaWidth / PanoramaHeight)
            ? WallpaperStyle.Span
            : WallpaperStyle;
    }

    public AppConfig Clone()
    {
        var copy = (AppConfig)MemberwiseClone();
        copy.NasaQueries = [.. NasaQueries];
        return copy;
    }

    public void RememberManualSelection(WallpaperSelection selection)
    {
        ManualQuery = selection.Query;
        ManualCategories = selection.Categories;
        ManualContentMode = selection.ContentMode;
    }

    /// <summary>Returns a copy of the configuration that searches with the manual selection instead of automatic settings.</summary>
    public AppConfig ForManualSelection(WallpaperSelection selection)
    {
        var copy = Clone();
        copy.WallhavenQuery = selection.Query;
        copy.WallhavenCategories = selection.Categories;
        copy.AllowSketchy = false;
        copy.AllowNsfw = selection.ContentMode == WallpaperContentMode.Nsfw;
        copy.NsfwOnly = selection.ContentMode == WallpaperContentMode.Nsfw;
        copy.Normalize();
        return copy;
    }
}

public sealed class ConfigStore(AppPaths paths, DiagnosticLog log, Func<(int Width, int Height)?>? screenSize = null)
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
            string? raw = null;
            AppConfig config;
            try
            {
                raw = File.Exists(paths.Config) ? await File.ReadAllTextAsync(paths.Config) : null;
                config = raw is null ? CreateDefaults() : JsonSerializer.Deserialize<AppConfig>(raw, JsonOptions) ?? CreateDefaults();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                log.Write("Configuration was invalid; safe defaults were loaded.", ex);
                if (ex is JsonException) BackUpInvalidConfig();
                raw = null;
                config = CreateDefaults();
            }
            config.Normalize();
            var normalized = JsonSerializer.Serialize(config, JsonOptions);
            // Only write when the file is missing or normalization changed it, so reads do not touch the disk.
            if (raw != normalized) await AtomicFile.WriteAllTextAsync(paths.Config, normalized);
            return config;
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(AppConfig config)
    {
        await gate.WaitAsync();
        try
        {
            config.Normalize();
            paths.EnsureCreated();
            await AtomicFile.WriteAllTextAsync(paths.Config, JsonSerializer.Serialize(config, JsonOptions));
        }
        finally { gate.Release(); }
    }

    private AppConfig CreateDefaults()
    {
        var config = new AppConfig();
        if (screenSize?.Invoke() is { } size && size.Width > 0 && size.Height > 0)
        {
            // Wallpapers are always landscape, so a rotated primary screen still gets landscape minimums.
            config.MinimumWidth = Math.Max(size.Width, size.Height);
            config.MinimumHeight = Math.Min(size.Width, size.Height);
        }
        return config;
    }

    private void BackUpInvalidConfig()
    {
        try { File.Copy(paths.Config, paths.Config + ".invalid", true); }
        catch (Exception ex) { log.Write("Could not back up the invalid configuration.", ex); }
    }
}
