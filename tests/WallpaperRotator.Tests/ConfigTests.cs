using System.Text.Json;
using System.Text.Json.Serialization;

namespace WallpaperRotator.Tests;

public class ConfigTests
{
    private static readonly JsonSerializerOptions EnumOptions = new() { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task MissingConfigIsCreatedAndValuesAreNormalized()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var store = new ConfigStore(paths, new DiagnosticLog(paths));
        var config = await store.LoadAsync();
        Assert.Equal(24, config.IntervalHours);
        Assert.True(File.Exists(paths.Config));
        Assert.Equal(RotationSchedule.Daily, config.Schedule);
        Assert.Equal(AutomaticRotationMode.Random, config.RotationMode);
        Assert.Equal(WallpaperStyle.Fill, config.WallpaperStyle);

        config.IntervalHours = -1;
        config.HistoryLimit = 900;
        config.WallhavenWeight = 0;
        config.NasaWeight = 0;
        config.NsfwOnly = true;
        config.ManualCategories = "999";
        await store.SaveAsync(config);
        var loaded = await store.LoadAsync();
        Assert.Equal(1, loaded.IntervalHours);
        Assert.Equal(100, loaded.HistoryLimit);
        Assert.True(loaded.WallhavenWeight > 0);
        Assert.True(loaded.NsfwOnly);
        Assert.Equal("111", loaded.ManualCategories);
    }

    [Fact]
    public async Task MalformedConfigLoadsDefaultsAndKeepsBackup()
    {
        using var temp = new TempDir();
        var paths = temp.CreatePaths();
        await File.WriteAllTextAsync(paths.Config, "{ not-json");
        var loaded = await new ConfigStore(paths, new DiagnosticLog(paths)).LoadAsync();
        Assert.Equal(3840, loaded.MinimumWidth);
        Assert.Contains("safe defaults", await File.ReadAllTextAsync(paths.Log));
        Assert.Equal("{ not-json", await File.ReadAllTextAsync(paths.Config + ".invalid"));
    }

    [Fact]
    public async Task ConcurrentSavesLeaveValidFile()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var store = new ConfigStore(paths, new DiagnosticLog(paths));
        await Task.WhenAll(Enumerable.Range(1, 20).Select(i => store.SaveAsync(new AppConfig { IntervalHours = i })));

        var saved = JsonSerializer.Deserialize<AppConfig>(await File.ReadAllTextAsync(paths.Config), EnumOptions);
        Assert.NotNull(saved);
        Assert.InRange(saved.IntervalHours, 1, 20);
        Assert.Empty(Directory.EnumerateFiles(paths.Root, "*.tmp"));
    }

    [Fact]
    public async Task LoadingUnchangedConfigDoesNotRewriteFile()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var store = new ConfigStore(paths, new DiagnosticLog(paths));
        await store.LoadAsync();
        var past = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(paths.Config, past);
        await store.LoadAsync();
        Assert.Equal(past, File.GetLastWriteTimeUtc(paths.Config));
    }

    [Theory]
    [InlineData(2560, 1440)]
    [InlineData(1440, 2560)]
    public async Task FirstRunUsesPrimaryScreenAsLandscapeMinimum(int width, int height)
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var config = await new ConfigStore(paths, new DiagnosticLog(paths), () => (width, height)).LoadAsync();
        Assert.Equal(2560, config.MinimumWidth);
        Assert.Equal(1440, config.MinimumHeight);
    }

    [Fact]
    public async Task ExistingConfigIgnoresScreenSize()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        await new ConfigStore(paths, new DiagnosticLog(paths)).LoadAsync();
        var config = await new ConfigStore(paths, new DiagnosticLog(paths), () => (1920, 1080)).LoadAsync();
        Assert.Equal(3840, config.MinimumWidth);
    }

    [Fact]
    public void ManualSelectionDoesNotChangeAutomaticSettings()
    {
        var config = new AppConfig { WallhavenQuery = "auto", WallhavenCategories = "100", AllowSketchy = true };
        var manual = config.ForManualSelection(new WallpaperSelection("anime", "010", WallpaperContentMode.Nsfw));

        Assert.Equal("anime", manual.WallhavenQuery);
        Assert.Equal("010", manual.WallhavenCategories);
        Assert.True(manual.NsfwOnly);
        Assert.False(manual.AllowSketchy);
        Assert.Equal("auto", config.WallhavenQuery);
        Assert.Equal("100", config.WallhavenCategories);
        Assert.True(config.AllowSketchy);
        Assert.False(config.NsfwOnly);
    }
}
