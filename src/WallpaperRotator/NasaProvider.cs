using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace WallpaperRotator;

public sealed class NasaProvider(HttpClient http, Random? random = null) : IWallpaperProvider
{
    private readonly Random rng = random ?? Random.Shared;
    public string Name => "NASA";

    public async Task<WallpaperCandidate?> GetCandidateAsync(AppConfig config, IReadOnlySet<string> recentIds, CancellationToken cancellationToken)
    {
        var term = config.NasaQueries[rng.Next(config.NasaQueries.Length)];
        var page = rng.Next(1, 6);
        var url = $"https://images-api.nasa.gov/search?q={Uri.EscapeDataString(term)}&media_type=image&page={page}&page_size=100";
        var result = await http.GetFromJsonAsync<SearchResponse>(url, cancellationToken);
        var items = result?.Collection?.Items?.OrderBy(_ => rng.Next()).ToArray() ?? [];
        foreach (var item in items)
        {
            var data = item.Data?.FirstOrDefault();
            if (data is null || recentIds.Contains($"NASA:{data.NasaId}")) continue;
            var manifest = await http.GetFromJsonAsync<Manifest>($"https://images-api.nasa.gov/asset/{Uri.EscapeDataString(data.NasaId)}", cancellationToken);
            var download = SelectBestAsset(manifest?.Collection?.Items?.Select(x => x.Href) ?? []);
            if (download is null) continue;
            return new(Name, data.NasaId, data.Title, $"https://images.nasa.gov/details/{Uri.EscapeDataString(data.NasaId)}", download, data.Center);
        }
        return null;
    }

    public static string? SelectBestAsset(IEnumerable<string?> assets) => assets
        .Where(x => Uri.TryCreate(x, UriKind.Absolute, out _) && (x!.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)))
        .OrderBy(x => x!.Contains("~orig", StringComparison.OrdinalIgnoreCase) ? 0 : x.Contains("~large", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
        .FirstOrDefault();

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
