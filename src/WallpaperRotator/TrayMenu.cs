using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WallpaperRotator;

public sealed record TrayMenuActions(
    Action Choose,
    Action Next,
    Action Previous,
    Action Cancel,
    Action OpenSource,
    Action AddToFavorites,
    Action Block,
    Action OpenWallpapers,
    Action OpenFavorites,
    Action OpenLog,
    Action TogglePause,
    Action Settings,
    Action Wallhaven,
    Action ToggleAutoStart,
    Action Exit);

/// <summary>What the menu shows when it opens; built by the tray context from its current state.</summary>
public sealed record TrayMenuState(string? CurrentTitle, bool HasSourceUrl, string Status, bool Busy, bool Paused, bool AutoStart);

/// <summary>The tray context menu: grouped items with Fluent icons, drawn in the Windows light or dark theme.</summary>
public sealed class TrayMenu : IDisposable
{
    private const int MaxTitleLength = 42;

    private readonly ToolStripLabel titleLabel;
    private readonly ToolStripLabel statusLabel;
    private readonly ToolStripMenuItem chooseItem;
    private readonly ToolStripMenuItem nextItem;
    private readonly ToolStripMenuItem previousItem;
    private readonly ToolStripMenuItem cancelItem;
    private readonly ToolStripMenuItem currentMenu;
    private readonly ToolStripMenuItem openSourceItem;
    private readonly ToolStripMenuItem blockItem;
    private readonly ToolStripMenuItem pauseItem;
    private readonly ToolStripMenuItem autoStartItem;
    private readonly Dictionary<ToolStripItem, (char Glyph, bool Accent)> glyphs = [];
    private readonly Font boldFont;
    private TrayTheme? theme;

    public TrayMenu(TrayMenuActions actions, Image? headerImage)
    {
        Strip = new ContextMenuStrip { ShowCheckMargin = false, ShowImageMargin = true };
        var scale = Strip.DeviceDpi / 96f;
        Strip.ImageScalingSize = new Size(Scale(16, scale), Scale(16, scale));
        Strip.Padding = new Padding(0, Scale(4, scale), 0, Scale(4, scale));

        boldFont = new Font(Strip.Font, FontStyle.Bold);
        titleLabel = new ToolStripLabel { Image = headerImage, Font = boldFont };
        statusLabel = new ToolStripLabel { Tag = TrayItemRole.Secondary };
        chooseItem = Item("Выбрать обои…", Glyphs.Pictures, actions.Choose, accent: true);
        chooseItem.Font = boldFont;
        chooseItem.ShortcutKeyDisplayString = "Двойной щелчок";
        nextItem = Item("Следующие обои", Glyphs.Next, actions.Next);
        previousItem = Item("Предыдущие обои", Glyphs.Previous, actions.Previous);
        cancelItem = Item("Отменить загрузку", Glyphs.Cancel, actions.Cancel);
        cancelItem.Visible = false;

        openSourceItem = Item("Открыть страницу источника", Glyphs.Globe, actions.OpenSource);
        blockItem = Item("Больше не показывать", Glyphs.Blocked, actions.Block);
        currentMenu = Submenu("Текущие обои", Glyphs.Photo,
            openSourceItem, Item("Добавить в избранное", Glyphs.Star, actions.AddToFavorites), new ToolStripSeparator(), blockItem);

        var foldersMenu = Submenu("Папки", Glyphs.Folder,
            Item("Скачанные обои", Glyphs.Pictures, actions.OpenWallpapers),
            Item("Избранное", Glyphs.Star, actions.OpenFavorites),
            new ToolStripSeparator(),
            Item("Журнал диагностики", Glyphs.Document, actions.OpenLog));

        pauseItem = Item("Приостановить автосмену", Glyphs.Pause, actions.TogglePause);
        autoStartItem = Item("Запускать вместе с Windows", Glyphs.Startup, actions.ToggleAutoStart);
        var settingsMenu = Submenu("Параметры", Glyphs.Settings,
            Item("Настройки…", Glyphs.Settings, actions.Settings),
            Item("Ключ и контент Wallhaven…", Glyphs.Key, actions.Wallhaven),
            new ToolStripSeparator(),
            autoStartItem);

        Strip.Items.AddRange(new ToolStripItem[]
        {
            titleLabel, statusLabel,
            new ToolStripSeparator(),
            chooseItem, nextItem, previousItem, cancelItem,
            new ToolStripSeparator(),
            currentMenu, foldersMenu,
            new ToolStripSeparator(),
            pauseItem, settingsMenu,
            new ToolStripSeparator(),
            Item("Выход", Glyphs.Power, actions.Exit)
        });

        var itemPadding = new Padding(0, Scale(3, scale), 0, Scale(3, scale));
        foreach (var item in AllItems(Strip.Items).OfType<ToolStripMenuItem>()) item.Padding = itemPadding;
        titleLabel.Padding = new Padding(0, Scale(4, scale), 0, 0);
        statusLabel.Padding = new Padding(0, 0, 0, Scale(4, scale));

        RoundCorners(Strip);
        foreach (var dropDown in AllItems(Strip.Items).OfType<ToolStripMenuItem>().Where(x => x.HasDropDownItems)) RoundCorners(dropDown.DropDown);
        ApplyTheme();
    }

    public ContextMenuStrip Strip { get; }

    /// <summary>Refreshes texts, availability and the theme; call before the menu is shown.</summary>
    public void Update(TrayMenuState state)
    {
        ApplyTheme();
        // "&&" keeps a literal ampersand in titles such as "Earth & Moon" from becoming a mnemonic.
        titleLabel.Text = state.CurrentTitle is null ? "Обои ещё не выбраны" : Shorten(state.CurrentTitle).Replace("&", "&&", StringComparison.Ordinal);
        titleLabel.ToolTipText = state.CurrentTitle;
        statusLabel.Text = state.Status;

        chooseItem.Enabled = nextItem.Enabled = previousItem.Enabled = !state.Busy;
        cancelItem.Visible = state.Busy;
        currentMenu.Enabled = state.CurrentTitle is not null;
        openSourceItem.Enabled = state.HasSourceUrl;
        blockItem.Enabled = !state.Busy;

        pauseItem.Text = state.Paused ? "Возобновить автосмену" : "Приостановить автосмену";
        SetGlyph(pauseItem, state.Paused ? Glyphs.Play : Glyphs.Pause, accent: state.Paused);
        // A checked item gets an accent check mark in place of its icon, like the pause toggle above;
        // Checked itself is still set so the state reaches screen readers.
        autoStartItem.Checked = state.AutoStart;
        SetGlyph(autoStartItem, state.AutoStart ? Glyphs.CheckMark : Glyphs.Startup, accent: state.AutoStart);
    }

    internal static string Shorten(string text) =>
        text.Length <= MaxTitleLength ? text : string.Concat(text.AsSpan(0, MaxTitleLength - 1).TrimEnd(), "…");

    private ToolStripMenuItem Item(string text, char glyph, Action action, bool accent = false)
    {
        var item = new ToolStripMenuItem(text, null, (_, _) => action());
        glyphs[item] = (glyph, accent);
        return item;
    }

    private ToolStripMenuItem Submenu(string text, char glyph, params ToolStripItem[] children)
    {
        var item = new ToolStripMenuItem(text);
        item.DropDownItems.AddRange(children);
        if (item.DropDown is ToolStripDropDownMenu dropDown)
        {
            dropDown.ShowCheckMargin = false;
            dropDown.ImageScalingSize = Strip.ImageScalingSize;
            dropDown.Padding = Strip.Padding;
        }
        glyphs[item] = (glyph, false);
        return item;
    }

    private void SetGlyph(ToolStripItem item, char glyph, bool accent)
    {
        if (glyphs.TryGetValue(item, out var old) && old == (glyph, accent) && item.Image is not null) return;
        glyphs[item] = (glyph, accent);
        if (theme is not null) ReplaceImage(item, theme);
    }

    private void ApplyTheme()
    {
        var next = TrayTheme.Current();
        if (next == theme) return;
        theme = next;
        // In high contrast the stock renderer draws with the system colors the user chose.
        ToolStripRenderer renderer = next.HighContrast ? new ToolStripProfessionalRenderer() : new TrayMenuRenderer(next);
        Strip.Renderer = renderer;
        foreach (var item in AllItems(Strip.Items))
        {
            if (item is ToolStripMenuItem { HasDropDownItems: true } parent) parent.DropDown.Renderer = renderer;
            if (glyphs.ContainsKey(item)) ReplaceImage(item, next);
        }
    }

    private void ReplaceImage(ToolStripItem item, TrayTheme current)
    {
        var (glyph, accent) = glyphs[item];
        var old = item.Image;
        item.Image = GlyphRenderer.Render(glyph, Strip.ImageScalingSize.Width, accent ? current.Accent : current.Icon);
        old?.Dispose();
    }

    private static IEnumerable<ToolStripItem> AllItems(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            yield return item;
            if (item is ToolStripMenuItem menuItem)
                foreach (var child in AllItems(menuItem.DropDownItems)) yield return child;
        }
    }

    private static int Scale(int value, float scale) => (int)Math.Round(value * scale);

    private static void RoundCorners(ToolStripDropDown dropDown) =>
        dropDown.HandleCreated += (_, _) => NativeMethods.RequestRoundCorners(dropDown.Handle);

    public void Dispose()
    {
        foreach (var item in AllItems(Strip.Items))
            if (glyphs.ContainsKey(item)) item.Image?.Dispose();
        titleLabel.Image?.Dispose();
        Strip.Dispose();
        boldFont.Dispose();
    }

    /// <summary>Code points shared by Segoe Fluent Icons (Windows 11) and Segoe MDL2 Assets (Windows 10).</summary>
    private static class Glyphs
    {
        public const char Pictures = '\uE8B9';
        public const char Photo = '\uEB9F';
        public const char Next = '\uE893';
        public const char Previous = '\uE892';
        public const char Cancel = '\uE711';
        public const char Globe = '\uE774';
        public const char Star = '\uE734';
        public const char Blocked = '\uE733';
        public const char Folder = '\uE8B7';
        public const char Document = '\uE8A5';
        public const char Pause = '\uE769';
        public const char Play = '\uE768';
        public const char Settings = '\uE713';
        public const char Key = '\uE8D7';
        public const char CheckMark = '\uE73E';
        public const char Startup = '\uE823';
        public const char Power = '\uE7E8';
    }
}

internal enum TrayItemRole { Secondary }

/// <summary>Menu colors for the taskbar theme (the tray follows the system theme, not the apps theme).</summary>
internal sealed record TrayTheme(bool Dark, bool HighContrast, Color Background, Color Border, Color Text, Color SecondaryText, Color DisabledText, Color Icon, Color Hover, Color Separator, Color Accent)
{
    public static TrayTheme Current()
    {
        if (SystemInformation.HighContrast)
        {
            return new(false, true, SystemColors.Menu, SystemColors.WindowFrame, SystemColors.MenuText, SystemColors.MenuText,
                SystemColors.GrayText, SystemColors.MenuText, SystemColors.MenuHighlight, SystemColors.MenuText, SystemColors.MenuText);
        }
        var accent = ReadAccent();
        return ReadSystemUsesLightTheme()
            ? new(false, false, Color.FromArgb(249, 249, 249), Color.FromArgb(222, 222, 222), Color.FromArgb(26, 26, 26),
                Color.FromArgb(96, 96, 96), Color.FromArgb(160, 160, 160), Color.FromArgb(60, 60, 60),
                Color.FromArgb(232, 232, 232), Color.FromArgb(229, 229, 229), accent)
            : new(true, false, Color.FromArgb(44, 44, 44), Color.FromArgb(64, 64, 64), Color.FromArgb(245, 245, 245),
                Color.FromArgb(170, 170, 170), Color.FromArgb(110, 110, 110), Color.FromArgb(220, 220, 220),
                Color.FromArgb(61, 61, 61), Color.FromArgb(64, 64, 64), Lighten(accent));
    }

    private static bool ReadSystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return true; }
    }

    private static Color ReadAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            // Stored as 0xAABBGGRR.
            if (key?.GetValue("AccentColor") is int abgr)
                return Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return Color.FromArgb(0, 120, 212);
    }

    /// <summary>Dark accents are unreadable on a dark menu, so they are mixed with white.</summary>
    private static Color Lighten(Color color) =>
        color.GetBrightness() >= 0.55f ? color : Color.FromArgb((color.R + 255) / 2, (color.G + 255) / 2, (color.B + 255) / 2);
}

internal static class GlyphRenderer
{
    private static readonly string? FontName = new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }
        .FirstOrDefault(name => FontFamily.Families.Any(f => f.Name == name));

    public static Bitmap Render(char glyph, int size, Color color)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        if (FontName is null) return bitmap;
        using var graphics = Graphics.FromImage(bitmap);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(FontName, size * 0.75f, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(glyph.ToString(), font, brush, new RectangleF(0, 0, size, size), format);
        return bitmap;
    }
}

internal sealed class TrayMenuRenderer(TrayTheme theme) : ToolStripProfessionalRenderer
{
    private static readonly bool DrawsOwnBorder = Environment.OSVersion.Version.Build < 22000;

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(theme.Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // Windows 11 draws a themed border around the rounded popup itself.
        if (!DrawsOwnBorder) return;
        using var pen = new Pen(theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var inset = e.Item.Owner?.LogicalToDeviceUnits(4) ?? 4;
        var bounds = new Rectangle(inset, 1, e.Item.Width - inset * 2, e.Item.Height - 2);
        FillRounded(e.Graphics, theme.Hover, bounds, inset);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? theme.DisabledText
            // The shortcut column is rendered with the same event but a different text.
            : e.Item.Tag is TrayItemRole.Secondary || e.Text != e.Item.Text ? theme.SecondaryText
                : theme.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Image is null) return;
        if (e.Item.Enabled)
        {
            e.Graphics.DrawImage(e.Image, e.ImageRectangle);
            return;
        }
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = 0.35f });
        e.Graphics.DrawImage(e.Image, e.ImageRectangle, 0, 0, e.Image.Width, e.Image.Height, GraphicsUnit.Pixel, attributes);
    }

    // Checked items show a check-mark glyph as their image instead of the stock check box.
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled == false ? theme.DisabledText : theme.SecondaryText;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var inset = e.Item.Owner?.LogicalToDeviceUnits(12) ?? 12;
        var y = e.Item.Height / 2;
        using var pen = new Pen(theme.Separator);
        e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
    }

    private static void FillRounded(Graphics graphics, Color color, Rectangle bounds, int radius)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        using var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
        graphics.SmoothingMode = smoothing;
    }
}

internal static class NativeMethods
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmCornerRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Asks Windows 11 to round the popup; earlier versions reject the attribute, which is harmless.</summary>
    public static void RequestRoundCorners(IntPtr hwnd)
    {
        var preference = DwmCornerRound;
        _ = DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref preference, sizeof(int));
    }
}
