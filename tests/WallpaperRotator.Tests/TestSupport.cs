using System.Net;
using System.Text;

namespace WallpaperRotator.Tests;

/// <summary>Skips tests that need GDI+, DPAPI or the registry when the suite runs outside Windows.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows.";
    }
}

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(response(request));
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WallpaperRotatorTests", Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public AppPaths CreatePaths()
    {
        var paths = new AppPaths(Path);
        paths.EnsureCreated();
        return paths;
    }
    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* Best effort cleanup of the test directory. */ }
    }
}

internal static class Http
{
    public static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("") };

    public static HttpResponseMessage Bytes(byte[] bytes, string mediaType) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new(mediaType) } } };
}

internal static class Jpeg
{
    /// <summary>Builds the header of a baseline JPEG (SOI, APP0, DQT, SOF0, SOS) with the given size; enough for the size probe.</summary>
    public static byte[] Header(int width, int height)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        bytes.AddRange([0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);
        bytes.AddRange([0xFF, 0xDB, 0x00, 0x43, 0x00]);
        bytes.AddRange(Enumerable.Repeat((byte)1, 64));
        bytes.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width]);
        bytes.AddRange([0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01]);
        bytes.AddRange([0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00]);
        return [.. bytes];
    }
}

internal sealed class FakeProvider(string name, Func<IReadOnlySet<string>, CancellationToken, WallpaperCandidate?> next) : IWallpaperSearchProvider
{
    public string Name => name;
    public List<IReadOnlySet<string>> ExcludedSets { get; } = [];
    public List<WallpaperSortMode> SortModes { get; } = [];

    public Task<WallpaperCandidate?> GetCandidateAsync(AppConfig config, IReadOnlySet<string> excludedIds, CancellationToken cancellationToken)
    {
        ExcludedSets.Add(excludedIds);
        return Task.FromResult(next(excludedIds, cancellationToken));
    }

    public Task<IReadOnlyList<WallpaperCandidate>> GetCandidatesAsync(
        AppConfig config, IReadOnlySet<string> excludedIds, int count, WallpaperSortMode sortMode, CancellationToken cancellationToken)
    {
        ExcludedSets.Add(excludedIds);
        SortModes.Add(sortMode);
        var candidate = next(excludedIds, cancellationToken);
        return Task.FromResult<IReadOnlyList<WallpaperCandidate>>(candidate is null ? [] : [candidate]);
    }
}
