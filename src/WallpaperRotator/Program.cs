namespace WallpaperRotator;

internal static class Program
{
    public static string Version { get; } = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    [STAThread]
    private static void Main()
    {
        var paths = new AppPaths();
        paths.EnsureCreated();
        var log = new DiagnosticLog(paths);
        // A tray app has no window to show a crash in; log unexpected errors instead of silently exiting.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => log.Write("Unhandled UI error.", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Write("Unhandled fatal error.", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Write("Unobserved background error.", e.Exception);
            e.SetObserved();
        };
        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(true, "Local\\WallpaperRotator.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("Wallpaper Rotator уже запущен.", "Wallpaper Rotator");
            return;
        }

        var configs = new ConfigStore(paths, log, PrimaryScreenSize);
        var secrets = new SecretStore(paths, log);
        using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"WallpaperRotator/{Version} (+personal desktop utility)");
        var wallhaven = new WallhavenProvider(http, secrets.Load);
        var nasa = new NasaProvider(http);
        var history = new HistoryStore(paths, log);
        var states = new RotationStateStore(paths, log);
        var downloader = new ImageDownloader(http, paths);
        var rotation = new RotationService(wallhaven, nasa, downloader, history, states, configs, log);
        var dailyRotation = new DailyRotationCoordinator(states, () => DateTime.Now);
        Application.Run(new TrayApplicationContext(new TrayServices(
            rotation, dailyRotation, configs, secrets, history, states, wallhaven, paths, log, http)));
    }

    internal static (int Width, int Height)? PrimaryScreenSize() =>
        Screen.PrimaryScreen?.Bounds is { Width: > 0, Height: > 0 } bounds ? (bounds.Width, bounds.Height) : null;
}
