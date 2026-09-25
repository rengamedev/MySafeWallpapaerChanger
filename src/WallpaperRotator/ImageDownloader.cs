using System.Drawing;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WallpaperRotator;

public sealed class ImageDownloader(HttpClient http, AppPaths paths, TimeSpan? stallTimeout = null)
{
    /// <summary>
    /// HttpClient.Timeout stops applying once headers arrive, so reading the body has its own limit: the download is
    /// abandoned when no data arrives for this long. Slow but steady connections are not affected.
    /// </summary>
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromMinutes(1);

    public static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "w.wallhaven.cc", "images-assets.nasa.gov"
    };

    private readonly TimeSpan timeout = stallTimeout ?? DefaultStallTimeout;

    public static bool IsAllowed(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && AllowedHosts.Contains(uri.Host);

    public async Task<(string Path, int Width, int Height)> DownloadAsync(WallpaperCandidate candidate, AppConfig config, CancellationToken cancellationToken)
    {
        var uri = new Uri(candidate.DownloadUrl);
        if (!IsAllowed(uri)) throw new InvalidDataException("Download host is not allowed.");
        paths.EnsureCreated();
        var temp = Path.Combine(paths.Wallpapers, $".{Guid.NewGuid():N}.tmp");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            response.EnsureSuccessStatusCode();
            var mime = response.Content.Headers.ContentType?.MediaType;
            if (mime is not ("image/jpeg" or "image/png")) throw new InvalidDataException("Unsupported image content type.");
            var maxBytes = config.MaximumFileMegabytes * 1024L * 1024L;
            if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("Image is larger than configured limit.");
            await using (var input = await response.Content.ReadAsStreamAsync(limit.Token))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    limit.CancelAfter(timeout);
                    var read = await input.ReadAsync(buffer, limit.Token);
                    if (read == 0) break;
                    total += read;
                    if (total > maxBytes) throw new InvalidDataException("Image is larger than configured limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), limit.Token);
                }
            }
            var info = Inspect(temp);
            if (!ImageRequirements.IsSatisfied(config, info.Width, info.Height))
                throw new InvalidDataException("Image dimensions do not meet configured requirements.");
            var extension = mime == "image/png" ? ".png" : ".jpg";
            var safeId = string.Concat(candidate.Id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
            var idHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Id)))[..12].ToLowerInvariant();
            var final = Path.Combine(paths.Wallpapers, $"{candidate.Provider.ToLowerInvariant()}-{safeId}-{idHash}{extension}");
            File.Move(temp, final, true);
            return (final, info.Width, info.Height);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            DeleteQuietly(temp);
            throw new TimeoutException("Image download stopped receiving data.");
        }
        catch
        {
            DeleteQuietly(temp);
            throw;
        }
    }

    public static (int Width, int Height) Inspect(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            return (image.Width, image.Height);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
        {
            // GDI+ reports undecodable data with these exception types.
            throw new InvalidDataException("Image could not be decoded.", ex);
        }
    }

    /// <summary>Reads only the size of an image already on disk; null when it is missing or unreadable.</summary>
    public static (int Width, int Height)? TryReadSize(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            return (image.Width, image.Height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ExternalException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); } catch { /* A leftover temp file is removed by the startup cleanup. */ }
    }
}

public static class ImageProbe
{
    public const int ProbeBytes = 256 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reads only the beginning of a JPEG to learn its size, so unsuitable originals are not downloaded in full.
    /// Returns null when the size cannot be determined; the full download still validates the image.
    /// </summary>
    public static async Task<(int Width, int Height)?> TryGetJpegSizeAsync(HttpClient http, Uri uri, CancellationToken cancellationToken)
    {
        if (!ImageDownloader.IsAllowed(uri)) return null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ProbeTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(0, ProbeBytes - 1);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
        var buffer = new byte[ProbeBytes];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), limit.Token)) > 0) total += read;
        return TryReadJpegSize(buffer.AsSpan(0, total), out var width, out var height) ? (width, height) : null;
    }

    public static bool TryReadJpegSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return false;
        var i = 2;
        while (i + 2 <= data.Length)
        {
            if (data[i] != 0xFF) return false;
            var marker = data[i + 1];
            if (marker == 0xFF) { i++; continue; } // Fill byte before a marker.
            i += 2;
            if (marker is 0x01 or 0xD8 or (>= 0xD0 and <= 0xD7)) continue; // Markers without a payload.
            if (marker is 0xD9 or 0xDA) return false; // End of image or scan data before any frame header.
            if (i + 2 > data.Length) return false;
            var length = (data[i] << 8) | data[i + 1];
            if (length < 2) return false;
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (i + 7 > data.Length) return false;
                height = (data[i + 3] << 8) | data[i + 4];
                width = (data[i + 5] << 8) | data[i + 6];
                return width > 0 && height > 0;
            }
            i += length;
        }
        return false;
    }
}
