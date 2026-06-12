using CleverPoint.Migrator.App.Services;
using CleverPoint.Migrator.App.Theme;

namespace CleverPoint.Migrator.App.Screens;

/// <summary>
/// "Browse..." picker: connect to a site and choose a list or library from
/// everything the site offers (system lists hidden), with subsite drill-down.
/// </summary>
public class ListPickerDialog : Form
{
    private readonly AppSettings _settings;
    private readonly SiteBrowser _browser;
    private readonly ComboBox _siteUrl = new() { Width = 420 };
    private readonly ListView _list = new();
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 26, ForeColor = Brand.TextSecondary, Padding = new Padding(10, 5, 0, 0) };
    private Core.Csom.SpConnection? _conn;

    public string? SelectedSiteUrl { get; private set; }
    public string? SelectedListTitle { get; private set; }

    public ListPickerDialog(AppSettings settings, string role, string? initialUrl = null)
    {
        _settings = settings;
        _browser = new SiteBrowser(settings.CacheMinutes);
        Text = $"Pick the {role} list or library";
        Size = new Size(620, 560);
        MinimumSize = new Size(540, 460);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Brand.Surface;
        Font = Brand.Body;
        Icon = AppIcon.Create();

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(10, 8, 0, 0) };
        _siteUrl.Text = initialUrl ?? "";
        foreach (var c in settings.Connections) _siteUrl.Items.Add(c.SiteUrl);
        var connect = new Button { Text = "Connect", AutoSize = true, Padding = new Padding(12, 2, 12, 2), FlatStyle = FlatStyle.Flat, BackColor = Brand.Accent, ForeColor = Color.White, Margin = new Padding(10, 0, 0, 0) };
        connect.FlatAppearance.BorderSize = 0;
        top.Controls.AddRange(new Control[] { _siteUrl, connect });
        Controls.Add(top);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.BorderStyle = BorderStyle.None;
        _list.SmallImageList = FileTypeIcons.Shared;
        _list.Columns.Add("Name", 360);
        _list.Columns.Add("Details", 180);
        Controls.Add(_list);
        _list.BringToFront();
        Controls.Add(_status);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 12, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(12, 4, 12, 4), FlatStyle = FlatStyle.Flat, Margin = new Padding(10, 0, 0, 0) };
        var ok = new Button { Text = "Use this list", AutoSize = true, Padding = new Padding(12, 4, 12, 4), FlatStyle = FlatStyle.Flat, BackColor = Brand.Accent, ForeColor = Color.White };
        ok.FlatAppearance.BorderSize = 0;
        bottom.Controls.AddRange(new Control[] { cancel, ok });
        Controls.Add(bottom);
        CancelButton = cancel;

        connect.Click += async (_, _) => await ConnectAsync();
        _list.DoubleClick += async (_, _) => await OnPickAsync(ok);
        ok.Click += async (_, _) => await OnPickAsync(ok);
    }

    private async Task ConnectAsync()
    {
        try
        {
            _status.Text = "Connecting...";
            _conn = ConnectionResolver.Resolve(this, _settings, _siteUrl.Text.Trim());
            await LoadSiteAsync();
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not connect: {ex.Message}";
        }
    }

    private async Task LoadSiteAsync()
    {
        if (_conn == null) return;
        var lists = await Task.Run(() => _browser.GetListsAsync(_conn));
        var webs = await Task.Run(() => _browser.GetSubwebsAsync(_conn));
        _list.Items.Clear();
        foreach (var web in webs)
            _list.Items.Add(new ListViewItem(new[] { web.Title, "Subsite (double-click to open)" }, "site") { Tag = web });
        foreach (var l in lists)
            _list.Items.Add(new ListViewItem(new[] { l.Title, $"{(l.IsLibrary ? "Library" : "List")}, {l.ItemCount} items" },
                l.IsLibrary ? "library" : "list") { Tag = l });
        _status.Text = $"{webs.Count} subsites, {lists.Count} lists and libraries";
    }

    private async Task OnPickAsync(Button ok)
    {
        if (_list.SelectedItems.Count == 0 || _conn == null) return;
        switch (_list.SelectedItems[0].Tag)
        {
            case SpWebInfo web:
                _conn = _conn.ForWeb(web.Url);
                _siteUrl.Text = web.Url;
                await LoadSiteAsync();
                break;
            case SpListInfo info:
                SelectedSiteUrl = _conn.SiteUrl;
                SelectedListTitle = info.Title;
                DialogResult = DialogResult.OK;
                Close();
                break;
        }
    }
}

/// <summary>
/// Crisp programmatic file-type and object icons (no blurry bitmap packs):
/// colored glyph tiles keyed by extension or object kind.
/// </summary>
public static class FileTypeIcons
{
    public static readonly ImageList Shared = Build();

    private static ImageList Build()
    {
        // Real pictograms, drawn at runtime (no licensed assets): folders look
        // like folders, PDFs are the red-label page, images show a tiny photo.
        var list = new ImageList { ImageSize = new Size(20, 20), ColorDepth = ColorDepth.Depth32Bit };
        var pageBorder = Color.FromArgb(0x9A, 0xA3, 0xAD);

        void Add(string key, Action<Graphics> draw)
        {
            var bmp = new Bitmap(20, 20);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            draw(g);
            list.Images.Add(key, bmp);
        }

        // White sheet with a folded top-right corner.
        void Page(Graphics g)
        {
            var sheet = new[] { new Point(4, 1), new Point(12, 1), new Point(16, 5), new Point(16, 18), new Point(4, 18) };
            using var border = new Pen(pageBorder);
            g.FillPolygon(Brushes.White, sheet);
            g.DrawPolygon(border, sheet);
            g.DrawLines(border, new[] { new Point(12, 1), new Point(12, 5), new Point(16, 5) });
        }

        void TextLines(Graphics g, Color color)
        {
            using var pen = new Pen(color, 1.4f);
            for (var y = 7; y <= 15; y += 3)
                g.DrawLine(pen, 7, y, 13, y);
        }

        // Office-style: page with a colored letter chip in the lower left.
        void OfficePage(Graphics g, Color color, string letter)
        {
            Page(g);
            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, 2, 9, 10, 9);
            using var font = new Font("Segoe UI", 5.8f, FontStyle.Bold);
            var size = g.MeasureString(letter, font);
            g.DrawString(letter, font, Brushes.White, 2 + (10 - size.Width) / 2, 9 + (9 - size.Height) / 2);
        }

        Add("site", g =>
        {
            using var brush = new SolidBrush(Color.FromArgb(0x1F, 0x4E, 0x79));
            g.FillRectangle(brush, 2, 2, 16, 16);
            using var pen = new Pen(Color.White, 1.5f);
            g.DrawLine(pen, 3, 7, 17, 7);    // header band
            g.DrawLine(pen, 10, 7, 10, 17);  // two web-part tiles
        });
        Add("library", g =>
        {
            // Two stacked sheets in the brand teal.
            using var back = new SolidBrush(Color.FromArgb(0x10, 0x6E, 0x64));
            using var front = new SolidBrush(Color.FromArgb(0x17, 0x9C, 0x8E));
            g.FillRectangle(back, 6, 2, 11, 13);
            g.FillRectangle(front, 3, 5, 11, 13);
            using var pen = new Pen(Color.White, 1.2f);
            g.DrawLine(pen, 5, 9, 12, 9);
            g.DrawLine(pen, 5, 12, 12, 12);
            g.DrawLine(pen, 5, 15, 12, 15);
        });
        Add("list", g =>
        {
            using var brush = new SolidBrush(Color.FromArgb(0x6f, 0x42, 0xc1));
            g.FillRectangle(brush, 2, 2, 16, 16);
            using var pen = new Pen(Color.White, 1.4f);
            foreach (var y in new[] { 6, 10, 14 })
            {
                g.FillEllipse(Brushes.White, 5, y - 1, 2, 2);
                g.DrawLine(pen, 9, y, 15, y);
            }
        });
        Add("folder", g =>
        {
            var dark = Color.FromArgb(0xD4, 0xA0, 0x17);
            using var tab = new SolidBrush(dark);
            g.FillRectangle(tab, 2, 4, 7, 4);
            using var body = new SolidBrush(Color.FromArgb(0xF6, 0xC2, 0x44));
            g.FillRectangle(body, 2, 6, 16, 10);
            using var pen = new Pen(dark);
            g.DrawRectangle(pen, 2, 6, 16, 10);
        });
        Add("docx", g => OfficePage(g, Color.FromArgb(0x2B, 0x57, 0x9A), "W"));
        Add("xlsx", g => OfficePage(g, Color.FromArgb(0x21, 0x73, 0x46), "X"));
        Add("pptx", g => OfficePage(g, Color.FromArgb(0xC2, 0x4F, 0x1C), "P"));
        Add("pdf", g =>
        {
            // The classic red-label document.
            Page(g);
            using var brush = new SolidBrush(Color.FromArgb(0xC4, 0x2B, 0x1F));
            g.FillRectangle(brush, 2, 9, 14, 7);
            using var font = new Font("Segoe UI", 4.8f, FontStyle.Bold);
            var size = g.MeasureString("PDF", font);
            g.DrawString("PDF", font, Brushes.White, 2 + (14 - size.Width) / 2, 9 + (7 - size.Height) / 2);
        });
        Add("image", g =>
        {
            // Page with a tiny photo: sky, hills, sun.
            Page(g);
            using var sky = new SolidBrush(Color.FromArgb(0xD6, 0xEA, 0xF8));
            g.FillRectangle(sky, 6, 7, 9, 8);
            using var hill = new SolidBrush(Color.FromArgb(0x3E, 0x8E, 0x4E));
            g.FillPolygon(hill, new[] { new Point(6, 15), new Point(9, 10), new Point(12, 15) });
            g.FillPolygon(hill, new[] { new Point(10, 15), new Point(13, 12), new Point(15, 15) });
            using var sun = new SolidBrush(Color.FromArgb(0xF2, 0xA9, 0x1E));
            g.FillEllipse(sun, 12f, 7.8f, 2.6f, 2.6f);
            using var frame = new Pen(pageBorder);
            g.DrawRectangle(frame, 6, 7, 9, 8);
        });
        Add("txt", g =>
        {
            Page(g);
            TextLines(g, Color.FromArgb(0x5A, 0x64, 0x6E));
        });
        Add("file", g =>
        {
            Page(g);
            TextLines(g, Color.FromArgb(0xC2, 0xC9, 0xD0));
        });
        return list;
    }

    public static string KeyFor(string fileName, bool isFolder)
    {
        if (isFolder) return "folder";
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".docx" or ".doc" => "docx",
            ".xlsx" or ".xls" or ".csv" => "xlsx",
            ".pptx" or ".ppt" => "pptx",
            ".pdf" => "pdf",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".svg" => "image",
            ".txt" or ".log" or ".md" => "txt",
            _ => "file",
        };
    }
}
