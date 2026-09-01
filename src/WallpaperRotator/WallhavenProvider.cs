using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace WallpaperRotator;

public sealed class WallhavenProvider(HttpClient http, Func<string?> apiKey, Random? random = null) : IWallpaperSearchProvider
{
    private readonly Random rng = random ?? Random.Shared;
    public string Name => "Wallhaven";

    public async Task<WallpaperCandidate?> GetCandidateAsync(AppConfig config, IReadOnlySet<string> recentIds, CancellationToken cancellationToken)
        => (await GetCandidatesAsync(config, recentIds, 1, WallpaperSortMode.Random, cancellationToken)).FirstOrDefault();

    public async Task<IReadOnlyList<WallpaperCandidate>> GetCandidatesAsync(
        AppConfig config,
        IReadOnlySet<string> recentIds,
        int count,
        WallpaperSortMode sortMode,
        CancellationToken cancellationToken)
    {
        count = Math.Clamp(count, 1, 24);
        var key = apiKey();
        var purity = "100";
        if (!string.IsNullOrEmpty(key))
            purity = config.NsfwOnly ? "001" : $"1{(config.AllowSketchy ? '1' : '0')}{(config.AllowNsfw ? '1' : '0')}";
        var parameters = new List<string>
        {
            $"sorting={(sortMode == WallpaperSortMode.TopMonth ? "toplist" : "random")}",
            $"categories={config.WallhavenCategories}", $"purity={purity}",
            $"atleast={config.MinimumWidth}x{config.MinimumHeight}",
            $"q={Uri.EscapeDataString(config.WallhavenQuery)}"
        };
        if (sortMode == WallpaperSortMode.TopMonth) parameters.Add("topRange=1M");
        var query = string.Join("&", parameters);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://wallhaven.cc/api/v1/search?{query}");
        if (!string.IsNullOrEmpty(key)) request.Headers.Add("X-API-Key", key);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<SearchResponse>(cancellationToken: cancellationToken);
        return payload?.Data?
            .Where(x => x.DimensionX >= config.MinimumWidth && x.DimensionY >= config.MinimumHeight && x.DimensionX >= x.DimensionY)
            .Where(x => !recentIds.Contains($"Wallhaven:{x.Id}"))
            .DistinctBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(_ => rng.Next())
            .Take(count)
            .Select(x => new WallpaperCandidate(Name, x.Id, $"Wallhaven {x.Id}", x.Url, x.Path,
                ThumbnailUrl: x.Thumbs?.Large, Favorites: x.Favorites))
            .ToArray() ?? [];
    }

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
    private sealed class Thumbnails { [JsonPropertyName("large")] public string? Large { get; set; } }
}
