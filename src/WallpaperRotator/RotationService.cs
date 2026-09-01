namespace WallpaperRotator;

public sealed class RotationService(
    IReadOnlyList<IWallpaperProvider> providers,
    ImageDownloader downloader,
    HistoryStore history,
    ConfigStore configs,
    DiagnosticLog log,
    Random? random = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Random rng = random ?? Random.Shared;

    public async Task<HistoryEntry?> RotateAsync(WallpaperSelection? selection = null, CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) return null;
        try
        {
            var config = await configs.LoadAsync();
            if (selection is not null)
            {
                ApplySelection(config, selection);
                await configs.SaveAsync(config);
            }
            var entries = await history.LoadAsync();
            var ids = entries.Select(x => $"{x.Provider}:{x.Id}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var attempt = 0; attempt < config.MaximumAttempts; attempt++)
            {
                try
                {
                    var (provider, sortMode) = ChooseProvider(config);
                    var candidate = provider is IWallpaperSearchProvider searchProvider
                        ? (await searchProvider.GetCandidatesAsync(config, ids, 1, sortMode, cancellationToken)).FirstOrDefault()
                        : await provider.GetCandidateAsync(config, ids, cancellationToken);
                    if (candidate is null) continue;
                    return await InstallCandidateUnsafeAsync(candidate, config, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { log.Write($"Wallpaper attempt {attempt + 1} failed.", ex); }
            }
            return null;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<WallpaperCandidate>> GetManualCandidatesAsync(
        WallpaperSelection selection,
        int count = 10,
        CancellationToken cancellationToken = default)
    {
        var config = await configs.LoadAsync();
        ApplySelection(config, selection);
        await configs.SaveAsync(config);
        var entries = await history.LoadAsync();
        var ids = entries.Select(x => $"{x.Provider}:{x.Id}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var provider = providers.OfType<IWallpaperSearchProvider>().First(x => x.Name == "Wallhaven");
        return await provider.GetCandidatesAsync(config, ids, count, WallpaperSortMode.Random, cancellationToken);
    }

    public async Task<HistoryEntry?> SetCandidateAsync(WallpaperCandidate candidate, CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) return null;
        try { return await InstallCandidateUnsafeAsync(candidate, await configs.LoadAsync(), cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task<HistoryEntry?> SetPreviousAsync()
    {
        var entries = await history.LoadAsync();
        var previous = entries.Skip(1).FirstOrDefault(x => File.Exists(x.FilePath));
        if (previous is not null) WallpaperService.Set(previous.FilePath);
        return previous;
    }

    private async Task<HistoryEntry> InstallCandidateUnsafeAsync(WallpaperCandidate candidate, AppConfig config, CancellationToken cancellationToken)
    {
        var downloaded = await downloader.DownloadAsync(candidate, config, cancellationToken);
        try { WallpaperService.Set(downloaded.Path); }
        catch { try { File.Delete(downloaded.Path); } catch { } throw; }
        var entry = new HistoryEntry(candidate.Provider, candidate.Id, candidate.Title, candidate.SourceUrl,
            downloaded.Path, candidate.Credit, DateTimeOffset.Now);
        await history.AddAsync(entry, config.HistoryLimit);
        return entry;
    }

    private static void ApplySelection(AppConfig config, WallpaperSelection selection)
    {
        config.WallhavenQuery = selection.Query;
        config.WallhavenCategories = selection.Categories;
        config.AllowSketchy = false;
        config.AllowNsfw = selection.ContentMode == WallpaperContentMode.Nsfw;
        config.NsfwOnly = selection.ContentMode == WallpaperContentMode.Nsfw;
    }

    private (IWallpaperProvider Provider, WallpaperSortMode SortMode) ChooseProvider(AppConfig config)
    {
        var wallhaven = providers.First(x => x.Name == "Wallhaven");
        var nasa = providers.First(x => x.Name == "NASA");
        if (config.NsfwOnly || config.RotationMode == AutomaticRotationMode.WallhavenTop)
            return (wallhaven, config.RotationMode == AutomaticRotationMode.Random ? WallpaperSortMode.Random : WallpaperSortMode.TopMonth);

        if (config.RotationMode == AutomaticRotationMode.Random)
            return (rng.Next(config.WallhavenWeight + config.NasaWeight) < config.WallhavenWeight ? wallhaven : nasa, WallpaperSortMode.Random);

        var selected = rng.Next(config.WallhavenWeight + config.NasaWeight) < config.WallhavenWeight ? wallhaven : nasa;
        return (selected, selected == wallhaven ? WallpaperSortMode.TopMonth : WallpaperSortMode.Random);
    }
}
