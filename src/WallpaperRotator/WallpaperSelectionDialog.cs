namespace WallpaperRotator;

public sealed class WallpaperSelectionDialog : Form
{
    private sealed record Theme(string Name, string Query, string Categories);
    private static readonly Theme[] Themes =
    [
        new("Случайная", "", "111"),
        new("Природа", "nature landscape", "100"),
        new("Космос", "space galaxy", "100"),
        new("Города", "city architecture", "100"),
        new("Минимализм", "minimalism", "100"),
        new("Аниме", "anime", "010"),
        new("Своя тема…", "", "111")
    ];

    private readonly ComboBox theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 400 };
    private readonly TextBox query = new() { Width = 400, Enabled = false };
    private readonly RadioButton sfw = new() { Text = "Только SFW (безопасный контент)", AutoSize = true, Checked = true };
    private readonly RadioButton nsfw = new() { Text = "Только NSFW (18+, Wallhaven)", AutoSize = true };

    public WallpaperSelection Selection
    {
        get
        {
            var selected = Themes[theme.SelectedIndex];
            return new(query.Text.Trim(), selected.Categories,
                nsfw.Checked ? WallpaperContentMode.Nsfw : WallpaperContentMode.Sfw);
        }
    }

    public WallpaperSelectionDialog(bool hasWallhavenKey, AppConfig config)
    {
        Text = "Выбор новых обоев"; Width = 480; Height = 390; StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        theme.Items.AddRange(Themes.Select(x => x.Name).ToArray());
        theme.SelectedIndexChanged += (_, _) =>
        {
            var selected = Themes[theme.SelectedIndex];
            query.Enabled = theme.SelectedIndex == Themes.Length - 1;
            query.Text = query.Enabled ? config.WallhavenQuery : selected.Query;
            if (query.Enabled) query.Focus();
        };
        var savedTheme = Array.FindIndex(Themes, x => x.Query == config.WallhavenQuery && x.Categories == config.WallhavenCategories);
        theme.SelectedIndex = savedTheme >= 0 ? savedTheme : Themes.Length - 1;
        nsfw.Enabled = hasWallhavenKey;
        nsfw.Checked = hasWallhavenKey && config.NsfwOnly;
        sfw.Checked = !nsfw.Checked;

        var content = new GroupBox { Text = "Тип контента", Width = 420, Height = 92 };
        var contentPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        contentPanel.Controls.AddRange([sfw, nsfw]); content.Controls.Add(contentPanel);
        var note = new Label
        {
            Text = hasWallhavenKey ? "NSFW запрашивается отдельно и не смешивается с SFW."
                : "Для NSFW сначала добавьте API-ключ Wallhaven в меню приложения.",
            AutoSize = true, MaximumSize = new Size(420, 0),
            ForeColor = hasWallhavenKey ? SystemColors.ControlText : Color.DarkRed
        };
        var ok = new Button { Text = "Сменить обои", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 42, Padding = new Padding(6) };
        buttons.Controls.AddRange([cancel, ok]);
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12), WrapContents = false };
        panel.Controls.AddRange([new Label { Text = "Тема:", AutoSize = true }, theme,
            new Label { Text = "Поисковый запрос:", AutoSize = true }, query, content, note]);
        Controls.Add(panel); Controls.Add(buttons); AcceptButton = ok; CancelButton = cancel;
    }
}
