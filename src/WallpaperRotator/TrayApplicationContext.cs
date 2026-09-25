using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace WallpaperRotator;

public sealed record TrayServices(
    RotationService Rotation,
    DailyRotationCoordinator DailyRotation,
    ConfigStore Configs,
    SecretStore Secrets,
    HistoryStore History,
    RotationStateStore States,
    WallhavenProvider Wallhaven,
    AppPaths Paths,
    DiagnosticLog Log,
    HttpClient Http);

public sealed class TrayApplicationContext : ApplicationContext
{
    private const string AppName = "Wallpaper Rotator";
    private static readonly TimeSpan OrphanMinimumAge = TimeSpan.FromHours(1);
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private readonly TrayServices services;
    private readonly NotifyIcon tray;
    private readonly SynchronizationContext uiContext;
    private readonly System.Windows.Forms.Timer intervalTimer = new();
    private readonly System.Windows.Forms.Timer dailyTimer = new() { Interval = (int)RotationTiming.DailyCheckInterval.TotalMilliseconds };
    private readonly TrayMenu menu;
    private CancellationTokenSource? operation;
    private HistoryEntry? current;
    private string? status;
    private bool paused;
    private RotationSchedule? schedule;
    private DateTimeOffset? nextIntervalRotation;
    private bool intervalFailureReported;
    private bool exiting;

    public TrayApplicationContext(TrayServices services)
    {
        this.services = services;
        uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        menu = new TrayMenu(new TrayMenuActions(
            Choose: () => Run(ChooseAndRotateAsync),
            Next: () => Run(RotateManuallyAsync),
            Previous: () => Run(PreviousAsync),
            Cancel: () => operation?.Cancel(),
            OpenSource: () => OpenUrl(current?.SourceUrl),
            AddToFavorites: AddCurrentToFavorites,
            Block: () => Run(BlockCurrentAsync),
            OpenWallpapers: () => OpenPath(services.Paths.Wallpapers),
            OpenFavorites: () => OpenPath(services.Paths.Favorites),
            OpenLog: () => OpenPath(services.Paths.Log),
            TogglePause: () => Run(TogglePauseAsync),
            Settings: () => Run(ConfigureSettingsAsync),
            Wallhaven: () => Run(ConfigureKeyAsync),
            ToggleAutoStart: ToggleAutoStart,
            Exit: ExitThread), HeaderImage());
        menu.Strip.Opening += (_, _) => menu.Update(MenuState());
        tray = new NotifyIcon { Icon = appIcon, Text = AppName, Visible = true, ContextMenuStrip = menu.Strip };
        tray.DoubleClick += (_, _) => Run(ChooseAndRotateAsync);
        intervalTimer.Tick += (_, _) => Run(RunIntervalRotationAsync);
        dailyTimer.Tick += (_, _) => Run(() => TryDailyRotationAsync(notifyFailure: false));
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        Run(InitializeAsync);
    }

    private bool Busy => operation is not null;

    /// <summary>The small frame of the EXE icon; the associated icon is 32 px only and blurs when shrunk.</summary>
    private static Bitmap? HeaderImage()
    {
        using var icon = Icon.ExtractIcon(Application.ExecutablePath, 0, SystemInformation.SmallIconSize.Width);
        return icon?.ToBitmap();
    }

    private TrayMenuState MenuState() => new(
        current?.Title,
        HttpsUri(current?.SourceUrl) is not null,
        DescribeSchedule(),
        Busy,
        paused,
        AutoStartService.IsEnabled());

    private string DescribeSchedule()
    {
        if (status is not null) return char.ToUpperInvariant(status[0]) + status[1..];
        if (paused) return "Автосмена на паузе";
        return schedule switch
        {
            RotationSchedule.Daily => "Автосмена раз в день",
            RotationSchedule.Interval when nextIntervalRotation is { } next => next.Date == DateTimeOffset.Now.Date
                ? $"Следующая смена в {next:HH:mm}"
                : string.Create(Russian, $"Следующая смена {next:d MMMM} в {next:HH:mm}"),
            _ => "Автосмена включена"
        };
    }

    /// <summary>Keeps an open menu in sync when an operation starts or ends underneath it.</summary>
    private void RefreshMenu()
    {
        if (!exiting && menu.Strip.Visible) menu.Update(MenuState());
    }

    private async Task InitializeAsync()
    {
        current = (await services.History.LoadAsync()).FirstOrDefault();
        var removed = await services.History.CleanupOrphansAsync(OrphanMinimumAge, DateTime.UtcNow);
        if (removed > 0) services.Log.Write($"Removed {removed} orphaned wallpaper file(s).");
        dailyTimer.Start();
        await ApplyScheduleAsync(notifyFailure: true);
    }

    private async Task ChooseAndRotateAsync()
    {
        if (Busy) return;
        var config = await services.Configs.LoadAsync();
        using var dialog = new WallpaperSelectionDialog(!string.IsNullOrEmpty(services.Secrets.Load()), config);
        if (dialog.ShowDialog() != DialogResult.OK || !TryBeginOperation("поиск вариантов…", out var token)) return;
        try
        {
            var candidates = await services.Rotation.GetManualCandidatesAsync(dialog.Selection, 10, token);
            if (candidates.Count == 0)
            {
                Show("Не удалось найти подходящие варианты. Текущий фон сохранён.", ToolTipIcon.Warning);
                return;
            }
            using var preview = new WallpaperPreviewDialog(candidates, services.Http);
            if (preview.ShowDialog() != DialogResult.OK || preview.SelectedCandidate is null) return;
            SetStatus("загрузка оригинала…");
            var changed = await services.Rotation.SetCandidateAsync(preview.SelectedCandidate, token);
            await ReportChangeAsync(changed, "Не удалось установить выбранные обои.", notifyFailure: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { ShowCancelled(); }
        catch (Exception ex)
        {
            services.Log.Write("Unexpected manual wallpaper selection error.", ex);
            Show("Ошибка выбора обоев. Подробности в diagnostics.log.", ToolTipIcon.Error);
        }
        finally { EndOperation(); }
    }

    private async Task RotateManuallyAsync()
    {
        if (!TryBeginOperation("загрузка…", out var token)) return;
        try
        {
            var changed = await services.Rotation.RotateAsync(token);
            await ReportChangeAsync(changed, "Не удалось найти подходящие обои. Текущий фон сохранён.", notifyFailure: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { ShowCancelled(); }
        catch (Exception ex)
        {
            services.Log.Write("Unexpected rotation error.", ex);
            Show("Ошибка смены обоев. Подробности в diagnostics.log.", ToolTipIcon.Error);
        }
        finally { EndOperation(); }
    }

    private async Task PreviousAsync()
    {
        if (Busy) return;
        var previous = await services.Rotation.SetPreviousAsync();
        if (previous is null) Show("Более ранних обоев в истории нет.", ToolTipIcon.Info);
        else current = previous;
    }

    private async Task BlockCurrentAsync()
    {
        if (current is null)
        {
            Show("Текущие обои неизвестны.", ToolTipIcon.Warning);
            return;
        }
        await services.Rotation.BlockAsync(current);
        Show($"«{current.Title}» больше не будет показываться.", ToolTipIcon.Info);
        await RotateManuallyAsync();
    }

    private void AddCurrentToFavorites()
    {
        try
        {
            if (current is null || !File.Exists(current.FilePath))
            {
                Show("Файл текущих обоев не найден.", ToolTipIcon.Warning);
                return;
            }
            services.Paths.EnsureCreated();
            File.Copy(current.FilePath, Path.Combine(services.Paths.Favorites, Path.GetFileName(current.FilePath)), true);
            Show("Обои сохранены в избранное.", ToolTipIcon.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            services.Log.Write("Could not add wallpaper to favorites.", ex);
            Show("Не удалось сохранить обои в избранное.", ToolTipIcon.Error);
        }
    }

    private async Task ConfigureKeyAsync()
    {
        var config = await services.Configs.LoadAsync();
        var hasApiKey = !string.IsNullOrEmpty(services.Secrets.Load());
        using var dialog = new KeyDialog(hasApiKey, config.AllowSketchy, config.AllowNsfw, services.Secrets.Load, services.Wallhaven.ValidateKeyAsync);
        if (dialog.ShowDialog() != DialogResult.OK) return;
        if (dialog.RemoveKey) services.Secrets.Delete();
        else if (!string.IsNullOrEmpty(dialog.ApiKey)) services.Secrets.Save(dialog.ApiKey);
        var keyIsAvailable = !dialog.RemoveKey && (hasApiKey || !string.IsNullOrEmpty(dialog.ApiKey));
        config.AllowSketchy = keyIsAvailable && dialog.AllowSketchy;
        config.AllowNsfw = keyIsAvailable && dialog.AllowNsfw;
        if (!keyIsAvailable) config.NsfwOnly = false;
        await services.Configs.SaveAsync(config);
        Show("Настройки Wallhaven сохранены.", ToolTipIcon.Info);
    }

    private async Task ConfigureSettingsAsync()
    {
        var config = await services.Configs.LoadAsync();
        var state = await services.States.LoadAsync();
        var previousStyle = config.WallpaperStyle;
        using var dialog = new SettingsDialog(config, !string.IsNullOrEmpty(services.Secrets.Load()), state.BlockedIds.Count, Program.PrimaryScreenSize());
        if (dialog.ShowDialog() != DialogResult.OK) return;
        dialog.ApplyTo(config);
        await services.Configs.SaveAsync(config);
        if (dialog.ClearBlocked) await services.States.UpdateAsync(s => s.BlockedIds.Clear());
        if (config.WallpaperStyle != previousStyle && current is not null && File.Exists(current.FilePath))
            WallpaperService.Set(current.FilePath, config.WallpaperStyle);
        Show("Настройки сохранены.", ToolTipIcon.Info);
        await ApplyScheduleAsync(notifyFailure: true);
    }

    private void ToggleAutoStart()
    {
        try
        {
            AutoStartService.SetEnabled(!AutoStartService.IsEnabled());
        }
        catch (Exception ex)
        {
            services.Log.Write("Could not change autostart.", ex);
            Show("Не удалось изменить автозапуск.", ToolTipIcon.Error);
        }
    }

    private async Task TogglePauseAsync()
    {
        paused = !paused;
        Show(paused ? "Автоматическая смена приостановлена." : "Автоматическая смена возобновлена.", ToolTipIcon.Info);
        await ApplyScheduleAsync(notifyFailure: true);
    }

    private async Task ApplyScheduleAsync(bool notifyFailure)
    {
        intervalTimer.Stop();
        nextIntervalRotation = null;
        if (paused) return;
        var config = await services.Configs.LoadAsync();
        schedule = config.Schedule;
        if (config.Schedule == RotationSchedule.Daily)
        {
            await TryDailyRotationAsync(notifyFailure);
            return;
        }
        // The interval counts from the last successful change, so restarting the app does not reset it.
        var state = await services.States.LoadAsync();
        StartIntervalTimer(RotationTiming.GetIntervalDelay(state.LastSuccessfulRotation, TimeSpan.FromHours(config.IntervalHours), DateTimeOffset.Now));
    }

    private void StartIntervalTimer(TimeSpan delay)
    {
        intervalTimer.Stop();
        intervalTimer.Interval = (int)Math.Clamp(delay.TotalMilliseconds, 1000, int.MaxValue);
        intervalTimer.Start();
        nextIntervalRotation = DateTimeOffset.Now.AddMilliseconds(intervalTimer.Interval);
    }

    private async Task RunIntervalRotationAsync()
    {
        intervalTimer.Stop();
        var config = await services.Configs.LoadAsync();
        if (paused || config.Schedule != RotationSchedule.Interval) return;
        if (!TryBeginOperation("автоматическая смена…", out var token))
        {
            StartIntervalTimer(RotationTiming.RetryDelay);
            return;
        }
        var succeeded = false;
        try
        {
            var changed = await services.Rotation.RotateAsync(token);
            succeeded = changed is not null;
            await ReportChangeAsync(changed, "Автоматическая смена не удалась. Повторю позже.", notifyFailure: !intervalFailureReported);
            intervalFailureReported = !succeeded;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { ShowCancelled(); }
        catch (Exception ex) { services.Log.Write("Unexpected interval rotation error.", ex); }
        finally { EndOperation(); }
        if (succeeded) await ApplyScheduleAsync(notifyFailure: false);
        else if (!paused) StartIntervalTimer(RotationTiming.RetryDelay);
    }

    private async Task TryDailyRotationAsync(bool notifyFailure)
    {
        if (paused || Busy) return;
        var config = await services.Configs.LoadAsync();
        if (config.Schedule != RotationSchedule.Daily) return;
        await services.DailyRotation.TryRunAsync(paused, async _ =>
        {
            if (!TryBeginOperation("ежедневная смена…", out var token)) return false;
            try
            {
                var changed = await services.Rotation.RotateAsync(token);
                await ReportChangeAsync(changed, "Ежедневная смена не удалась. Текущий фон сохранён, повторю позже.", notifyFailure);
                return changed is not null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                ShowCancelled();
                return false;
            }
            catch (Exception ex)
            {
                services.Log.Write("Unexpected daily rotation error.", ex);
                if (notifyFailure) Show("Ошибка ежедневной смены. Подробности в diagnostics.log.", ToolTipIcon.Error);
                return false;
            }
            finally { EndOperation(); }
        });
    }

    private async Task ReportChangeAsync(HistoryEntry? changed, string failureMessage, bool notifyFailure)
    {
        if (changed is null)
        {
            if (notifyFailure) Show(failureMessage, ToolTipIcon.Warning);
            return;
        }
        current = changed;
        if ((await services.Configs.LoadAsync()).ShowSuccessNotifications) Show($"Установлено: {changed.Title}", ToolTipIcon.Info);
    }

    private bool TryBeginOperation(string description, out CancellationToken token)
    {
        token = default;
        if (Busy) return false;
        operation = new CancellationTokenSource();
        token = operation.Token;
        SetStatus(description);
        RefreshMenu();
        return true;
    }

    private void EndOperation()
    {
        operation?.Dispose();
        operation = null;
        SetStatus(null);
        RefreshMenu();
    }

    private void SetStatus(string? text)
    {
        status = text;
        if (!exiting) tray.Text = text is null ? AppName : $"{AppName} — {text}";
    }

    private void ShowCancelled() => Show("Загрузка отменена. Текущий фон сохранён.", ToolTipIcon.Info);

    /// <summary>Runs a menu or timer action; every failure is logged instead of reaching the WinForms message loop.</summary>
    private async void Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            services.Log.Write("Unexpected tray action error.", ex);
            Show("Произошла ошибка. Подробности в diagnostics.log.", ToolTipIcon.Error);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) uiContext.Post(_ => Run(() => ApplyScheduleAsync(notifyFailure: true)), null);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock) uiContext.Post(_ => Run(() => TryDailyRotationAsync(notifyFailure: true)), null);
    }

    private void OpenUrl(string? url)
    {
        if (HttpsUri(url) is { } uri) OpenPath(uri.AbsoluteUri);
    }

    private static Uri? HttpsUri(string? url) =>
        !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;

    private void OpenPath(string path)
    {
        try
        {
            services.Paths.EnsureCreated();
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { services.Log.Write("Could not open requested item.", ex); }
    }

    private void Show(string message, ToolTipIcon icon)
    {
        if (!exiting) tray.ShowBalloonTip(4000, AppName, message, icon);
    }

    protected override void ExitThreadCore()
    {
        exiting = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        operation?.Cancel();
        intervalTimer.Stop();
        intervalTimer.Dispose();
        dailyTimer.Stop();
        dailyTimer.Dispose();
        tray.Visible = false;
        tray.Dispose();
        menu.Dispose();
        base.ExitThreadCore();
    }
}
