using System.Diagnostics;
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

    private readonly TrayServices services;
    private readonly NotifyIcon tray;
    private readonly SynchronizationContext uiContext;
    private readonly System.Windows.Forms.Timer intervalTimer = new();
    private readonly System.Windows.Forms.Timer dailyTimer = new() { Interval = (int)RotationTiming.DailyCheckInterval.TotalMilliseconds };
    private readonly ToolStripMenuItem cancelItem;
    private readonly ToolStripMenuItem pauseItem;
    private readonly ToolStripMenuItem autoStartItem;
    private CancellationTokenSource? operation;
    private HistoryEntry? current;
    private bool intervalFailureReported;
    private bool exiting;

    public TrayApplicationContext(TrayServices services)
    {
        this.services = services;
        uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Выбрать и сменить обои…", null, (_, _) => Run(ChooseAndRotateAsync));
        menu.Items.Add("Следующие обои", null, (_, _) => Run(RotateManuallyAsync));
        menu.Items.Add("Предыдущие обои", null, (_, _) => Run(PreviousAsync));
        cancelItem = new ToolStripMenuItem("Отменить загрузку", null, (_, _) => operation?.Cancel()) { Enabled = false };
        menu.Items.Add(cancelItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Открыть страницу источника", null, (_, _) => OpenUrl(current?.SourceUrl));
        menu.Items.Add("Добавить в избранное", null, (_, _) => AddCurrentToFavorites());
        menu.Items.Add("Больше не показывать эти обои", null, (_, _) => Run(BlockCurrentAsync));
        menu.Items.Add("Открыть папку с обоями", null, (_, _) => OpenPath(services.Paths.Wallpapers));
        menu.Items.Add("Открыть избранное", null, (_, _) => OpenPath(services.Paths.Favorites));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Настройки…", null, (_, _) => Run(ConfigureSettingsAsync));
        menu.Items.Add("Ключ и контент Wallhaven…", null, (_, _) => Run(ConfigureKeyAsync));
        menu.Items.Add(new ToolStripSeparator());
        autoStartItem = new ToolStripMenuItem("Запускать вместе с Windows", null, ToggleAutoStart) { Checked = AutoStartService.IsEnabled() };
        pauseItem = new ToolStripMenuItem("Пауза автоматической смены", null, (_, _) => Run(TogglePauseAsync));
        menu.Items.Add(autoStartItem);
        menu.Items.Add(pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());
        var appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        tray = new NotifyIcon { Icon = appIcon, Text = AppName, Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Run(ChooseAndRotateAsync);
        intervalTimer.Tick += (_, _) => Run(RunIntervalRotationAsync);
        dailyTimer.Tick += (_, _) => Run(() => TryDailyRotationAsync(notifyFailure: false));
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        Run(InitializeAsync);
    }

    private bool Busy => operation is not null;

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
        var previous = config.Clone();
        using var dialog = new SettingsDialog(config, !string.IsNullOrEmpty(services.Secrets.Load()), state.BlockedIds.Count,
            Program.PrimaryScreenSize(), Program.DesktopSize());
        if (dialog.ShowDialog() != DialogResult.OK) return;
        dialog.ApplyTo(config);
        await services.Configs.SaveAsync(config);
        if (dialog.ClearBlocked) await services.States.UpdateAsync(s => s.BlockedIds.Clear());
        if (current is not null && File.Exists(current.FilePath))
        {
            var size = ImageDownloader.TryReadSize(current.FilePath);
            if (config.StyleFor(size) != previous.StyleFor(size)) WallpaperService.Set(current.FilePath, config.StyleFor(size));
        }
        Show("Настройки сохранены.", ToolTipIcon.Info);
        await ApplyScheduleAsync(notifyFailure: true);
    }

    private void ToggleAutoStart(object? sender, EventArgs e)
    {
        try
        {
            AutoStartService.SetEnabled(!autoStartItem.Checked);
            autoStartItem.Checked = AutoStartService.IsEnabled();
        }
        catch (Exception ex)
        {
            services.Log.Write("Could not change autostart.", ex);
            Show("Не удалось изменить автозапуск.", ToolTipIcon.Error);
        }
    }

    private async Task TogglePauseAsync()
    {
        pauseItem.Checked = !pauseItem.Checked;
        await ApplyScheduleAsync(notifyFailure: true);
    }

    private async Task ApplyScheduleAsync(bool notifyFailure)
    {
        intervalTimer.Stop();
        if (pauseItem.Checked) return;
        var config = await services.Configs.LoadAsync();
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
    }

    private async Task RunIntervalRotationAsync()
    {
        intervalTimer.Stop();
        var config = await services.Configs.LoadAsync();
        if (pauseItem.Checked || config.Schedule != RotationSchedule.Interval) return;
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
        else if (!pauseItem.Checked) StartIntervalTimer(RotationTiming.RetryDelay);
    }

    private async Task TryDailyRotationAsync(bool notifyFailure)
    {
        if (pauseItem.Checked || Busy) return;
        var config = await services.Configs.LoadAsync();
        if (config.Schedule != RotationSchedule.Daily) return;
        await services.DailyRotation.TryRunAsync(pauseItem.Checked, async _ =>
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

    private bool TryBeginOperation(string status, out CancellationToken token)
    {
        token = default;
        if (Busy) return false;
        operation = new CancellationTokenSource();
        token = operation.Token;
        cancelItem.Enabled = true;
        SetStatus(status);
        return true;
    }

    private void EndOperation()
    {
        operation?.Dispose();
        operation = null;
        cancelItem.Enabled = false;
        SetStatus(null);
    }

    private void SetStatus(string? status)
    {
        if (!exiting) tray.Text = status is null ? AppName : $"{AppName} — {status}";
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
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            OpenPath(uri.AbsoluteUri);
    }

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
        base.ExitThreadCore();
    }
}
