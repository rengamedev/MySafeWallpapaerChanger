using System.Diagnostics;

namespace WallpaperRotator;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly RotationService rotation;
    private readonly ConfigStore configs;
    private readonly SecretStore secrets;
    private readonly AppPaths paths;
    private readonly DiagnosticLog log;
    private readonly System.Windows.Forms.Timer timer = new();
    private readonly ToolStripMenuItem pauseItem;
    private readonly ToolStripMenuItem autoStartItem;
    private HistoryEntry? current;
    private bool busy;

    public TrayApplicationContext(RotationService rotation, ConfigStore configs, SecretStore secrets, AppPaths paths, DiagnosticLog log)
    {
        this.rotation = rotation; this.configs = configs; this.secrets = secrets; this.paths = paths; this.log = log;
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
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        var config = await configs.LoadAsync();
        timer.Interval = checked(config.IntervalHours * 60 * 60 * 1000);
        timer.Start();
        current = (await new HistoryStore(paths, log).LoadAsync()).FirstOrDefault();
    }
    private async Task ChooseAndRotateAsync()
    {
        var config = await configs.LoadAsync();
        using var dialog = new WallpaperSelectionDialog(!string.IsNullOrEmpty(secrets.Load()), config);
        if (dialog.ShowDialog() == DialogResult.OK) await RotateAsync(dialog.Selection);
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
        timer.Interval = checked(config.IntervalHours * 60 * 60 * 1000);
        Show("Настройки сохранены.", ToolTipIcon.Info);
    }
    private void ToggleAutoStart(object? sender, EventArgs e) { try { AutoStartService.SetEnabled(!autoStartItem.Checked); autoStartItem.Checked = AutoStartService.IsEnabled(); } catch (Exception ex) { log.Write("Could not change autostart.", ex); } }
    private void TogglePause(object? sender, EventArgs e) { pauseItem.Checked = !pauseItem.Checked; if (pauseItem.Checked) timer.Stop(); else timer.Start(); }
    private void OpenUrl(string? url) { if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps) OpenPath(uri.AbsoluteUri); }
    private void OpenPath(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { log.Write("Could not open requested item.", ex); } }
    private void Show(string message, ToolTipIcon icon) { tray.ShowBalloonTip(4000, "Wallpaper Rotator", message, icon); }
    protected override void ExitThreadCore() { timer.Stop(); timer.Dispose(); tray.Visible = false; tray.Dispose(); base.ExitThreadCore(); }
}
