using System.Net;

namespace WallpaperRotator.Tests;

public sealed class RotationServiceTests : IDisposable
{
    private readonly TempDir temp = new();
    private readonly AppPaths paths;
    private readonly DiagnosticLog log;
    private readonly HistoryStore history;
    private readonly RotationStateStore states;
    private readonly ConfigStore configs;
    private readonly HttpClient http = new(new StubHandler(_ => throw new InvalidOperationException("No download expected.")));
    private readonly List<string> applied = [];
    private readonly List<TimeSpan> delays = [];

    public RotationServiceTests()
    {
        paths = temp.CreatePaths();
        log = new DiagnosticLog(paths);
        history = new HistoryStore(paths, log);
        states = new RotationStateStore(paths, log);
        configs = new ConfigStore(paths, log);
    }

    public void Dispose()
    {
        http.Dispose();
        temp.Dispose();
    }

    private RotationService Create(FakeProvider wallhaven, FakeProvider? nasa = null, Random? random = null) =>
        new(wallhaven, nasa ?? new FakeProvider("NASA", (_, _) => null), new ImageDownloader(http, paths), history, states, configs, log,
            (path, _) => applied.Add(path), random,
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });

    private async Task SaveConfig(Action<AppConfig> change)
    {
        var config = await configs.LoadAsync();
        change(config);
        await configs.SaveAsync(config);
    }

    [Theory]
    [InlineData(AutomaticRotationMode.WallhavenTop, false, "Wallhaven", WallpaperSortMode.TopMonth)]
    [InlineData(AutomaticRotationMode.Random, true, "Wallhaven", WallpaperSortMode.Random)]
    [InlineData(AutomaticRotationMode.Mixed, true, "Wallhaven", WallpaperSortMode.TopMonth)]
    public void ProviderChoiceFollowsMode(AutomaticRotationMode mode, bool nsfwOnly, string provider, WallpaperSortMode sort)
    {
        var service = Create(new FakeProvider("Wallhaven", (_, _) => null));
        var choice = service.ChooseProvider(new AppConfig { RotationMode = mode, NsfwOnly = nsfwOnly });
        Assert.Equal(provider, choice.Provider.Name);
        Assert.Equal(sort, choice.SortMode);
    }

    [Theory]
    [InlineData(AutomaticRotationMode.Random, WallpaperSortMode.Random)]
    [InlineData(AutomaticRotationMode.Mixed, WallpaperSortMode.TopMonth)]
    public void WeightsSelectProvider(AutomaticRotationMode mode, WallpaperSortMode wallhavenSort)
    {
        var service = Create(new FakeProvider("Wallhaven", (_, _) => null));
        Assert.Equal("NASA", service.ChooseProvider(new AppConfig { RotationMode = mode, WallhavenWeight = 0, NasaWeight = 1 }).Provider.Name);
        var wallhaven = service.ChooseProvider(new AppConfig { RotationMode = mode, WallhavenWeight = 1, NasaWeight = 0 });
        Assert.Equal("Wallhaven", wallhaven.Provider.Name);
        Assert.Equal(wallhavenSort, wallhaven.SortMode);
    }

    [Fact]
    public async Task RateLimitWaitsBeforeNextAttempt()
    {
        await SaveConfig(c => { c.RotationMode = AutomaticRotationMode.WallhavenTop; c.MaximumAttempts = 3; });
        var calls = 0;
        var wallhaven = new FakeProvider("Wallhaven", (_, _) =>
            ++calls == 1 ? throw new HttpRequestException("limited", null, HttpStatusCode.TooManyRequests) : null);

        Assert.Null(await Create(wallhaven).RotateAsync());
        Assert.Equal(3, calls);
        Assert.Equal([RotationService.RateLimitDelay], delays);
    }

    [Fact]
    public async Task RequestTimeoutCountsAsFailedAttempt()
    {
        await SaveConfig(c => { c.RotationMode = AutomaticRotationMode.WallhavenTop; c.MaximumAttempts = 2; });
        var calls = 0;
        var wallhaven = new FakeProvider("Wallhaven", (_, _) => ++calls == 1 ? throw new TaskCanceledException("HttpClient timeout") : null);
        Assert.Null(await Create(wallhaven).RotateAsync());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task UserCancellationStopsRotation()
    {
        await SaveConfig(c => c.RotationMode = AutomaticRotationMode.WallhavenTop);
        using var cancellation = new CancellationTokenSource();
        var wallhaven = new FakeProvider("Wallhaven", (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return null;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(wallhaven).RotateAsync(cancellation.Token));
        Assert.Single(wallhaven.ExcludedSets);
    }

    [Fact]
    public async Task HistoryAndBlockedWallpapersAreExcluded()
    {
        await SaveConfig(c => { c.RotationMode = AutomaticRotationMode.WallhavenTop; c.MaximumAttempts = 1; });
        var shown = new HistoryEntry("Wallhaven", "shown", "t", "https://wallhaven.cc/w/shown", Path.Combine(paths.Wallpapers, "a.jpg"), null, DateTimeOffset.Now);
        await history.AddAsync(shown, 10);
        var wallhaven = new FakeProvider("Wallhaven", (_, _) => null);
        var service = Create(wallhaven);
        await service.BlockAsync(shown with { Id = "blocked" });

        await service.RotateAsync();

        Assert.Contains("Wallhaven:shown", wallhaven.ExcludedSets[0]);
        Assert.Contains("Wallhaven:blocked", wallhaven.ExcludedSets[0]);
    }

    [Fact]
    public async Task ManualCandidatesUseSelectionAndRememberIt()
    {
        await SaveConfig(c => { c.WallhavenQuery = "auto"; c.AllowSketchy = true; });
        var wallhaven = new FakeProvider("Wallhaven", (_, _) => null);
        await Create(wallhaven).GetManualCandidatesAsync(new WallpaperSelection("city", "100", WallpaperContentMode.Sfw));

        var saved = await configs.LoadAsync();
        Assert.Equal("city", saved.ManualQuery);
        Assert.Equal("100", saved.ManualCategories);
        Assert.Equal("auto", saved.WallhavenQuery);
        Assert.True(saved.AllowSketchy);
        Assert.Equal([WallpaperSortMode.Random], wallhaven.SortModes);
    }

    [Fact]
    public async Task PreviousStepsFurtherBackOnEachCall()
    {
        for (var i = 0; i < 4; i++)
        {
            var file = Path.Combine(paths.Wallpapers, $"{i}.jpg");
            if (i != 1) await File.WriteAllTextAsync(file, "x"); // Entry 1 has lost its file and is skipped.
            await history.AddAsync(new HistoryEntry("NASA", i.ToString(System.Globalization.CultureInfo.InvariantCulture), "t", "https://images.nasa.gov", file, null, DateTimeOffset.Now), 10);
        }
        var service = Create(new FakeProvider("Wallhaven", (_, _) => null));

        Assert.Equal("2", (await service.SetPreviousAsync())?.Id);
        Assert.Equal("0", (await service.SetPreviousAsync())?.Id);
        Assert.Null(await service.SetPreviousAsync());
        Assert.Equal([Path.Combine(paths.Wallpapers, "2.jpg"), Path.Combine(paths.Wallpapers, "0.jpg")], applied);
    }
}
