using System.Net;

namespace WallpaperRotator.Tests;

public class ProviderTests
{
    [Fact]
    public void NasaAssetSelectionPrefersOriginalAndUpgradesToHttps()
    {
        var selected = NasaProvider.SelectBestAsset([
            "http://images-assets.nasa.gov/image/a/a~large.jpg",
            "http://images-assets.nasa.gov/image/a/a~orig.jpg",
            "https://example/x.png"
        ]);
        Assert.Equal("https://images-assets.nasa.gov/image/a/a~orig.jpg", selected);
    }

    [Fact]
    public void NasaAssetSelectionRejectsPlainHttpOnOtherHosts()
    {
        Assert.Null(NasaProvider.SelectBestAsset(["http://example.com/a~orig.jpg", "not a url", null]));
    }

    [Fact]
    public async Task NasaSkipsImagesTooSmallByHeaderAndFallsBackToFirstPage()
    {
        var handler = new StubHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "images-api.nasa.gov" && uri.AbsolutePath == "/search")
            {
                return uri.Query.Contains("page=1&")
                    ? Http.Json("""{"collection":{"items":[{"data":[{"nasa_id":"small","title":"Small"}]},{"data":[{"nasa_id":"big","title":"Big","center":"JPL"}]}]}}""")
                    : Http.Json("""{"collection":{"items":[]}}""");
            }
            if (uri.AbsolutePath.StartsWith("/asset/", StringComparison.Ordinal))
            {
                var id = uri.AbsolutePath["/asset/".Length..];
                return Http.Json($$$"""{"collection":{"items":[{"href":"http://images-assets.nasa.gov/image/{{{id}}}/{{{id}}}~orig.jpg"}]}}""");
            }
            if (uri.Host == "images-assets.nasa.gov")
            {
                Assert.NotNull(request.Headers.Range);
                return Http.Bytes(uri.AbsolutePath.Contains("small") ? Jpeg.Header(1920, 1080) : Jpeg.Header(5000, 3000), "image/jpeg");
            }
            return Http.Status(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler);
        // Seed 2 makes the first random search page greater than one, which exercises the fallback.
        var provider = new NasaProvider(http, new Random(2));
        var candidate = await provider.GetCandidateAsync(new AppConfig { MinimumWidth = 3840, MinimumHeight = 2160 }, new HashSet<string>(), default);

        Assert.NotNull(candidate);
        Assert.Equal("big", candidate.Id);
        Assert.Equal("JPL", candidate.Credit);
        Assert.Equal("https://images-assets.nasa.gov/image/big/big~orig.jpg", candidate.DownloadUrl);
        var searches = handler.Requests.Where(x => x.RequestUri!.AbsolutePath == "/search").ToArray();
        Assert.Equal(2, searches.Length);
        Assert.Contains("page=1&", searches[1].RequestUri!.Query);
    }

    [Fact]
    public async Task WallhavenParsesResultsAndExcludesKnownIds()
    {
        const string json = """{"data":[{"id":"abc123","url":"https://wallhaven.cc/w/abc123","path":"https://w.wallhaven.cc/a.jpg","dimension_x":4000,"dimension_y":2400}]}""";
        var handler = new StubHandler(_ => Http.Json(json));
        using var http = new HttpClient(handler);
        var provider = new WallhavenProvider(http, () => "secret");
        var config = new AppConfig { MinimumWidth = 3840, MinimumHeight = 2160, AllowNsfw = true, NsfwOnly = true };

        var candidate = await provider.GetCandidateAsync(config, new HashSet<string>(), default);
        Assert.Equal("abc123", candidate!.Id);
        Assert.Contains("purity=001", handler.Requests[0].RequestUri!.Query);
        Assert.Equal("secret", handler.Requests[0].Headers.GetValues("X-API-Key").Single());
        Assert.DoesNotContain("secret", handler.Requests[0].RequestUri!.ToString());

        Assert.Null(await provider.GetCandidateAsync(config, new HashSet<string> { "Wallhaven:abc123" }, default));
    }

    [Fact]
    public async Task WallhavenWithoutKeyAlwaysRequestsSfw()
    {
        var handler = new StubHandler(_ => Http.Json("""{"data":[]}"""));
        using var http = new HttpClient(handler);
        var provider = new WallhavenProvider(http, () => null);
        await provider.GetCandidateAsync(new AppConfig { AllowNsfw = true, NsfwOnly = true }, new HashSet<string>(), default);
        Assert.Contains("purity=100", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task WallhavenToplistUsesRandomPageAndFallsBackToFirstPage()
    {
        const string json = """
            {"data":[
            {"id":"one","url":"https://wallhaven.cc/w/one","path":"https://w.wallhaven.cc/one.jpg","dimension_x":4000,"dimension_y":2400,"favorites":42,"thumbs":{"large":"https://th.wallhaven.cc/lg/one.jpg"}},
            {"id":"two","url":"https://wallhaven.cc/w/two","path":"https://w.wallhaven.cc/two.jpg","dimension_x":4000,"dimension_y":2400,"favorites":7,"thumbs":{"large":"https://th.wallhaven.cc/lg/two.jpg"}},
            {"id":"portrait","url":"https://wallhaven.cc/w/portrait","path":"https://w.wallhaven.cc/p.jpg","dimension_x":2400,"dimension_y":4000,"favorites":99,"thumbs":{"large":"https://th.wallhaven.cc/lg/p.jpg"}}
            ]}
            """;
        var handler = new StubHandler(request => request.RequestUri!.Query.Contains("page=") ? Http.Json("""{"data":[]}""") : Http.Json(json));
        using var http = new HttpClient(handler);
        // Seed 2 makes the random toplist page greater than one.
        var provider = new WallhavenProvider(http, () => null, new Random(2));
        var candidates = await provider.GetCandidatesAsync(
            new AppConfig { MinimumWidth = 3840, MinimumHeight = 2160, WallhavenQuery = "nature landscape" },
            new HashSet<string> { "Wallhaven:two" }, 10, WallpaperSortMode.TopMonth, default);

        Assert.Single(candidates);
        Assert.Equal("one", candidates[0].Id);
        Assert.Equal(42, candidates[0].Favorites);
        Assert.Equal("https://th.wallhaven.cc/lg/one.jpg", candidates[0].ThumbnailUrl);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("page=", handler.Requests[0].RequestUri!.Query);
        var query = handler.Requests[1].RequestUri!.Query;
        Assert.Contains("sorting=toplist", query);
        Assert.Contains("topRange=1M", query);
        Assert.Contains("q=nature%20landscape", query);
        Assert.DoesNotContain("page=", query);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.InternalServerError, null)]
    public async Task WallhavenKeyValidation(HttpStatusCode status, bool? expected)
    {
        var handler = new StubHandler(_ => Http.Status(status));
        using var http = new HttpClient(handler);
        var result = await new WallhavenProvider(http, () => null).ValidateKeyAsync("key", default);
        Assert.Equal(expected, result);
        Assert.Equal("/api/v1/settings", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("key", handler.Requests[0].Headers.GetValues("X-API-Key").Single());
    }

    [Fact]
    public void JpegProbeReadsFrameSize()
    {
        Assert.True(ImageProbe.TryReadJpegSize(Jpeg.Header(5120, 2880), out var width, out var height));
        Assert.Equal(5120, width);
        Assert.Equal(2880, height);
    }

    [Fact]
    public void JpegProbeRejectsOtherData()
    {
        var header = Jpeg.Header(5120, 2880);
        Assert.False(ImageProbe.TryReadJpegSize([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A], out _, out _));
        Assert.False(ImageProbe.TryReadJpegSize(header.AsSpan(0, 30), out _, out _));
        Assert.False(ImageProbe.TryReadJpegSize([], out _, out _));
    }
}
