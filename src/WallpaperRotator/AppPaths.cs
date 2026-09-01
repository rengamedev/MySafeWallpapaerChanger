namespace WallpaperRotator;

public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WallpaperRotator");
        Wallpapers = Path.Combine(Root, "wallpapers");
        Config = Path.Combine(Root, "config.json");
        History = Path.Combine(Root, "history.json");
        State = Path.Combine(Root, "state.json");
        Secret = Path.Combine(Root, "wallhaven-key.dat");
        Log = Path.Combine(Root, "diagnostics.log");
    }

    public string Root { get; }
    public string Wallpapers { get; }
    public string Config { get; }
    public string History { get; }
    public string State { get; }
    public string Secret { get; }
    public string Log { get; }
    public void EnsureCreated() { Directory.CreateDirectory(Root); Directory.CreateDirectory(Wallpapers); }
}
