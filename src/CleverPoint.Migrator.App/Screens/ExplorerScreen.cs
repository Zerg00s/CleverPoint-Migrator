using CleverPoint.Migrator.App.Services;
using CleverPoint.Migrator.App.Theme;
using CleverPoint.Migrator.Core.Csom;

namespace CleverPoint.Migrator.App.Screens;

/// <summary>
/// The two-pane explorer: source on the left, target on the right. Drill
/// from site to subsites, lists/libraries and folders; multi-select on the
/// left and drag onto the right pane (or use the Copy button) to start a
/// migration. Async loading keeps the UI responsive.
/// </summary>
public class ExplorerScreen : UserControl
{
    private readonly AppSettings _settings;
    private readonly SiteBrowser _browser;
    private readonly ExplorerPane _source;
    private readonly ExplorerPane _target;

    public ExplorerScreen(AppSettings settings)
    {
        _settings = settings;
        _browser = new SiteBrowser(settings.CacheMinutes);
        BackColor = Brand.Surface;

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 8,
            BackColor = Brand.Surface,
        };
        _source = new ExplorerPane("Source", settings, _browser, isTarget: false);
        _target = new ExplorerPane("Target", settings, _browser, isTarget: true);
        _target.DropReceived += OnDropMigration;
        split.Panel1.Controls.Add(_source);
        split.Panel2.Controls.Add(_target);
        Controls.Add(split);
        Load += (_, _) => split.SplitterDistance = Width / 2;
    }

    private void OnDropMigration(List<SpFolderEntry> items)
    {
        if (_source.Connection == null || _target.Connection == null || _source.CurrentList == null || _target.CurrentList == null)
        {
            MessageBox.Show(FindForm(), "Open a list or library on both sides first, then drag items across.",
                "Almost there", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var summary = items.Count == 0
            ? $"Copy everything in '{_source.CurrentList.Title}' to '{_target.CurrentList.Title}'?"
            : $"Copy {items.Count} selected item(s) from '{_source.CurrentList.Title}' to '{_target.CurrentList.Title}'?";
        if (MessageBox.Show(FindForm(), summary + "\n\nMetadata, structure and attachments are preserved.",
                "Start migration", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        using var wizard = new MigrationWizard(_settings);
        wizard.Preset(_source.Connection.SiteUrl, _source.CurrentList.Title,
            _target.Connection.SiteUrl, _target.CurrentList.Title,
            items.Where(i => i.IsFolder).Select(i => i.ServerRelativeUrl).FirstOrDefault());
        wizard.ShowDialog(FindForm());
    }
}

/// <summary>One pane: site URL bar, breadcrumb drill-down list, item counts.</summary>
public class ExplorerPane : UserControl
{
    private readonly string _role;
    private readonly AppSettings _settings;
    private readonly SiteBrowser _browser;
    private readonly bool _isTarget;
    private readonly ComboBox _siteUrl = new() { Dock = DockStyle.Fill };
    private readonly Button _connect = new() { Text = "Open", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Brand.Accent, ForeColor = Color.White };
    private readonly ListView _items = new();
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, ForeColor = Brand.TextSecondary, Padding = new Padding(8, 4, 0, 0) };
    private string? _currentFolder;

    public SpConnection? Connection { get; private set; }
    public SpListInfo? CurrentList { get; private set; }
    public event Action<List<SpFolderEntry>>? DropReceived;

    public ExplorerPane(string role, AppSettings settings, SiteBrowser browser, bool isTarget)
    {
        _role = role;
        _settings = settings;
        _browser = browser;
        _isTarget = isTarget;
        BackColor = Brand.SurfaceAlt;
        Dock = DockStyle.Fill;
        Padding = new Padding(8);

        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 64, ColumnCount = 2 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = role, Font = Brand.Heading, ForeColor = Brand.Primary, AutoSize = true }, 0, 0);
        header.Controls.Add(_siteUrl, 0, 1);
        header.Controls.Add(_connect, 1, 1);
        Controls.Add(header);

        foreach (var c in settings.Connections)
            _siteUrl.Items.Add(c.SiteUrl);

        _items.Dock = DockStyle.Fill;
        _items.View = View.Details;
        _items.FullRowSelect = true;
        _items.MultiSelect = !isTarget;
        _items.BorderStyle = BorderStyle.None;
        _items.Columns.Add("Name", 320);
        _items.Columns.Add("Details", 160);
        _items.DoubleClick += async (_, _) => await DrillAsync();
        Controls.Add(_items);
        _items.BringToFront();
        Controls.Add(_status);

        _connect.Click += async (_, _) => await OpenSiteAsync();

        if (isTarget)
        {
            _items.AllowDrop = true;
            _items.DragEnter += (_, e) => e.Effect = DragDropEffects.Copy;
            _items.DragDrop += (_, e) =>
            {
                var payload = e.Data?.GetData(typeof(List<SpFolderEntry>)) as List<SpFolderEntry>;
                DropReceived?.Invoke(payload ?? new List<SpFolderEntry>());
            };
        }
        else
        {
            _items.ItemDrag += (_, e) =>
            {
                var selection = _items.SelectedItems.Cast<ListViewItem>()
                    .Select(i => i.Tag).OfType<SpFolderEntry>().ToList();
                _items.DoDragDrop(selection, DragDropEffects.Copy);
            };
        }
    }

    private async Task OpenSiteAsync()
    {
        var url = _siteUrl.Text.Trim().TrimEnd('/');
        if (url.Length == 0) return;
        try
        {
            _status.Text = "Connecting...";
            var saved = _settings.Connections.FirstOrDefault(c =>
                c.AuthMode == "AppCertificate" && url.StartsWith(new Uri(c.SiteUrl).GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase));
            if (saved == null)
            {
                _status.Text = "No saved connection covers this site (Settings > Connections).";
                return;
            }
            var creds = new Core.Auth.AppCredentials
            {
                TenantId = saved.TenantId,
                AppId = saved.AppId,
                CertPfxPath = saved.CertPfxPath,
                CertPassword = string.IsNullOrEmpty(saved.CertPasswordProtected) ? "" : AppSettings.Unprotect(saved.CertPasswordProtected),
            };
            Connection = new SpConnection(url, new Core.Auth.CertTokenProvider(creds));
            CurrentList = null;
            _currentFolder = null;
            await ShowSiteAsync();
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not connect: {ex.Message}";
        }
    }

    private async Task ShowSiteAsync()
    {
        if (Connection == null) return;
        _status.Text = "Loading site...";
        var lists = await Task.Run(() => _browser.GetListsAsync(Connection));
        var webs = await Task.Run(() => _browser.GetSubwebsAsync(Connection));
        _items.Items.Clear();
        foreach (var web in webs)
        {
            var row = new ListViewItem(new[] { web.Title, "Subsite" }) { Tag = web };
            row.ForeColor = Brand.Primary;
            _items.Items.Add(row);
        }
        foreach (var list in lists)
        {
            var row = new ListViewItem(new[] { list.Title, $"{(list.IsLibrary ? "Library" : "List")}, {list.ItemCount} items" }) { Tag = list };
            _items.Items.Add(row);
        }
        _status.Text = $"{webs.Count} subsites, {lists.Count} lists and libraries";
    }

    private async Task DrillAsync()
    {
        if (Connection == null || _items.SelectedItems.Count == 0) return;
        switch (_items.SelectedItems[0].Tag)
        {
            case null:   // ".." row
                CurrentList = null;
                _currentFolder = null;
                await ShowSiteAsync();
                break;
            case SpWebInfo web:
                Connection = Connection.ForWeb(web.Url);
                _siteUrl.Text = web.Url;
                CurrentList = null;
                await ShowSiteAsync();
                break;
            case SpListInfo list:
                CurrentList = list;
                await ShowFolderAsync(list.ServerRelativeUrl);
                break;
            case SpFolderEntry { IsFolder: true } folder:
                await ShowFolderAsync(folder.ServerRelativeUrl);
                break;
        }
    }

    private async Task ShowFolderAsync(string folderUrl)
    {
        if (Connection == null) return;
        _status.Text = "Loading folder...";
        _currentFolder = folderUrl;
        var entries = await Task.Run(() => _browser.GetFolderAsync(Connection, folderUrl));
        _items.Items.Clear();
        _items.Items.Add(new ListViewItem(new[] { "..", "Back to site" }));
        foreach (var entry in entries)
        {
            var row = new ListViewItem(new[]
            {
                entry.Name,
                entry.IsFolder ? "Folder" : $"{entry.Size / 1024.0:F0} KB",
            }) { Tag = entry };
            _items.Items.Add(row);
        }
        _status.Text = $"{CurrentList?.Title}: {entries.Count(e => e.IsFolder)} folders, {entries.Count(e => !e.IsFolder)} files"
            + (_isTarget ? "  (drop items here to migrate)" : "  (drag items to the target pane)");
    }
}
