using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace WallpaperRotator;

public sealed class WallhavenProvider(HttpClient http, Func<string?> apiKey, Random? random = null) : IWallpaperSearchProvider
{
    private const int ToplistPages = 5;

    /// <summary>Wallhaven accepts only its own list of ratios; these are the ultrawide ones a multi-monitor desktop can match.</summary>
    private static readonly (string Name, double Aspect)[] PanoramaRatios = [("21x9", 21.0 / 9), ("32x9", 32.0 / 9), ("48x9", 48.0 / 9)];

    private readonly Random rng = random ?? Random.Shared;
    public string Name => "Wallhaven";

    public async Task<WallpaperCandidate?> GetCandidateAsync(AppConfig config, IReadOnlySet<string> excludedIds, CancellationToken cancellationToken)
    {
        var candidates = await GetCandidatesAsync(config, excludedIds, 1, WallpaperSortMode.Random, cancellationToken);
        return candidates.Count > 0 ? candidates[0] : null;
    }

    public async Task<IReadOnlyList<WallpaperCandidate>> GetCandidatesAsync(
        AppConfig config,
        IReadOnlySet<string> excludedIds,
        int count,
        WallpaperSortMode sortMode,
        CancellationToken cancellationToken)
    {
        count = Math.Clamp(count, 1, 24);
        // One toplist page holds only 24 images; a random page keeps the monthly top from running out after a few days.
        var page = sortMode == WallpaperSortMode.TopMonth ? rng.Next(1, ToplistPages + 1) : 1;
        var candidates = await SearchAsync(config, excludedIds, count, sortMode, page, cancellationToken);
        if (candidates.Count == 0 && page > 1)
            candidates = await SearchAsync(config, excludedIds, count, sortMode, 1, cancellationToken);
        return candidates;
    }

    /// <summary>Returns true for a valid key, false for a rejected key and null when Wallhaven could not be reached.</summary>
    public async Task<bool?> ValidateKeyAsync(string key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://wallhaven.cc/api/v1/settings");
            request.Headers.Add("X-API-Key", key.Trim());
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return true;
            return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? false : null;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<WallpaperCandidate>> SearchAsync(
        AppConfig config,
        IReadOnlySet<string> excludedIds,
        int count,
        WallpaperSortMode sortMode,
        int page,
        CancellationToken cancellationToken)
    {
        var key = apiKey();
        var purity = "100";
        if (!string.IsNullOrEmpty(key))
            purity = config.NsfwOnly ? "001" : $"1{(config.AllowSketchy ? '1' : '0')}{(config.AllowNsfw ? '1' : '0')}";
        // The monthly toplist holds only a couple of panoramas, so their "top" is the all-time favorites instead.
        var sorting = sortMode != WallpaperSortMode.TopMonth ? "random" : config.Panorama ? "favorites" : "toplist";
        var parameters = new List<string>
        {
            $"sorting={sorting}",
            $"categories={config.WallhavenCategories}", $"purity={purity}",
            $"atleast={config.RequiredWidth}x{config.RequiredHeight}",
            $"q={Uri.EscapeDataString(config.WallhavenQuery)}"
        };
        if (sorting == "toplist") parameters.Add("topRange=1M");
        if (GetPanoramaRatio(config) is { } ratio) parameters.Add($"ratios={ratio}");
        if (page > 1) parameters.Add($"page={page}");
        var query = string.Join("&", parameters);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://wallhaven.cc/api/v1/search?{query}");
        if (!string.IsNullOrEmpty(key)) request.Headers.Add("X-API-Key", key);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<SearchResponse>(cancellationToken: cancellationToken);
        return payload?.Data?
            .Where(x => ImageRequirements.IsSatisfied(config, x.DimensionX, x.DimensionY))
            .Where(x => !excludedIds.Contains(WallpaperKeys.Create(Name, x.Id)))
            .DistinctBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(_ => rng.Next())
            .Take(count)
            // The large thumbnail is cropped to 3:2, which would hide most of a panorama.
            .Select(x => new WallpaperCandidate(Name, x.Id, $"Wallhaven {x.Id}", x.Url, x.Path,
                ThumbnailUrl: config.Panorama ? x.Thumbs?.Original ?? x.Thumbs?.Large : x.Thumbs?.Large, Favorites: x.Favorites))
            .ToArray() ?? [];
    }

    /// <summary>
    /// The Wallhaven ratio matching the panorama shape, or null for an unusual layout; the results are then only
    /// filtered locally, which finds far fewer images.
    /// </summary>
    internal static string? GetPanoramaRatio(AppConfig config) => !config.Panorama ? null : PanoramaRatios
        .Where(x => ImageRequirements.IsNearAspect(config.PanoramaWidth, config.PanoramaHeight, x.Aspect))
        .Select(x => x.Name)
        .FirstOrDefault();

    private sealed class SearchResponse { [JsonPropertyName("data")] public Item[]? Data { get; set; } }
    private sealed class Item
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("dimension_x")] public int DimensionX { get; set; }
        [JsonPropertyName("dimension_y")] public int DimensionY { get; set; }
        [JsonPropertyName("favorites")] public int Favorites { get; set; }
        [JsonPropertyName("thumbs")] public Thumbnails? Thumbs { get; set; }
    }
    private sealed class Thumbnails
    {
        [JsonPropertyName("large")] public string? Large { get; set; }
        [JsonPropertyName("original")] public string? Original { get; set; }
    }
}
