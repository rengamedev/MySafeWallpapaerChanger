using System.Drawing;
using System.Security.Cryptography;
using System.Text;

namespace WallpaperRotator;

public sealed class ImageDownloader(HttpClient http, AppPaths paths)
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "w.wallhaven.cc", "images-assets.nasa.gov"
    };

    public async Task<(string Path, int Width, int Height)> DownloadAsync(WallpaperCandidate candidate, AppConfig config, CancellationToken cancellationToken)
    {
        var uri = new Uri(candidate.DownloadUrl);
        if (uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host)) throw new InvalidDataException("Download host is not allowed.");
        paths.EnsureCreated();
        var temp = Path.Combine(paths.Wallpapers, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var mime = response.Content.Headers.ContentType?.MediaType;
            if (mime is not ("image/jpeg" or "image/png")) throw new InvalidDataException("Unsupported image content type.");
            var maxBytes = config.MaximumFileMegabytes * 1024L * 1024L;
            if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("Image is larger than configured limit.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > maxBytes) throw new InvalidDataException("Image is larger than configured limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            var info = Inspect(temp);
            if (info.Width < config.MinimumWidth || info.Height < config.MinimumHeight || info.Width < info.Height)
                throw new InvalidDataException("Image dimensions do not meet configured requirements.");
            var extension = mime == "image/png" ? ".png" : ".jpg";
            var safeId = string.Concat(candidate.Id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
            var idHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Id)))[..12].ToLowerInvariant();
            var final = Path.Combine(paths.Wallpapers, $"{candidate.Provider.ToLowerInvariant()}-{safeId}-{idHash}{extension}");
            File.Move(temp, final, true);
            return (final, info.Width, info.Height);
        }
        catch { try { File.Delete(temp); } catch { } throw; }
    }

    public static (int Width, int Height) Inspect(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
        return (image.Width, image.Height);
    }
}
