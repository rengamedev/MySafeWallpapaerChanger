using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace WallpaperRotator;

public sealed class NasaProvider(HttpClient http, Random? random = null, int maximumProbes = 12) : IWallpaperProvider
{
    private const string AssetHost = "images-assets.nasa.gov";
    private readonly Random rng = random ?? Random.Shared;
    public string Name => "NASA";

    public async Task<WallpaperCandidate?> GetCandidateAsync(AppConfig config, IReadOnlySet<string> excludedIds, CancellationToken cancellationToken)
    {
        var term = config.NasaQueries[rng.Next(config.NasaQueries.Length)];
        var page = rng.Next(1, 6);
        var items = await SearchAsync(term, page, cancellationToken);
        // Narrow queries may have fewer pages than the random pick; the first page always exists.
        if (items.Length == 0 && page > 1) items = await SearchAsync(term, 1, cancellationToken);
        var probes = 0;
        foreach (var item in items.OrderBy(_ => rng.Next()))
        {
            var data = item.Data?.FirstOrDefault();
            if (data is null || string.IsNullOrEmpty(data.NasaId) || excludedIds.Contains(WallpaperKeys.Create(Name, data.NasaId))) continue;
            if (probes++ >= maximumProbes) break;
            var manifest = await http.GetFromJsonAsync<Manifest>($"https://images-api.nasa.gov/asset/{Uri.EscapeDataString(data.NasaId)}", cancellationToken);
            var download = SelectBestAsset(manifest?.Collection?.Items?.Select(x => x.Href) ?? []);
            if (download is null) continue;
            // Most NASA photos are smaller than 4K or portrait; check the JPEG header before downloading the whole file.
            if (await TryProbeAsync(new Uri(download), cancellationToken) is { } size &&
                !ImageRequirements.IsSatisfied(config, size.Width, size.Height)) continue;
            return new(Name, data.NasaId, data.Title, $"https://images.nasa.gov/details/{Uri.EscapeDataString(data.NasaId)}", download, data.Center);
        }
        return null;
    }

    public static string? SelectBestAsset(IEnumerable<string?> assets) => assets
        .Select(NormalizeAssetUrl)
        .Where(x => x is not null &&
            (x.AbsolutePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || x.AbsolutePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)))
        .OrderBy(x => x!.AbsolutePath.Contains("~orig", StringComparison.OrdinalIgnoreCase) ? 0 : x.AbsolutePath.Contains("~large", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
        .Select(x => x!.AbsoluteUri)
        .FirstOrDefault();

    /// <summary>The asset manifest lists plain-HTTP links; the same files are served over HTTPS.</summary>
    private static Uri? NormalizeAssetUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme == Uri.UriSchemeHttp && string.Equals(uri.Host, AssetHost, StringComparison.OrdinalIgnoreCase))
            uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        return uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    private async Task<SearchItem[]> SearchAsync(string term, int page, CancellationToken cancellationToken)
    {
        var url = $"https://images-api.nasa.gov/search?q={Uri.EscapeDataString(term)}&media_type=image&page={page}&page_size=100";
        var result = await http.GetFromJsonAsync<SearchResponse>(url, cancellationToken);
        return result?.Collection?.Items ?? [];
    }

    private async Task<(int Width, int Height)?> TryProbeAsync(Uri uri, CancellationToken cancellationToken)
    {
        try { return await ImageProbe.TryGetJpegSizeAsync(http, uri, cancellationToken); }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null; // Unknown size: the full download still validates the image.
        }
    }

    private sealed class SearchResponse { [JsonPropertyName("collection")] public SearchCollection? Collection { get; set; } }
    private sealed class SearchCollection { [JsonPropertyName("items")] public SearchItem[]? Items { get; set; } }
    private sealed class SearchItem { [JsonPropertyName("data")] public SearchData[]? Data { get; set; } }
    private sealed class SearchData
    {
        [JsonPropertyName("nasa_id")] public string NasaId { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("center")] public string? Center { get; set; }
    }
    private sealed class Manifest { [JsonPropertyName("collection")] public ManifestCollection? Collection { get; set; } }
    private sealed class ManifestCollection { [JsonPropertyName("items")] public ManifestItem[]? Items { get; set; } }
    private sealed class ManifestItem { [JsonPropertyName("href")] public string? Href { get; set; } }
}
