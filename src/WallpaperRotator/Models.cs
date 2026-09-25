using System.Text.Json.Serialization;

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

public sealed record HistoryEntry(string Provider, string Id, string Title, string SourceUrl, string FilePath, string? Credit, DateTimeOffset DownloadedAt)
{
    [JsonIgnore] public string Key => WallpaperKeys.Create(Provider, Id);
}

public enum WallpaperContentMode { Sfw, Nsfw }

public enum RotationSchedule { Daily, Interval }

public enum AutomaticRotationMode { Random, WallhavenTop, Mixed }

public enum WallpaperSortMode { Random, TopMonth }

public enum WallpaperStyle { Fill, Fit, Stretch, Center, Tile, Span, Unchanged }

public sealed record WallpaperSelection(string Query, string Categories, WallpaperContentMode ContentMode);

public interface IWallpaperProvider
{
    string Name { get; }
    Task<WallpaperCandidate?> GetCandidateAsync(AppConfig config, IReadOnlySet<string> excludedIds, CancellationToken cancellationToken);
}

public interface IWallpaperSearchProvider : IWallpaperProvider
{
    Task<IReadOnlyList<WallpaperCandidate>> GetCandidatesAsync(
        AppConfig config,
        IReadOnlySet<string> excludedIds,
        int count,
        WallpaperSortMode sortMode,
        CancellationToken cancellationToken);
}

public static class WallpaperKeys
{
    public static string Create(string provider, string id) => $"{provider}:{id}";
}

public static class ImageRequirements
{
    /// <summary>A panorama may differ from the desktop shape by this fraction; Span crops the excess at the edges.</summary>
    public const double PanoramaAspectTolerance = 0.05;

    public static bool IsSatisfied(AppConfig config, int width, int height) =>
        width >= config.RequiredWidth && height >= config.RequiredHeight && width >= height &&
        (!config.Panorama || IsNearAspect(width, height, (double)config.PanoramaWidth / config.PanoramaHeight));

    public static bool IsNearAspect(int width, int height, double aspect) =>
        height > 0 && Math.Abs((double)width / height / aspect - 1) <= PanoramaAspectTolerance;
}
