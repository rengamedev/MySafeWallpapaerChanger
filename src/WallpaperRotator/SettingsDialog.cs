namespace WallpaperRotator;

public sealed class SettingsDialog : Form
{
    private readonly ComboBox schedule = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox rotationMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox wallpaperStyle = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly CheckBox panorama = new() { Text = "Одна картинка на все мониторы", AutoSize = true };
    private readonly NumericUpDown panoramaWidth = Number(1600, 32768, 90);
    private readonly NumericUpDown panoramaHeight = Number(600, 8640, 90);
    private readonly CheckBox showSuccessNotifications = new() { Text = "Уведомлять об успешной смене", AutoSize = true };
    private readonly CheckBox clearBlocked = new() { AutoSize = true };
    private readonly NumericUpDown intervalHours = Number(1, 576);
    private readonly NumericUpDown minimumWidth = Number(800, 16384);
    private readonly NumericUpDown minimumHeight = Number(600, 8640);
    private readonly NumericUpDown historyLimit = Number(1, 100);
    private readonly NumericUpDown maximumFileMegabytes = Number(1, 100);
    private readonly NumericUpDown maximumAttempts = Number(1, 10);
    private readonly NumericUpDown wallhavenWeight = Number(0, 1000);
    private readonly NumericUpDown nasaWeight = Number(0, 1000);
    private readonly TextBox wallhavenQuery = new() { Dock = DockStyle.Fill };
    private readonly ComboBox wallhavenCategories = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly CheckBox allowSketchy = new() { Text = "Разрешить sketchy", AutoSize = true };
    private readonly CheckBox allowNsfw = new() { Text = "Разрешить NSFW", AutoSize = true };
    private readonly CheckBox nsfwOnly = new() { Text = "Только NSFW для автоматической смены", AutoSize = true };
    private readonly TextBox nasaQueries = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Height = 125 };

    private static readonly (string Label, WallpaperStyle Value)[] Styles =
    [
        ("Заполнение", WallpaperStyle.Fill),
        ("По размеру", WallpaperStyle.Fit),
        ("Растянуть", WallpaperStyle.Stretch),
        ("По центру", WallpaperStyle.Center),
        ("Замостить", WallpaperStyle.Tile),
        ("Расширение на все мониторы", WallpaperStyle.Span),
        ("Не менять (как в настройках Windows)", WallpaperStyle.Unchanged)
    ];

    private static readonly (string Label, string Value)[] Categories =
    [
        ("Обычные", "100"),
        ("Аниме", "010"),
        ("Люди", "001"),
        ("Обычные + аниме", "110"),
        ("Обычные + люди", "101"),
        ("Аниме + люди", "011"),
        ("Все категории", "111")
    ];

    public bool ClearBlocked => clearBlocked.Checked;

    public SettingsDialog(
        AppConfig config, bool hasApiKey, int blockedCount, (int Width, int Height)? screenSize, (int Width, int Height)? desktopSize)
    {
        Text = "Настройки Wallpaper Rotator";
        ClientSize = new Size(610, 760);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        schedule.Items.AddRange(["Раз в день при запуске/пробуждении", "Каждые N часов"]);
        schedule.SelectedIndex = (int)config.Schedule;
        rotationMode.Items.AddRange(["Случайный (Wallhaven + NASA)", "Топ Wallhaven за месяц", "Смешанный топ Wallhaven + NASA"]);
        rotationMode.SelectedIndex = (int)config.RotationMode;
        wallpaperStyle.Items.AddRange(Styles.Select(x => x.Label).ToArray());
        wallpaperStyle.SelectedIndex = Math.Max(0, Array.FindIndex(Styles, x => x.Value == config.WallpaperStyle));
        panorama.Checked = config.Panorama;
        panoramaWidth.Value = config.PanoramaWidth;
        panoramaHeight.Value = config.PanoramaHeight;
        showSuccessNotifications.Checked = config.ShowSuccessNotifications;
        clearBlocked.Text = $"Снова показывать скрытые обои ({blockedCount})";
        clearBlocked.Enabled = blockedCount > 0;
        intervalHours.Value = config.IntervalHours;
        minimumWidth.Value = config.MinimumWidth;
        minimumHeight.Value = config.MinimumHeight;
        historyLimit.Value = config.HistoryLimit;
        maximumFileMegabytes.Value = config.MaximumFileMegabytes;
        maximumAttempts.Value = config.MaximumAttempts;
        wallhavenWeight.Value = config.WallhavenWeight;
        nasaWeight.Value = config.NasaWeight;
        wallhavenQuery.Text = config.WallhavenQuery;
        wallhavenCategories.Items.AddRange(Categories.Select(x => x.Label).ToArray());
        wallhavenCategories.SelectedIndex = Math.Max(0, Array.FindIndex(Categories, x => x.Value == config.WallhavenCategories));
        allowSketchy.Checked = config.AllowSketchy && hasApiKey;
        allowNsfw.Checked = config.AllowNsfw && hasApiKey;
        nsfwOnly.Checked = config.NsfwOnly && hasApiKey;
        allowSketchy.Enabled = hasApiKey;
        allowNsfw.Enabled = hasApiKey;
        nsfwOnly.Enabled = hasApiKey;
        nasaQueries.Lines = config.NasaQueries;

        var general = FormTable();
        AddRow(general, "Расписание:", schedule);
        AddRow(general, "Период смены обоев (часов):", intervalHours);
        AddRow(general, "Минимальная ширина (px):", minimumWidth);
        AddRow(general, "Минимальная высота (px):", minimumHeight);
        Button? useScreen = null;
        if (screenSize is { } size)
        {
            useScreen = new Button { Text = $"Как у экрана ({Math.Max(size.Width, size.Height)}×{Math.Min(size.Width, size.Height)})", AutoSize = true };
            useScreen.Click += (_, _) =>
            {
                minimumWidth.Value = Math.Clamp(Math.Max(size.Width, size.Height), (int)minimumWidth.Minimum, (int)minimumWidth.Maximum);
                minimumHeight.Value = Math.Clamp(Math.Min(size.Width, size.Height), (int)minimumHeight.Minimum, (int)minimumHeight.Maximum);
            };
            AddRow(general, "", useScreen);
        }
        AddRow(general, "Расположение обоев:", wallpaperStyle);
        AddRow(general, "Панорама:", panorama);
        var panoramaSize = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        panoramaSize.Controls.AddRange([panoramaWidth, new Label { Text = "×", AutoSize = true, Margin = new Padding(3, 6, 3, 0) }, panoramaHeight]);
        AddRow(general, "Размер панорамы (px):", panoramaSize);
        Button? useDesktop = null;
        // Only side-by-side monitors form a landscape panorama; a vertical stack has no matching wallpapers.
        if (desktopSize is { } desktop && desktop.Width > desktop.Height)
        {
            useDesktop = new Button { Text = $"Как у рабочего стола ({desktop.Width}×{desktop.Height})", AutoSize = true };
            useDesktop.Click += (_, _) =>
            {
                panoramaWidth.Value = Math.Clamp(desktop.Width, (int)panoramaWidth.Minimum, (int)panoramaWidth.Maximum);
                panoramaHeight.Value = Math.Clamp(desktop.Height, (int)panoramaHeight.Minimum, (int)panoramaHeight.Maximum);
            };
            AddRow(general, "", useDesktop);
        }
        var panoramaHint = new Label
        {
            Text = "Одно изображение растягивается на все мониторы; подходят только обои с пропорциями всего рабочего стола " +
                "(два 4K рядом — 7680×2160, 32:9). Источник — только Wallhaven.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 0, 3, 8)
        };
        general.Controls.Add(panoramaHint, 0, general.RowCount);
        general.SetColumnSpan(panoramaHint, 2);
        general.RowCount++;
        AddRow(general, "Количество обоев в истории:", historyLimit);
        AddRow(general, "Максимальный размер файла (МБ):", maximumFileMegabytes);
        AddRow(general, "Попыток загрузки за смену:", maximumAttempts);
        AddRow(general, "Уведомления:", showSuccessNotifications);
        AddRow(general, "Скрытые обои:", clearBlocked);

        var sources = FormTable();
        AddRow(sources, "Автоматическая ротация:", rotationMode);
        AddRow(sources, "Вес Wallhaven:", wallhavenWeight);
        AddRow(sources, "Вес NASA:", nasaWeight);
        AddRow(sources, "Категории Wallhaven:", wallhavenCategories);
        AddRow(sources, "Запрос Wallhaven:", wallhavenQuery);
        var purity = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        purity.Controls.AddRange([allowSketchy, allowNsfw, nsfwOnly]);
        AddRow(sources, "Типы контента:", purity);
        AddRow(sources, "Запросы NASA (по одному в строке):", nasaQueries);
        if (!hasApiKey)
        {
            var warning = new Label { Text = "Sketchy и NSFW доступны после установки API-ключа Wallhaven.", AutoSize = true, ForeColor = Color.DarkRed };
            sources.Controls.Add(warning, 0, sources.RowCount);
            sources.SetColumnSpan(warning, 2);
            sources.RowCount++;
        }

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
        var generalTab = new TabPage("Основные") { Padding = new Padding(12) };
        var sourcesTab = new TabPage("Источники") { Padding = new Padding(12) };
        generalTab.Controls.Add(general);
        sourcesTab.Controls.Add(sources);
        tabs.TabPages.AddRange([generalTab, sourcesTab]);

        var ok = new Button { Text = "Сохранить", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([cancel, ok]);
        Controls.Add(tabs);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        void UpdateControls()
        {
            // Panoramas come only from Wallhaven, so its weights do not apply either.
            var enabled = rotationMode.SelectedIndex != (int)AutomaticRotationMode.WallhavenTop && !panorama.Checked;
            wallhavenWeight.Enabled = enabled;
            nasaWeight.Enabled = enabled;
            wallpaperStyle.Enabled = !panorama.Checked;
            minimumWidth.Enabled = minimumHeight.Enabled = !panorama.Checked;
            if (useScreen is not null) useScreen.Enabled = !panorama.Checked;
            panoramaWidth.Enabled = panoramaHeight.Enabled = panorama.Checked;
            if (useDesktop is not null) useDesktop.Enabled = panorama.Checked;
        }
        schedule.SelectedIndexChanged += (_, _) => intervalHours.Enabled = schedule.SelectedIndex == (int)RotationSchedule.Interval;
        rotationMode.SelectedIndexChanged += (_, _) => UpdateControls();
        panorama.CheckedChanged += (_, _) => UpdateControls();
        intervalHours.Enabled = config.Schedule == RotationSchedule.Interval;
        UpdateControls();
    }

    public void ApplyTo(AppConfig config)
    {
        config.Schedule = (RotationSchedule)schedule.SelectedIndex;
        config.RotationMode = (AutomaticRotationMode)rotationMode.SelectedIndex;
        config.WallpaperStyle = Styles[wallpaperStyle.SelectedIndex].Value;
        config.ShowSuccessNotifications = showSuccessNotifications.Checked;
        config.IntervalHours = (int)intervalHours.Value;
        config.MinimumWidth = (int)minimumWidth.Value;
        config.MinimumHeight = (int)minimumHeight.Value;
        config.Panorama = panorama.Checked;
        config.PanoramaWidth = (int)panoramaWidth.Value;
        config.PanoramaHeight = (int)panoramaHeight.Value;
        config.HistoryLimit = (int)historyLimit.Value;
        config.MaximumFileMegabytes = (int)maximumFileMegabytes.Value;
        config.MaximumAttempts = (int)maximumAttempts.Value;
        config.WallhavenWeight = (int)wallhavenWeight.Value;
        config.NasaWeight = (int)nasaWeight.Value;
        config.WallhavenCategories = Categories[wallhavenCategories.SelectedIndex].Value;
        config.AllowSketchy = allowSketchy.Checked;
        config.AllowNsfw = allowNsfw.Checked;
        config.NsfwOnly = nsfwOnly.Checked;
        config.WallhavenQuery = wallhavenQuery.Text;
        config.NasaQueries = nasaQueries.Lines;
        config.Normalize();
    }

    private static NumericUpDown Number(int minimum, int maximum, int width = 150) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Width = width,
        ThousandsSeparator = true
    };

    private static TableLayoutPanel FormTable() => new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        ColumnCount = 2,
        ColumnStyles = { new ColumnStyle(SizeType.Percent, 55), new ColumnStyle(SizeType.Percent, 45) },
        Padding = new Padding(4)
    };

    private static void AddRow(TableLayoutPanel table, string label, Control control)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 8) }, 0, row);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(3, 5, 3, 5);
        table.Controls.Add(control, 1, row);
    }
}
