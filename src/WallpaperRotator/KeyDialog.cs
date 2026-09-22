namespace WallpaperRotator;

public sealed class KeyDialog : Form
{
    private readonly TextBox key = new() { UseSystemPasswordChar = true, Width = 420 };
    private readonly CheckBox sketchy = new() { Text = "Разрешить sketchy", AutoSize = true };
    private readonly CheckBox nsfw = new() { Text = "Разрешить NSFW", AutoSize = true };
    private readonly CheckBox removeKey = new() { Text = "Удалить сохранённый ключ", AutoSize = true };
    private readonly Button check = new() { Text = "Проверить ключ", AutoSize = true };
    private readonly Label checkResult = new() { AutoSize = true, MaximumSize = new Size(420, 0) };
    private readonly CancellationTokenSource closing = new();
    public string ApiKey => key.Text.Trim();
    public bool AllowSketchy => sketchy.Checked;
    public bool AllowNsfw => nsfw.Checked;
    public bool RemoveKey => removeKey.Checked;

    public KeyDialog(
        bool hasApiKey,
        bool allowSketchy,
        bool allowNsfw,
        Func<string?> storedKey,
        Func<string, CancellationToken, Task<bool?>> validateKey)
    {
        Text = "Ключ Wallhaven";
        Width = 480;
        Height = 340;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        sketchy.Checked = allowSketchy;
        nsfw.Checked = allowNsfw;
        var status = new Label
        {
            Text = hasApiKey ? "Ключ установлен и сохранён" : "Ключ не установлен",
            AutoSize = true,
            ForeColor = hasApiKey ? Color.DarkGreen : Color.DarkRed
        };
        var label = new Label
        {
            Text = hasApiKey ? "Новый API-ключ (пустое поле оставит текущий ключ):" : "API-ключ:",
            AutoSize = true
        };
        removeKey.Visible = hasApiKey;
        removeKey.CheckedChanged += (_, _) =>
        {
            key.Enabled = !removeKey.Checked;
            sketchy.Enabled = !removeKey.Checked;
            nsfw.Enabled = !removeKey.Checked;
            check.Enabled = !removeKey.Checked;
        };
        check.Click += async (_, _) =>
        {
            var candidate = string.IsNullOrEmpty(ApiKey) ? storedKey() : ApiKey;
            if (string.IsNullOrEmpty(candidate))
            {
                ShowCheckResult("Введите ключ для проверки.", Color.DarkRed);
                return;
            }
            check.Enabled = false;
            ShowCheckResult("Проверка…", SystemColors.ControlText);
            try
            {
                var valid = await validateKey(candidate, closing.Token);
                if (IsDisposed) return;
                if (valid == true) ShowCheckResult("Ключ принят Wallhaven.", Color.DarkGreen);
                else if (valid == false) ShowCheckResult("Wallhaven отклонил ключ.", Color.DarkRed);
                else ShowCheckResult("Не удалось связаться с Wallhaven. Попробуйте позже.", Color.DarkOrange);
            }
            catch (OperationCanceledException) { /* The dialog was closed during the check. */ }
            finally
            {
                if (!IsDisposed) check.Enabled = !removeKey.Checked;
            }
        };
        var ok = new Button { Text = "Сохранить", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 42 };
        buttons.Controls.AddRange([cancel, ok]);
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12), WrapContents = false };
        panel.Controls.AddRange([status, label, key, check, checkResult, sketchy, nsfw, removeKey]);
        Controls.Add(panel);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void ShowCheckResult(string text, Color color)
    {
        checkResult.Text = text;
        checkResult.ForeColor = color;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closing.Cancel();
            closing.Dispose();
        }
        base.Dispose(disposing);
    }
}
