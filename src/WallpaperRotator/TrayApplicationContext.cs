using System.Diagnostics;
using Microsoft.Win32;

namespace WallpaperRotator;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly RotationService rotation;
    private readonly DailyRotationCoordinator dailyRotation;
    private readonly ConfigStore configs;
    private readonly SecretStore secrets;
    private readonly AppPaths paths;
    private readonly DiagnosticLog log;
    private readonly HttpClient http;
    private readonly SynchronizationContext uiContext;
    private readonly System.Windows.Forms.Timer timer = new();
    private readonly ToolStripMenuItem pauseItem;
    private readonly ToolStripMenuItem autoStartItem;
    private HistoryEntry? current;
    private bool busy;

    public TrayApplicationContext(
        RotationService rotation,
        DailyRotationCoordinator dailyRotation,
        ConfigStore configs,
        SecretStore secrets,
        AppPaths paths,
        DiagnosticLog log,
        HttpClient http)
    {
        this.rotation = rotation; this.dailyRotation = dailyRotation; this.configs = configs; this.secrets = secrets;
        this.paths = paths; this.log = log; this.http = http;
        uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Выбрать и сменить обои…", null, async (_, _) => await ChooseAndRotateAsync());
        menu.Items.Add("Следующие обои", null, async (_, _) => await RotateAsync());
        menu.Items.Add("Предыдущие обои", null, async (_, _) => await PreviousAsync());
        menu.Items.Add("Открыть страницу источника", null, (_, _) => OpenUrl(current?.SourceUrl));
        menu.Items.Add("Открыть папку с обоями", null, (_, _) => OpenPath(paths.Wallpapers));
        menu.Items.Add("Настройки…", null, async (_, _) => await ConfigureSettingsAsync());
        menu.Items.Add("Ключ и контент Wallhaven…", null, async (_, _) => await ConfigureKeyAsync());
        menu.Items.Add(new ToolStripSeparator());
        autoStartItem = new ToolStripMenuItem("Запускать вместе с Windows", null, ToggleAutoStart) { Checked = AutoStartService.IsEnabled(), CheckOnClick = false };
        pauseItem = new ToolStripMenuItem("Пауза", null, TogglePause) { CheckOnClick = false };
        menu.Items.Add(autoStartItem); menu.Items.Add(pauseItem);
        menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Выход", null, (_, _) => ExitThread());
        var appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        tray = new NotifyIcon { Icon = appIcon, Text = "Wallpaper Rotator", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += async (_, _) => await ChooseAndRotateAsync();
        timer.Tick += async (_, _) => await RotateAsync();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        var config = await configs.LoadAsync();
        ApplySchedule(config);
        current = (await new HistoryStore(paths, log).LoadAsync()).FirstOrDefault();
        if (config.Schedule == RotationSchedule.Daily) await TryDailyRotationAsync();
    }
    private async Task ChooseAndRotateAsync()
    {
        var config = await configs.LoadAsync();
        using var dialog = new WallpaperSelectionDialog(!string.IsNullOrEmpty(secrets.Load()), config);
        if (dialog.ShowDialog() != DialogResult.OK || busy || pauseItem.Checked) return;
        busy = true; tray.Text = "Wallpaper Rotator — поиск вариантов…";
        try
        {
            var candidates = await rotation.GetManualCandidatesAsync(dialog.Selection, 10);
            if (candidates.Count == 0)
            {
                Show("Не удалось найти подходящие варианты. Текущий фон сохранён.", ToolTipIcon.Warning);
                return;
            }
            using var preview = new WallpaperPreviewDialog(candidates, http);
            if (preview.ShowDialog() != DialogResult.OK || preview.SelectedCandidate is null) return;
            tray.Text = "Wallpaper Rotator — загрузка оригинала…";
            var changed = await rotation.SetCandidateAsync(preview.SelectedCandidate);
            if (changed is null) Show("Не удалось установить выбранные обои.", ToolTipIcon.Warning);
            else { current = changed; Show($"Установлено: {changed.Title}", ToolTipIcon.Info); }
        }
        catch (Exception ex)
        {
            log.Write("Unexpected manual wallpaper selection error.", ex);
            Show("Ошибка выбора обоев. Подробности в diagnostics.log.", ToolTipIcon.Error);
        }
        finally { busy = false; tray.Text = "Wallpaper Rotator"; }
    }
    private async Task RotateAsync(WallpaperSelection? selection = null)
    {
        if (busy || pauseItem.Checked) return;
        busy = true; tray.Text = "Wallpaper Rotator — загрузка…";
        try
        {
            var changed = await rotation.RotateAsync(selection);
            if (changed is null) Show("Не удалось найти подходящие обои. Текущий фон сохранён.", ToolTipIcon.Warning);
            else { current = changed; Show($"Установлено: {changed.Title}", ToolTipIcon.Info); }
        }
        catch (Exception ex) { log.Write("Unexpected rotation error.", ex); Show("Ошибка смены обоев. Подробности в diagnostics.log.", ToolTipIcon.Error); }
        finally { busy = false; tray.Text = "Wallpaper Rotator"; }
    }
    private async Task PreviousAsync()
    {
        try { current = await rotation.SetPreviousAsync() ?? current; }
        catch (Exception ex) { log.Write("Could not restore previous wallpaper.", ex); Show("Не удалось установить предыдущие обои.", ToolTipIcon.Error); }
    }
    private async Task ConfigureKeyAsync()
    {
        var config = await configs.LoadAsync();
        var hasApiKey = !string.IsNullOrEmpty(secrets.Load());
        using var dialog = new KeyDialog(hasApiKey, config.AllowSketchy, config.AllowNsfw);
        if (dialog.ShowDialog() != DialogResult.OK) return;
        if (dialog.RemoveKey) secrets.Delete();
        else if (!string.IsNullOrEmpty(dialog.ApiKey)) secrets.Save(dialog.ApiKey);
        var keyIsAvailable = !dialog.RemoveKey && (hasApiKey || !string.IsNullOrEmpty(dialog.ApiKey));
        config.AllowSketchy = keyIsAvailable && dialog.AllowSketchy;
        config.AllowNsfw = keyIsAvailable && dialog.AllowNsfw;
        if (!keyIsAvailable) config.NsfwOnly = false;
        await configs.SaveAsync(config);
        Show("Настройки Wallhaven сохранены.", ToolTipIcon.Info);
    }
    private async Task ConfigureSettingsAsync()
    {
        var config = await configs.LoadAsync();
        using var dialog = new SettingsDialog(config, !string.IsNullOrEmpty(secrets.Load()));
        if (dialog.ShowDialog() != DialogResult.OK) return;
        dialog.ApplyTo(config);
        await configs.SaveAsync(config);
        ApplySchedule(config);
        Show("Настройки сохранены.", ToolTipIcon.Info);
        if (config.Schedule == RotationSchedule.Daily) await TryDailyRotationAsync();
    }
    private void ToggleAutoStart(object? sender, EventArgs e) { try { AutoStartService.SetEnabled(!autoStartItem.Checked); autoStartItem.Checked = AutoStartService.IsEnabled(); } catch (Exception ex) { log.Write("Could not change autostart.", ex); } }
    private async void TogglePause(object? sender, EventArgs e)
    {
        pauseItem.Checked = !pauseItem.Checked;
        if (pauseItem.Checked) timer.Stop();
        else
        {
            var config = await configs.LoadAsync();
            ApplySchedule(config);
            if (config.Schedule == RotationSchedule.Daily) await TryDailyRotationAsync();
        }
    }
    private void ApplySchedule(AppConfig config)
    {
        timer.Stop();
        timer.Interval = checked(config.IntervalHours * 60 * 60 * 1000);
        if (!pauseItem.Checked && config.Schedule == RotationSchedule.Interval) timer.Start();
    }
    private async Task TryDailyRotationAsync()
    {
        var config = await configs.LoadAsync();
        if (config.Schedule != RotationSchedule.Daily || busy) return;
        await dailyRotation.TryRunAsync(pauseItem.Checked, async cancellationToken =>
        {
            if (busy) return false;
            busy = true; tray.Text = "Wallpaper Rotator — утренняя загрузка…";
            try
            {
                var changed = await rotation.RotateAsync(cancellationToken: cancellationToken);
                if (changed is null)
                {
                    Show("Утренняя смена не удалась. Текущий фон сохранён.", ToolTipIcon.Warning);
                    return false;
                }
                current = changed;
                Show($"Установлено: {changed.Title}", ToolTipIcon.Info);
                return true;
            }
            catch (Exception ex)
            {
                log.Write("Unexpected daily rotation error.", ex);
                Show("Ошибка утренней смены. Подробности в diagnostics.log.", ToolTipIcon.Error);
                return false;
            }
            finally { busy = false; tray.Text = "Wallpaper Rotator"; }
        });
    }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) uiContext.Post(async _ => await TryDailyRotationAsync(), null);
    }
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock) uiContext.Post(async _ => await TryDailyRotationAsync(), null);
    }
    private void OpenUrl(string? url) { if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps) OpenPath(uri.AbsoluteUri); }
    private void OpenPath(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { log.Write("Could not open requested item.", ex); } }
    private void Show(string message, ToolTipIcon icon) { tray.ShowBalloonTip(4000, "Wallpaper Rotator", message, icon); }
    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        timer.Stop(); timer.Dispose(); tray.Visible = false; tray.Dispose(); base.ExitThreadCore();
    }
}
