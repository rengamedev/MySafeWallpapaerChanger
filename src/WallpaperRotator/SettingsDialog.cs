namespace WallpaperRotator;

public sealed class SettingsDialog : Form
{
    private readonly ComboBox schedule = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox rotationMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
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

    private static readonly (string Label, string Value)[] Categories =
    [
        ("Обычные", "100"), ("Аниме", "010"), ("Люди", "001"),
        ("Обычные + аниме", "110"), ("Обычные + люди", "101"),
        ("Аниме + люди", "011"), ("Все категории", "111")
    ];

    public SettingsDialog(AppConfig config, bool hasApiKey)
    {
        Text = "Настройки Wallpaper Rotator";
        ClientSize = new Size(610, 580);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        schedule.Items.AddRange(["Раз в день при запуске/пробуждении", "Каждые N часов"]);
        schedule.SelectedIndex = (int)config.Schedule;
        rotationMode.Items.AddRange(["Случайный (Wallhaven + NASA)", "Топ Wallhaven за месяц", "Смешанный топ Wallhaven + NASA"]);
        rotationMode.SelectedIndex = (int)config.RotationMode;
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
        AddRow(general, "Количество обоев в истории:", historyLimit);
        AddRow(general, "Максимальный размер файла (МБ):", maximumFileMegabytes);
        AddRow(general, "Попыток загрузки за смену:", maximumAttempts);

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

        void UpdateSourceControls()
        {
            var enabled = rotationMode.SelectedIndex != (int)AutomaticRotationMode.WallhavenTop;
            wallhavenWeight.Enabled = enabled;
            nasaWeight.Enabled = enabled;
        }
        schedule.SelectedIndexChanged += (_, _) => intervalHours.Enabled = schedule.SelectedIndex == (int)RotationSchedule.Interval;
        rotationMode.SelectedIndexChanged += (_, _) => UpdateSourceControls();
        intervalHours.Enabled = config.Schedule == RotationSchedule.Interval;
        UpdateSourceControls();
    }

    public void ApplyTo(AppConfig config)
    {
        config.Schedule = (RotationSchedule)schedule.SelectedIndex;
        config.RotationMode = (AutomaticRotationMode)rotationMode.SelectedIndex;
        config.IntervalHours = (int)intervalHours.Value;
        config.MinimumWidth = (int)minimumWidth.Value;
        config.MinimumHeight = (int)minimumHeight.Value;
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

    private static NumericUpDown Number(int minimum, int maximum) => new()
    {
        Minimum = minimum, Maximum = maximum, Width = 150, ThousandsSeparator = true
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
