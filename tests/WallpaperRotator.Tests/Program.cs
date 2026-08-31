using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Text;
using WallpaperRotator;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Config missing and normalization", TestConfig),
    ("Config malformed", TestMalformedConfig),
    ("Concurrent config saves remain valid", TestConcurrentConfigSaves),
    ("NASA asset preference", TestNasaAssetSelection),
    ("Wallhaven parsing and recent exclusion", TestWallhaven),
    ("Image validation and download", TestImageDownload),
    ("Distinct IDs use distinct wallpaper files", TestImageFilenameCollision),
    ("History retention", TestHistory),
    ("History cleanup stays inside wallpaper directory", TestHistoryCleanupContainment),
    ("DPAPI secret round-trip", TestSecret)
};
var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed");
return failed == 0 ? 0 : 1;

static async Task TestConfig()
{
    using var temp = new TempDir();
    var paths = new AppPaths(temp.Path); var store = new ConfigStore(paths, new DiagnosticLog(paths));
    var config = await store.LoadAsync();
    Equal(24, config.IntervalHours); True(File.Exists(paths.Config));
    config.IntervalHours = -1; config.HistoryLimit = 900; config.WallhavenWeight = 0; config.NasaWeight = 0; config.NsfwOnly = true;
    await store.SaveAsync(config); var loaded = await store.LoadAsync();
    Equal(1, loaded.IntervalHours); Equal(100, loaded.HistoryLimit); True(loaded.WallhavenWeight > 0); True(loaded.NsfwOnly);
}

static async Task TestMalformedConfig()
{
    using var temp = new TempDir(); var paths = new AppPaths(temp.Path); paths.EnsureCreated();
    await File.WriteAllTextAsync(paths.Config, "{ not-json");
    var loaded = await new ConfigStore(paths, new DiagnosticLog(paths)).LoadAsync();
    Equal(3840, loaded.MinimumWidth); True((await File.ReadAllTextAsync(paths.Log)).Contains("safe defaults"));
}

static async Task TestConcurrentConfigSaves()
{
    using var temp = new TempDir(); var paths = new AppPaths(temp.Path);
    var store = new ConfigStore(paths, new DiagnosticLog(paths));
    var saves = Enumerable.Range(1, 20).Select(i => store.SaveAsync(new AppConfig { IntervalHours = i }));
    await Task.WhenAll(saves);

    var json = await File.ReadAllTextAsync(paths.Config);
    var saved = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json);
    True(saved is not null); True(saved!.IntervalHours is >= 1 and <= 20);
    True(!Directory.EnumerateFiles(paths.Root, "*.tmp").Any());
}

static Task TestNasaAssetSelection()
{
    var selected = NasaProvider.SelectBestAsset(new[] { "https://images-assets.nasa.gov/a~large.jpg", "https://images-assets.nasa.gov/a~orig.jpg", "https://example/x.png" });
    True(selected!.Contains("~orig")); return Task.CompletedTask;
}

static async Task TestWallhaven()
{
    const string json = """{"data":[{"id":"abc123","url":"https://wallhaven.cc/w/abc123","path":"https://w.wallhaven.cc/a.jpg","dimension_x":4000,"dimension_y":2400}]}""";
    Uri? requestedUri = null;
    using var http = new HttpClient(new StubHandler(request => { requestedUri = request.RequestUri; return Json(json); }));
    var provider = new WallhavenProvider(http, () => "secret");
    var config = new AppConfig { MinimumWidth = 3840, MinimumHeight = 2160, AllowNsfw = true, NsfwOnly = true };
    var candidate = await provider.GetCandidateAsync(config, new HashSet<string>(), default);
    Equal("abc123", candidate!.Id);
    True(requestedUri!.Query.Contains("purity=001"));
    var excluded = await provider.GetCandidateAsync(config, new HashSet<string> { "Wallhaven:abc123" }, default);
    True(excluded is null);
}

static async Task TestImageDownload()
{
    using var temp = new TempDir(); using var bitmap = new Bitmap(800, 600);
    await using var memory = new MemoryStream(); bitmap.Save(memory, ImageFormat.Jpeg); var bytes = memory.ToArray();
    using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("image/jpeg") } } }));
    var paths = new AppPaths(temp.Path); var downloader = new ImageDownloader(http, paths);
    var result = await downloader.DownloadAsync(new("Wallhaven", "ok", "title", "https://wallhaven.cc/w/ok", "https://w.wallhaven.cc/a.jpg"),
        new AppConfig { MinimumWidth = 800, MinimumHeight = 600 }, default);
    True(File.Exists(result.Path)); Equal(800, result.Width); Equal(600, result.Height);
}

static async Task TestImageFilenameCollision()
{
    using var temp = new TempDir(); using var bitmap = new Bitmap(800, 600);
    await using var memory = new MemoryStream(); bitmap.Save(memory, ImageFormat.Jpeg); var bytes = memory.ToArray();
    using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("image/jpeg") } } }));
    var downloader = new ImageDownloader(http, new AppPaths(temp.Path));
    var config = new AppConfig { MinimumWidth = 800, MinimumHeight = 600 };

    var first = await downloader.DownloadAsync(new("Wallhaven", "a/b", "one", "https://wallhaven.cc/w/one", "https://w.wallhaven.cc/a.jpg"), config, default);
    var second = await downloader.DownloadAsync(new("Wallhaven", "a?b", "two", "https://wallhaven.cc/w/two", "https://w.wallhaven.cc/b.jpg"), config, default);

    True(!string.Equals(first.Path, second.Path, StringComparison.OrdinalIgnoreCase));
    True(File.Exists(first.Path)); True(File.Exists(second.Path));
}

static async Task TestHistory()
{
    using var temp = new TempDir(); var paths = new AppPaths(temp.Path); paths.EnsureCreated(); var log = new DiagnosticLog(paths); var store = new HistoryStore(paths, log);
    for (var i = 0; i < 3; i++)
    {
        var file = System.IO.Path.Combine(paths.Wallpapers, $"{i}.jpg"); await File.WriteAllTextAsync(file, "x");
        await store.AddAsync(new("NASA", i.ToString(), "t", "https://images.nasa.gov", file, null, DateTimeOffset.Now), 2);
    }
    var entries = await store.LoadAsync(); Equal(2, entries.Count); Equal("2", entries[0].Id); True(!File.Exists(System.IO.Path.Combine(paths.Wallpapers, "0.jpg")));
}

static async Task TestHistoryCleanupContainment()
{
    using var temp = new TempDir(); var paths = new AppPaths(temp.Path); paths.EnsureCreated();
    var external = System.IO.Path.Combine(temp.Path, "keep.txt");
    await File.WriteAllTextAsync(external, "important");
    var poisoned = new[] { new HistoryEntry("NASA", "poisoned", "t", "https://images.nasa.gov", external, null, DateTimeOffset.Now) };
    await File.WriteAllTextAsync(paths.History, System.Text.Json.JsonSerializer.Serialize(poisoned));
    var managed = System.IO.Path.Combine(paths.Wallpapers, "new.jpg");
    await File.WriteAllTextAsync(managed, "x");

    await new HistoryStore(paths, new DiagnosticLog(paths)).AddAsync(
        new("NASA", "new", "t", "https://images.nasa.gov", managed, null, DateTimeOffset.Now), 1);

    True(File.Exists(external));
}

static Task TestSecret()
{
    using var temp = new TempDir(); var paths = new AppPaths(temp.Path); var store = new SecretStore(paths);
    store.Save("top-secret"); Equal("top-secret", store.Load()); True(!Encoding.UTF8.GetString(File.ReadAllBytes(paths.Secret)).Contains("top-secret"));
    store.Delete(); True(store.Load() is null); return Task.CompletedTask;
}

static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
}
sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WallpaperRotatorTests", Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}
