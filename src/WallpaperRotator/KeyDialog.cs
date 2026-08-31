namespace WallpaperRotator;

public sealed class KeyDialog : Form
{
    private readonly TextBox key = new() { UseSystemPasswordChar = true, Dock = DockStyle.Top };
    private readonly CheckBox sketchy = new() { Text = "Разрешить sketchy", AutoSize = true };
    private readonly CheckBox nsfw = new() { Text = "Разрешить NSFW", AutoSize = true };
    private readonly CheckBox removeKey = new() { Text = "Удалить сохранённый ключ", AutoSize = true };
    public string ApiKey => key.Text.Trim();
    public bool AllowSketchy => sketchy.Checked;
    public bool AllowNsfw => nsfw.Checked;
    public bool RemoveKey => removeKey.Checked;

    public KeyDialog(bool hasApiKey, bool allowSketchy, bool allowNsfw)
    {
        Text = "Ключ Wallhaven"; Width = 480; Height = 290; StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        sketchy.Checked = allowSketchy; nsfw.Checked = allowNsfw;
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
        };
        var ok = new Button { Text = "Сохранить", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 42 };
        buttons.Controls.AddRange([cancel, ok]);
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12), WrapContents = false };
        key.Width = 420;
        panel.Controls.AddRange([status, label, key, sketchy, nsfw, removeKey]);
        Controls.Add(panel); Controls.Add(buttons); AcceptButton = ok; CancelButton = cancel;
    }
}
