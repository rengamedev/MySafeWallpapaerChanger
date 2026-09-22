using System.Drawing;
using System.Drawing.Imaging;
using System.Net;

namespace WallpaperRotator.Tests;

public class DownloaderTests
{
    private static readonly AppConfig SmallMinimum = new() { MinimumWidth = 800, MinimumHeight = 600 };

    private static WallpaperCandidate Candidate(string id, string url = "https://w.wallhaven.cc/a.jpg") =>
        new("Wallhaven", id, "title", $"https://wallhaven.cc/w/{id}", url);

    private static byte[] JpegImage(int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using var memory = new MemoryStream();
        bitmap.Save(memory, ImageFormat.Jpeg);
        return memory.ToArray();
    }

    [WindowsFact]
    public async Task ValidImageIsDownloaded()
    {
        using var temp = new TempDir();
        var bytes = JpegImage(800, 600);
        using var http = new HttpClient(new StubHandler(_ => Http.Bytes(bytes, "image/jpeg")));
        var result = await new ImageDownloader(http, new AppPaths(temp.Path)).DownloadAsync(Candidate("ok"), SmallMinimum, default);
        Assert.True(File.Exists(result.Path));
        Assert.Equal(800, result.Width);
        Assert.Equal(600, result.Height);
    }

    [WindowsFact]
    public async Task DistinctIdsUseDistinctFiles()
    {
        using var temp = new TempDir();
        var bytes = JpegImage(800, 600);
        using var http = new HttpClient(new StubHandler(_ => Http.Bytes(bytes, "image/jpeg")));
        var downloader = new ImageDownloader(http, new AppPaths(temp.Path));
        var first = await downloader.DownloadAsync(Candidate("a/b"), SmallMinimum, default);
        var second = await downloader.DownloadAsync(Candidate("a?b", "https://w.wallhaven.cc/b.jpg"), SmallMinimum, default);
        Assert.NotEqual(first.Path, second.Path, StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(first.Path));
        Assert.True(File.Exists(second.Path));
    }

    [WindowsFact]
    public async Task PortraitImageIsRejectedAndNotKept()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var bytes = JpegImage(600, 800);
        using var http = new HttpClient(new StubHandler(_ => Http.Bytes(bytes, "image/jpeg")));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ImageDownloader(http, paths).DownloadAsync(Candidate("portrait"), new AppConfig { MinimumWidth = 800, MinimumHeight = 600 }, default));
        Assert.Empty(Directory.EnumerateFiles(paths.Wallpapers));
    }

    [Theory]
    [InlineData("http://w.wallhaven.cc/a.jpg")]
    [InlineData("https://example.com/a.jpg")]
    public async Task DisallowedDownloadUrlIsRejectedWithoutRequest(string url)
    {
        using var temp = new TempDir();
        var handler = new StubHandler(_ => throw new InvalidOperationException("No request expected."));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ImageDownloader(http, new AppPaths(temp.Path)).DownloadAsync(Candidate("x", url), SmallMinimum, default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WrongContentTypeIsRejected()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        using var http = new HttpClient(new StubHandler(_ => Http.Bytes([1, 2, 3], "text/html")));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ImageDownloader(http, paths).DownloadAsync(Candidate("x"), SmallMinimum, default));
        Assert.Empty(Directory.EnumerateFiles(paths.Wallpapers));
    }

    [Fact]
    public async Task OversizedImageIsRejectedAndTempFileRemoved()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var bytes = new byte[2 * 1024 * 1024];
        // Without Content-Length the limit is enforced while streaming.
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(bytes)) { Headers = { ContentType = new("image/jpeg") } }
        }));
        var config = new AppConfig { MinimumWidth = 800, MinimumHeight = 600, MaximumFileMegabytes = 1 };
        await Assert.ThrowsAsync<InvalidDataException>(() => new ImageDownloader(http, paths).DownloadAsync(Candidate("big"), config, default));
        Assert.Empty(Directory.EnumerateFiles(paths.Wallpapers));
    }

    [Fact]
    public async Task StalledDownloadTimesOutAndRemovesTempFile()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledStream()) { Headers = { ContentType = new("image/jpeg") } }
        }));
        var downloader = new ImageDownloader(http, paths, TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<TimeoutException>(() => downloader.DownloadAsync(Candidate("slow"), SmallMinimum, default));
        Assert.Empty(Directory.EnumerateFiles(paths.Wallpapers));
    }

    [Fact]
    public async Task SlowButSteadyDownloadIsNotTimedOut()
    {
        using var temp = new TempDir();
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new SlowStream(chunks: 30, chunkSize: 80 * 1024, TimeSpan.FromMilliseconds(60)))
            {
                Headers = { ContentType = new("image/jpeg") }
            }
        }));
        var config = new AppConfig { MinimumWidth = 800, MinimumHeight = 600, MaximumFileMegabytes = 1 };
        var downloader = new ImageDownloader(http, new AppPaths(temp.Path), TimeSpan.FromMilliseconds(500));
        // The whole transfer takes longer than the stall timeout; it ends on the size limit instead of a timeout.
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(Candidate("slow"), config, default));
        Assert.Contains("larger", error.Message);
    }

    [Fact]
    public async Task UserCancellationIsNotReportedAsTimeout()
    {
        using var temp = new TempDir();
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledStream()) { Headers = { ContentType = new("image/jpeg") } }
        }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var downloader = new ImageDownloader(http, new AppPaths(temp.Path), TimeSpan.FromMinutes(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(Candidate("slow"), SmallMinimum, cancellation.Token));
    }

    private sealed class SlowStream(int chunks, int chunkSize, TimeSpan delay) : Stream
    {
        private int remaining = chunks;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (remaining-- <= 0) return 0;
            await Task.Delay(delay, cancellationToken);
            var size = Math.Min(chunkSize, buffer.Length);
            buffer.Span[..size].Clear();
            return size;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A response body that never delivers data, like a connection that stopped responding.</summary>
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
