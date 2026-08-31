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
                config.WallhavenQuery = selection.Query;
                config.WallhavenCategories = selection.Categories;
                config.AllowSketchy = false;
                config.AllowNsfw = selection.ContentMode == WallpaperContentMode.Nsfw;
                config.NsfwOnly = selection.ContentMode == WallpaperContentMode.Nsfw;
                await configs.SaveAsync(config);
            }
            var entries = await history.LoadAsync();
            var ids = entries.Select(x => $"{x.Provider}:{x.Id}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var attempt = 0; attempt < config.MaximumAttempts; attempt++)
            {
                try
                {
                    var provider = ChooseProvider(config);
                    var candidate = await provider.GetCandidateAsync(config, ids, cancellationToken);
                    if (candidate is null) continue;
                    var downloaded = await downloader.DownloadAsync(candidate, config, cancellationToken);
                    try { WallpaperService.Set(downloaded.Path); }
                    catch { try { File.Delete(downloaded.Path); } catch { } throw; }
                    var entry = new HistoryEntry(candidate.Provider, candidate.Id, candidate.Title, candidate.SourceUrl,
                        downloaded.Path, candidate.Credit, DateTimeOffset.Now);
                    await history.AddAsync(entry, config.HistoryLimit);
                    return entry;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { log.Write($"Wallpaper attempt {attempt + 1} failed.", ex); }
            }
            return null;
        }
        finally { gate.Release(); }
    }

    public async Task<HistoryEntry?> SetPreviousAsync()
    {
        var entries = await history.LoadAsync();
        var previous = entries.Skip(1).FirstOrDefault(x => File.Exists(x.FilePath));
        if (previous is not null) WallpaperService.Set(previous.FilePath);
        return previous;
    }

    private IWallpaperProvider ChooseProvider(AppConfig config)
    {
        var wallhaven = providers.First(x => x.Name == "Wallhaven");
        var nasa = providers.First(x => x.Name == "NASA");
        if (config.NsfwOnly) return wallhaven;
        return rng.Next(config.WallhavenWeight + config.NasaWeight) < config.WallhavenWeight ? wallhaven : nasa;
    }
}
