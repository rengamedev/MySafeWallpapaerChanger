namespace WallpaperRotator;

public sealed class DiagnosticLog(AppPaths paths)
{
    private readonly object gate = new();
    public void Write(string message, Exception? error = null)
    {
        try
        {
            paths.EnsureCreated();
            var safe = error is null ? message : $"{message} [{error.GetType().Name}: {error.Message}]";
            lock (gate) File.AppendAllText(paths.Log, $"{DateTimeOffset.Now:O} {safe}{Environment.NewLine}");
        }
        catch { /* Diagnostics must never crash the tray app. */ }
    }
}
