namespace WallpaperRotator;

public sealed class WallpaperPreviewDialog : Form
{
    private const int MaximumThumbnailBytes = 5 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly CancellationTokenSource closing = new();
    private readonly List<Image> images = [];

    public WallpaperCandidate? SelectedCandidate { get; private set; }

    public WallpaperPreviewDialog(IReadOnlyList<WallpaperCandidate> candidates, HttpClient http)
    {
        this.http = http;
        Text = "Выберите обои";
        ClientSize = new Size(940, 720);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 540);

        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Padding = new Padding(8)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var picture = new PictureBox
            {
                Dock = DockStyle.Fill,
                Height = 220,
                BackColor = Color.FromArgb(35, 35, 35),
                SizeMode = PictureBoxSizeMode.Zoom,
                Cursor = Cursors.Hand
            };
            var caption = new Label
            {
                Text = $"{candidate.Title}  •  в избранном: {candidate.Favorites:N0}",
                Dock = DockStyle.Bottom,
                Height = 30,
                TextAlign = ContentAlignment.MiddleCenter,
                AutoEllipsis = true,
                Cursor = Cursors.Hand
            };
            var card = new Panel
            {
                Width = 440,
                Height = 260,
                Margin = new Padding(8),
                BorderStyle = BorderStyle.FixedSingle,
                Cursor = Cursors.Hand
            };
            card.Controls.Add(picture);
            card.Controls.Add(caption);
            void SelectCandidate(object? _, EventArgs __)
            {
                SelectedCandidate = candidate;
                DialogResult = DialogResult.OK;
                Close();
            }
            card.Click += SelectCandidate;
            picture.Click += SelectCandidate;
            caption.Click += SelectCandidate;
            grid.Controls.Add(card, index % 2, index / 2);
            _ = LoadThumbnailAsync(candidate, picture, closing.Token);
        }

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(grid);
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(8),
            FlowDirection = FlowDirection.RightToLeft
        };
        buttons.Controls.Add(cancel);
        Controls.Add(scroll);
        Controls.Add(buttons);
        CancelButton = cancel;
    }

    private async Task LoadThumbnailAsync(WallpaperCandidate candidate, PictureBox picture, CancellationToken cancellationToken)
    {
        try
        {
            if (!Uri.TryCreate(candidate.ThumbnailUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(uri.Host, "th.wallhaven.cc", StringComparison.OrdinalIgnoreCase)) return;
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not ("image/jpeg" or "image/png")) return;
            if (response.Content.Headers.ContentLength > MaximumThumbnailBytes) return;

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var memory = new MemoryStream();
            var buffer = new byte[32 * 1024];
            var total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > MaximumThumbnailBytes) return;
                await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            memory.Position = 0;
            using var source = Image.FromStream(memory, useEmbeddedColorManagement: false, validateImageData: true);
            var image = new Bitmap(source);
            if (IsDisposed || picture.IsDisposed)
            {
                image.Dispose();
                return;
            }
            images.Add(image);
            picture.Image = image;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ArgumentException or OperationCanceledException or ObjectDisposedException)
        {
            // A missing preview must not prevent choosing or installing the original image.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closing.Cancel();
            closing.Dispose();
            foreach (var image in images) image.Dispose();
        }
        base.Dispose(disposing);
    }
}
