using System.Text.Json;

namespace WallpaperRotator;

public sealed class HistoryStore(AppPaths paths, DiagnosticLog log)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png"];
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<List<HistoryEntry>> LoadAsync()
    {
        await gate.WaitAsync();
        try { return await TryLoadUnsafeAsync() ?? []; }
        finally { gate.Release(); }
    }

    public async Task AddAsync(HistoryEntry entry, int limit)
    {
        await gate.WaitAsync();
        try
        {
            var entries = await TryLoadUnsafeAsync() ?? [];
            entries.RemoveAll(x => x.Provider == entry.Provider && x.Id == entry.Id);
            entries.Insert(0, entry);
            var removed = entries.Skip(limit).ToArray();
            entries = entries.Take(limit).ToList();
            paths.EnsureCreated();
            await AtomicFile.WriteAllTextAsync(paths.History, JsonSerializer.Serialize(entries, JsonOptions));
            foreach (var old in removed)
            {
                try
                {
                    if (IsManagedWallpaper(old.FilePath) && File.Exists(old.FilePath) &&
                        !entries.Any(x => PathsEqual(x.FilePath, old.FilePath)))
                        File.Delete(old.FilePath);
                }
                catch (Exception ex) { log.Write("Could not remove an old wallpaper.", ex); }
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Deletes wallpaper files that no history entry references and temporary files left by an interrupted download.
    /// Files newer than <paramref name="minimumAge"/> are kept because a download may still be finishing.
    /// </summary>
    public async Task<int> CleanupOrphansAsync(TimeSpan minimumAge, DateTime utcNow)
    {
        await gate.WaitAsync();
        try
        {
            if (!Directory.Exists(paths.Wallpapers)) return 0;
            var entries = await TryLoadUnsafeAsync();
            if (entries is null) return 0; // Unreadable history: never guess which files are still needed.
            var deleted = 0;
            foreach (var file in Directory.EnumerateFiles(paths.Wallpapers))
            {
                try
                {
                    var name = Path.GetFileName(file);
                    var isTemp = name.StartsWith('.') && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                    var isImage = ImageExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
                    if (!isTemp && !isImage) continue;
                    if (utcNow - File.GetLastWriteTimeUtc(file) < minimumAge) continue;
                    if (isImage && entries.Any(x => PathsEqual(x.FilePath, file))) continue;
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    log.Write("Could not remove an orphaned wallpaper file.", ex);
                }
            }
            return deleted;
        }
        finally { gate.Release(); }
    }

    private async Task<List<HistoryEntry>?> TryLoadUnsafeAsync()
    {
        if (!File.Exists(paths.History)) return [];
        try { return JsonSerializer.Deserialize<List<HistoryEntry>>(await File.ReadAllTextAsync(paths.History), JsonOptions) ?? []; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.Write("History could not be read and was ignored.", ex);
            return null;
        }
    }

    private bool IsManagedWallpaper(string filePath)
    {
        var wallpaperRoot = Path.GetFullPath(paths.Wallpapers);
        var candidate = Path.GetFullPath(filePath);
        return string.Equals(Path.GetDirectoryName(candidate), wallpaperRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
