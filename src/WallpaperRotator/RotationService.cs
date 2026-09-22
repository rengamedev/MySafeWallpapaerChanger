using System.Net;

namespace WallpaperRotator;

public sealed class RotationService(
    IWallpaperSearchProvider wallhaven,
    IWallpaperProvider nasa,
    ImageDownloader downloader,
    HistoryStore history,
    RotationStateStore states,
    ConfigStore configs,
    DiagnosticLog log,
    Action<string, WallpaperStyle>? applyWallpaper = null,
    Random? random = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    /// <summary>Wallhaven allows 45 API calls per minute; after HTTP 429 the next attempt waits this long.</summary>
    public static readonly TimeSpan RateLimitDelay = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Random rng = random ?? Random.Shared;
    private readonly Action<string, WallpaperStyle> apply = applyWallpaper ?? WallpaperService.Set;
    private readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;
    private int historyPosition;

    public async Task<HistoryEntry?> RotateAsync(CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) return null;
        try
        {
            var config = await configs.LoadAsync();
            var excluded = await GetExcludedIdsAsync();
            for (var attempt = 0; attempt < config.MaximumAttempts; attempt++)
            {
                try
                {
                    var (provider, sortMode) = ChooseProvider(config);
                    var candidate = provider is IWallpaperSearchProvider searchProvider
                        ? FirstOrNull(await searchProvider.GetCandidatesAsync(config, excluded, 1, sortMode, cancellationToken))
                        : await provider.GetCandidateAsync(config, excluded, cancellationToken);
                    if (candidate is null) continue;
                    return await InstallCandidateUnsafeAsync(candidate, config, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    log.Write($"Wallpaper attempt {attempt + 1} was rate limited.", ex);
                    if (attempt + 1 < config.MaximumAttempts) await wait(RateLimitDelay, cancellationToken);
                }
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
        config.RememberManualSelection(selection);
        await configs.SaveAsync(config);
        var excluded = await GetExcludedIdsAsync();
        return await wallhaven.GetCandidatesAsync(config.ForManualSelection(selection), excluded, count, WallpaperSortMode.Random, cancellationToken);
    }

    public async Task<HistoryEntry?> SetCandidateAsync(WallpaperCandidate candidate, CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) return null;
        try { return await InstallCandidateUnsafeAsync(candidate, await configs.LoadAsync(), cancellationToken); }
        finally { gate.Release(); }
    }

    /// <summary>Steps one wallpaper further back in history on every call. Returns null when there is nothing older.</summary>
    public async Task<HistoryEntry?> SetPreviousAsync(CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) return null;
        try
        {
            var config = await configs.LoadAsync();
            var entries = await history.LoadAsync();
            for (var position = historyPosition + 1; position < entries.Count; position++)
            {
                if (!File.Exists(entries[position].FilePath)) continue;
                apply(entries[position].FilePath, config.WallpaperStyle);
                historyPosition = position;
                return entries[position];
            }
            return null;
        }
        finally { gate.Release(); }
    }

    public async Task BlockAsync(HistoryEntry entry)
    {
        await states.UpdateAsync(state =>
        {
            if (!state.BlockedIds.Contains(entry.Key, StringComparer.OrdinalIgnoreCase)) state.BlockedIds.Add(entry.Key);
        });
    }

    private async Task<IReadOnlySet<string>> GetExcludedIdsAsync()
    {
        var entries = await history.LoadAsync();
        var state = await states.LoadAsync();
        var excluded = entries.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        excluded.UnionWith(state.BlockedIds);
        return excluded;
    }

    private async Task<HistoryEntry> InstallCandidateUnsafeAsync(WallpaperCandidate candidate, AppConfig config, CancellationToken cancellationToken)
    {
        var downloaded = await downloader.DownloadAsync(candidate, config, cancellationToken);
        try { apply(downloaded.Path, config.WallpaperStyle); }
        catch
        {
            try { File.Delete(downloaded.Path); } catch { /* The startup cleanup removes it later. */ }
            throw;
        }
        var entry = new HistoryEntry(candidate.Provider, candidate.Id, candidate.Title, candidate.SourceUrl,
            downloaded.Path, candidate.Credit, DateTimeOffset.Now);
        await history.AddAsync(entry, config.HistoryLimit);
        historyPosition = 0;
        // The wallpaper is already changed; a state write failure must not trigger another attempt.
        try { await states.UpdateAsync(state => state.LastSuccessfulRotation = entry.DownloadedAt); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Write("Could not record the rotation time.", ex); }
        return entry;
    }

    private static WallpaperCandidate? FirstOrNull(IReadOnlyList<WallpaperCandidate> candidates) =>
        candidates.Count > 0 ? candidates[0] : null;

    internal (IWallpaperProvider Provider, WallpaperSortMode SortMode) ChooseProvider(AppConfig config)
    {
        if (config.NsfwOnly || config.RotationMode == AutomaticRotationMode.WallhavenTop)
            return (wallhaven, config.RotationMode == AutomaticRotationMode.Random ? WallpaperSortMode.Random : WallpaperSortMode.TopMonth);

        var useWallhaven = rng.Next(config.WallhavenWeight + config.NasaWeight) < config.WallhavenWeight;
        if (config.RotationMode == AutomaticRotationMode.Random)
            return (useWallhaven ? wallhaven : nasa, WallpaperSortMode.Random);
        return useWallhaven ? (wallhaven, WallpaperSortMode.TopMonth) : (nasa, WallpaperSortMode.Random);
    }
}
