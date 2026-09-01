namespace WallpaperRotator;

public sealed record WallpaperCandidate(
    string Provider,
    string Id,
    string Title,
    string SourceUrl,
    string DownloadUrl,
    string? Credit = null,
    string? ThumbnailUrl = null,
    int Favorites = 0);

public sealed record HistoryEntry(string Provider, string Id, string Title, string SourceUrl, string FilePath, string? Credit, DateTimeOffset DownloadedAt);

public enum WallpaperContentMode { Sfw, Nsfw }

public enum RotationSchedule { Daily, Interval }

public enum AutomaticRotationMode { Random, WallhavenTop, Mixed }

public enum WallpaperSortMode { Random, TopMonth }

public sealed record WallpaperSelection(string Query, string Categories, WallpaperContentMode ContentMode);

public interface IWallpaperProvider
{
    string Name { get; }
    Task<WallpaperCandidate?> GetCandidateAsync(AppConfig config, IReadOnlySet<string> recentIds, CancellationToken cancellationToken);
}

public interface IWallpaperSearchProvider : IWallpaperProvider
{
    Task<IReadOnlyList<WallpaperCandidate>> GetCandidatesAsync(
        AppConfig config,
        IReadOnlySet<string> recentIds,
        int count,
        WallpaperSortMode sortMode,
        CancellationToken cancellationToken);
}
