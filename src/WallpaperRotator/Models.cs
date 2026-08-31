namespace WallpaperRotator;

public sealed record WallpaperCandidate(string Provider, string Id, string Title, string SourceUrl, string DownloadUrl, string? Credit = null);

public sealed record HistoryEntry(string Provider, string Id, string Title, string SourceUrl, string FilePath, string? Credit, DateTimeOffset DownloadedAt);

public enum WallpaperContentMode { Sfw, Nsfw }

public sealed record WallpaperSelection(string Query, string Categories, WallpaperContentMode ContentMode);

public interface IWallpaperProvider
{
    string Name { get; }
    Task<WallpaperCandidate?> GetCandidateAsync(AppConfig config, IReadOnlySet<string> recentIds, CancellationToken cancellationToken);
}
