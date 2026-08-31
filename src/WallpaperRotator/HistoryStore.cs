using System.Text.Json;

namespace WallpaperRotator;

public sealed class HistoryStore(AppPaths paths, DiagnosticLog log)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<List<HistoryEntry>> LoadAsync()
    {
        await gate.WaitAsync();
        try { return await LoadUnsafeAsync(); }
        finally { gate.Release(); }
    }

    public async Task AddAsync(HistoryEntry entry, int limit)
    {
        await gate.WaitAsync();
        try
        {
            var entries = await LoadUnsafeAsync();
            entries.RemoveAll(x => x.Provider == entry.Provider && x.Id == entry.Id);
            entries.Insert(0, entry);
            var removed = entries.Skip(limit).ToArray();
            entries = entries.Take(limit).ToList();
            await AtomicWriteAsync(JsonSerializer.Serialize(entries, JsonOptions));
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

    private async Task<List<HistoryEntry>> LoadUnsafeAsync()
    {
        if (!File.Exists(paths.History)) return [];
        try { return JsonSerializer.Deserialize<List<HistoryEntry>>(await File.ReadAllTextAsync(paths.History), JsonOptions) ?? []; }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            log.Write("History was invalid and was reset.", ex);
            return [];
        }
    }

    private async Task AtomicWriteAsync(string content)
    {
        paths.EnsureCreated();
        var temp = paths.History + ".tmp";
        await File.WriteAllTextAsync(temp, content);
        File.Move(temp, paths.History, true);
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
