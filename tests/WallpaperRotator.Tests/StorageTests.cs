using System.Text.Json;

namespace WallpaperRotator.Tests;

public class StorageTests
{
    private static HistoryEntry Entry(AppPaths paths, string id, string? file = null) =>
        new("NASA", id, "t", "https://images.nasa.gov", file ?? Path.Combine(paths.Wallpapers, $"{id}.jpg"), null, DateTimeOffset.Now);

    [Fact]
    public async Task HistoryKeepsNewestEntriesAndDeletesDroppedFiles()
    {
        using var temp = new TempDir();
        var paths = temp.CreatePaths();
        var store = new HistoryStore(paths, new DiagnosticLog(paths));
        for (var i = 0; i < 3; i++)
        {
            var entry = Entry(paths, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await File.WriteAllTextAsync(entry.FilePath, "x");
            await store.AddAsync(entry, 2);
        }
        var entries = await store.LoadAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal("2", entries[0].Id);
        Assert.False(File.Exists(Path.Combine(paths.Wallpapers, "0.jpg")));
    }

    [Fact]
    public async Task HistoryCleanupStaysInsideWallpaperDirectory()
    {
        using var temp = new TempDir();
        var paths = temp.CreatePaths();
        var external = Path.Combine(temp.Path, "keep.txt");
        await File.WriteAllTextAsync(external, "important");
        await File.WriteAllTextAsync(paths.History, JsonSerializer.Serialize(new[] { Entry(paths, "poisoned", external) }));
        var managed = Entry(paths, "new");
        await File.WriteAllTextAsync(managed.FilePath, "x");

        await new HistoryStore(paths, new DiagnosticLog(paths)).AddAsync(managed, 1);

        Assert.True(File.Exists(external));
    }

    [Fact]
    public async Task OrphanCleanupRemovesOnlyOldUnreferencedFiles()
    {
        using var temp = new TempDir();
        var paths = temp.CreatePaths();
        var store = new HistoryStore(paths, new DiagnosticLog(paths));
        var referenced = Entry(paths, "kept");
        await File.WriteAllTextAsync(referenced.FilePath, "x");
        await store.AddAsync(referenced, 10);
        var orphan = Path.Combine(paths.Wallpapers, "orphan.png");
        var leftoverTemp = Path.Combine(paths.Wallpapers, ".abc.tmp");
        var fresh = Path.Combine(paths.Wallpapers, "fresh.jpg");
        var unrelated = Path.Combine(paths.Wallpapers, "notes.txt");
        foreach (var file in new[] { orphan, leftoverTemp, fresh, unrelated }) await File.WriteAllTextAsync(file, "x");
        var old = DateTime.UtcNow.AddHours(-2);
        foreach (var file in new[] { referenced.FilePath, orphan, leftoverTemp, unrelated }) File.SetLastWriteTimeUtc(file, old);

        var removed = await store.CleanupOrphansAsync(TimeSpan.FromHours(1), DateTime.UtcNow);

        Assert.Equal(2, removed);
        Assert.True(File.Exists(referenced.FilePath));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(unrelated));
        Assert.False(File.Exists(orphan));
        Assert.False(File.Exists(leftoverTemp));
    }

    [Fact]
    public async Task OrphanCleanupDoesNothingWhenHistoryIsUnreadable()
    {
        using var temp = new TempDir();
        var paths = temp.CreatePaths();
        await File.WriteAllTextAsync(paths.History, "{ broken");
        var file = Path.Combine(paths.Wallpapers, "maybe-needed.jpg");
        await File.WriteAllTextAsync(file, "x");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-1));

        Assert.Equal(0, await new HistoryStore(paths, new DiagnosticLog(paths)).CleanupOrphansAsync(TimeSpan.FromHours(1), DateTime.UtcNow));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task DailyRotationRunsOncePerDayAndRetriesAfterFailure()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var states = new RotationStateStore(paths, new DiagnosticLog(paths));
        var now = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Local);
        var coordinator = new DailyRotationCoordinator(states, () => now);
        var calls = 0;

        Assert.False(await coordinator.TryRunAsync(true, _ => { calls++; return Task.FromResult(true); }));
        Assert.Equal(0, calls);
        Assert.False(await coordinator.TryRunAsync(false, _ => { calls++; return Task.FromResult(false); }));
        Assert.Equal(1, calls);
        Assert.Null((await states.LoadAsync()).LastSuccessfulDailyRotation);
        Assert.True(await coordinator.TryRunAsync(false, _ => { calls++; return Task.FromResult(true); }));
        Assert.Equal(2, calls);
        Assert.False(await coordinator.TryRunAsync(false, _ => { calls++; return Task.FromResult(true); }));
        Assert.Equal(2, calls);

        now = now.AddDays(1);
        Assert.True(await coordinator.TryRunAsync(false, _ => { calls++; return Task.FromResult(true); }));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task DailyRotationKeepsStateWrittenDuringRotation()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var states = new RotationStateStore(paths, new DiagnosticLog(paths));
        var rotatedAt = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var coordinator = new DailyRotationCoordinator(states, () => new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Local));

        await coordinator.TryRunAsync(false, async _ =>
        {
            await states.UpdateAsync(s => s.LastSuccessfulRotation = rotatedAt);
            return true;
        });

        var state = await states.LoadAsync();
        Assert.Equal(rotatedAt, state.LastSuccessfulRotation);
        Assert.Equal(new DateOnly(2026, 9, 1), state.LastSuccessfulDailyRotation);
    }

    [Fact]
    public async Task ConcurrentStateUpdatesAreNotLost()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var states = new RotationStateStore(paths, new DiagnosticLog(paths));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => states.UpdateAsync(s => s.BlockedIds.Add($"id{i}"))));
        Assert.Equal(20, (await states.LoadAsync()).BlockedIds.Distinct().Count());
    }

    [Fact]
    public void IntervalDelayCountsFromLastSuccessfulRotation()
    {
        var now = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var interval = TimeSpan.FromHours(24);
        Assert.Equal(TimeSpan.Zero, RotationTiming.GetIntervalDelay(null, interval, now));
        Assert.Equal(TimeSpan.FromHours(6), RotationTiming.GetIntervalDelay(now.AddHours(-18), interval, now));
        Assert.Equal(TimeSpan.Zero, RotationTiming.GetIntervalDelay(now.AddDays(-3), interval, now));
        Assert.Equal(interval, RotationTiming.GetIntervalDelay(now.AddDays(5), interval, now));
    }

    [WindowsFact]
    public void SecretRoundTripIsEncrypted()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var store = new SecretStore(paths, new DiagnosticLog(paths));
        store.Save("top-secret");
        Assert.Equal("top-secret", store.Load());
        Assert.DoesNotContain("top-secret", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(paths.Secret)));
        store.Delete();
        Assert.Null(store.Load());
    }

    [WindowsFact]
    public async Task UnreadableSecretIsLogged()
    {
        using var temp = new TempDir();
        var paths = temp.CreatePaths();
        await File.WriteAllBytesAsync(paths.Secret, [1, 2, 3, 4]);
        Assert.Null(new SecretStore(paths, new DiagnosticLog(paths)).Load());
        Assert.Contains("could not be read", await File.ReadAllTextAsync(paths.Log));
    }

    [Theory]
    [InlineData(WallpaperStyle.Fill, "10", "0")]
    [InlineData(WallpaperStyle.Fit, "6", "0")]
    [InlineData(WallpaperStyle.Tile, "0", "1")]
    [InlineData(WallpaperStyle.Span, "22", "0")]
    public void WallpaperStyleMapsToRegistryValues(WallpaperStyle style, string wallpaperStyle, string tile)
    {
        Assert.Equal((wallpaperStyle, tile), WallpaperService.GetRegistryValues(style));
    }

    [Fact]
    public void UnchangedWallpaperStyleKeepsWindowsSetting()
    {
        Assert.Null(WallpaperService.GetRegistryValues(WallpaperStyle.Unchanged));
    }
}
