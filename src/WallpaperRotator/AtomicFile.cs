namespace WallpaperRotator;

internal static class AtomicFile
{
    public static async Task WriteAllTextAsync(string path, string content)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temp, content);
            File.Move(temp, path, true);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* The temporary file may already be gone. */ }
        }
    }
}
