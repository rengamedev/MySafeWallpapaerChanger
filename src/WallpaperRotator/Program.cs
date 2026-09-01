namespace WallpaperRotator;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, "Local\\WallpaperRotator.SingleInstance", out var first);
        if (!first) { MessageBox.Show("Wallpaper Rotator уже запущен.", "Wallpaper Rotator"); return; }
        var paths = new AppPaths(); paths.EnsureCreated();
        var log = new DiagnosticLog(paths);
        var configs = new ConfigStore(paths, log);
        var secrets = new SecretStore(paths);
        var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WallpaperRotator/1.0 (+personal desktop utility)");
        var providers = new IWallpaperProvider[] { new WallhavenProvider(http, secrets.Load), new NasaProvider(http) };
        var history = new HistoryStore(paths, log);
        var downloader = new ImageDownloader(http, paths);
        var rotation = new RotationService(providers, downloader, history, configs, log);
        var dailyRotation = new DailyRotationCoordinator(new RotationStateStore(paths, log), () => DateTime.Now);
        Application.Run(new TrayApplicationContext(rotation, dailyRotation, configs, secrets, paths, log, http));
        http.Dispose();
    }
}
