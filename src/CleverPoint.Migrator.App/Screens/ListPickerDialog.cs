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
        var list = new ImageList { ImageSize = new Size(20, 20), ColorDepth = ColorDepth.Depth32Bit };
        void Add(string key, Color color, string glyph)
        {
            var bmp = new Bitmap(20, 20);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, 2, 2, 16, 16);
            using var font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            var size = g.MeasureString(glyph, font);
            g.DrawString(glyph, font, Brushes.White, (20 - size.Width) / 2, (20 - size.Height) / 2);
            list.Images.Add(key, bmp);
        }
        Add("site", Color.FromArgb(0x1F, 0x4E, 0x79), "S");
        Add("library", Color.FromArgb(0x17, 0x9C, 0x8E), "L");
        Add("list", Color.FromArgb(0x6f, 0x42, 0xc1), "=");
        Add("folder", Color.FromArgb(0xD4, 0xA0, 0x17), "F");
        Add("docx", Color.FromArgb(0x2B, 0x57, 0x9A), "W");
        Add("xlsx", Color.FromArgb(0x21, 0x73, 0x46), "X");
        Add("pptx", Color.FromArgb(0xC2, 0x4F, 0x1C), "P");
        Add("pdf", Color.FromArgb(0xB0, 0x2A, 0x37), "A");
        Add("image", Color.FromArgb(0x88, 0x44, 0xAA), "i");
        Add("txt", Color.FromArgb(0x5A, 0x64, 0x6E), "t");
        Add("file", Color.FromArgb(0x8A, 0x94, 0x9E), "·");
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
