using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace WallpaperRotator;

public sealed class WallhavenProvider(HttpClient http, Func<string?> apiKey) : IWallpaperProvider
{
    public string Name => "Wallhaven";

    public async Task<WallpaperCandidate?> GetCandidateAsync(AppConfig config, IReadOnlySet<string> recentIds, CancellationToken cancellationToken)
    {
        var key = apiKey();
        var purity = "100";
        if (!string.IsNullOrEmpty(key))
            purity = config.NsfwOnly ? "001" : $"1{(config.AllowSketchy ? '1' : '0')}{(config.AllowNsfw ? '1' : '0')}";
        var query = string.Join("&", new[]
        {
            $"sorting=random", $"categories={config.WallhavenCategories}", $"purity={purity}",
            $"atleast={config.MinimumWidth}x{config.MinimumHeight}",
            $"q={Uri.EscapeDataString(config.WallhavenQuery)}"
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://wallhaven.cc/api/v1/search?{query}");
        if (!string.IsNullOrEmpty(key)) request.Headers.Add("X-API-Key", key);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<SearchResponse>(cancellationToken: cancellationToken);
        var item = payload?.Data?.Where(x => x.DimensionX >= config.MinimumWidth && x.DimensionY >= config.MinimumHeight && x.DimensionX >= x.DimensionY)
            .FirstOrDefault(x => !recentIds.Contains($"Wallhaven:{x.Id}"));
        return item is null ? null : new(Name, item.Id, $"Wallhaven {item.Id}", item.Url, item.Path);
    }

    private sealed class SearchResponse { [JsonPropertyName("data")] public Item[]? Data { get; set; } }
    private sealed class Item
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("dimension_x")] public int DimensionX { get; set; }
        [JsonPropertyName("dimension_y")] public int DimensionY { get; set; }
    }
}
