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
        _source.CopyRequested += OnDropMigration;
        split.Panel1.Controls.Add(_source);
        split.Panel2.Controls.Add(_target);
        Controls.Add(split);
        Load += (_, _) => split.SplitterDistance = Width / 2;
    }

    private void OnDropMigration(List<SpFolderEntry> items)
    {
        // The source side needs at least an open list (or a selected one);
        // the target only needs a connected SITE - copying to a site with a
        // new title creates a brand-new list there.
        var sourceList = _source.CurrentList ?? _source.SelectedList;
        if (_source.Connection == null || _target.Connection == null || sourceList == null)
        {
            MessageBox.Show(FindForm(),
                "Open (or select) a list or library on the LEFT and connect a site on the RIGHT, then copy.",
                "Almost there", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Selected files become name filters; one selected folder becomes the scope.
        var folders = items.Where(i => i.IsFolder).ToList();
        var files = items.Where(i => !i.IsFolder).ToList();
        if (folders.Count > 1)
        {
            MessageBox.Show(FindForm(),
                "Copy one folder at a time (or the whole list). Multiple-folder selections aren't supported yet.",
                "One folder at a time", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var folderScope = folders.Count == 1 ? folders[0].ServerRelativeUrl
            : files.Count > 0 ? _source.CurrentFolder : null;
        var patterns = files.Select(f => f.Name).ToList();

        // New list on the SAME site gets " - Copy" so it never collides with the source.
        var sameSite = string.Equals(_source.Connection.SiteUrl.TrimEnd('/'), _target.Connection.SiteUrl.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
        var targetName = _target.CurrentList?.Title ?? (sameSite ? $"{sourceList.Title} - Copy" : sourceList.Title);
        var what = items.Count == 0 ? $"everything in '{sourceList.Title}'"
            : folders.Count == 1 ? $"folder '{folders[0].Name}'"
            : $"{files.Count} selected file(s)";
        var destination = _target.CurrentList != null
            ? $"'{_target.CurrentList.Title}'"
            : $"a new list '{targetName}' on {_target.Connection.SiteUrl}";
        if (MessageBox.Show(FindForm(), $"Copy {what} to {destination}?\n\nMetadata, structure and attachments are preserved.",
                "Start migration", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        using var wizard = new MigrationWizard(_settings);
        wizard.Preset(_source.Connection.SiteUrl, sourceList.Title,
            _target.Connection.SiteUrl, targetName, folderScope, patterns);
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

    /// <summary>The folder currently open in this pane (null at site level).</summary>
    public string? CurrentFolder => _currentFolder;

    /// <summary>A list/library ROW selected in the site view (whole-list copy without drilling in).</summary>
    public SpListInfo? SelectedList =>
        _items.SelectedItems.Count > 0 ? _items.SelectedItems[0].Tag as SpListInfo : null;

    public event Action<List<SpFolderEntry>>? DropReceived;
    public event Action<List<SpFolderEntry>>? CopyRequested;

    private const string AddConnectionEntry = "<  Add a new connection...  >";

    private void RefreshSiteChoices()
    {
        _siteUrl.Items.Clear();
        foreach (var c in _settings.Connections)
            _siteUrl.Items.Add(c.SiteUrl);
        _siteUrl.Items.Add(AddConnectionEntry);
    }

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

        // Searchable: type to filter; pick a saved connection or add one.
        _siteUrl.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _siteUrl.AutoCompleteSource = AutoCompleteSource.ListItems;
        RefreshSiteChoices();
        _siteUrl.SelectedIndexChanged += async (_, _) =>
        {
            if (_siteUrl.SelectedItem?.ToString() == AddConnectionEntry)
            {
                using var editor = new ConnectionEditor(_settings);
                var added = editor.ShowDialog(FindForm()) == DialogResult.OK;
                RefreshSiteChoices();
                _siteUrl.Text = added ? _settings.Connections[^1].SiteUrl : "";
                if (added) await OpenSiteAsync();
            }
            else
            {
                await OpenSiteAsync();
            }
        };

        _items.Dock = DockStyle.Fill;
        _items.View = View.Details;
        _items.FullRowSelect = true;
        _items.MultiSelect = !isTarget;
        _items.BorderStyle = BorderStyle.None;
        _items.SmallImageList = FileTypeIcons.Shared;
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

            // Keyboard/mouse-only alternative to drag and drop.
            var copy = new Button
            {
                Text = "Copy to target  >",
                AutoSize = true,
                Padding = new Padding(12, 4, 12, 4),
                FlatStyle = FlatStyle.Flat,
                BackColor = Brand.Accent,
                ForeColor = Color.White,
                Dock = DockStyle.Bottom,
            };
            copy.FlatAppearance.BorderSize = 0;
            copy.Click += (_, _) => CopyRequested?.Invoke(
                _items.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<SpFolderEntry>().ToList());
            Controls.Add(copy);
            copy.BringToFront();
            _status.BringToFront();
            _items.BringToFront();
        }
    }

    private async Task OpenSiteAsync()
    {
        var url = _siteUrl.Text.Trim().TrimEnd('/');
        if (url.Length == 0) return;
        try
        {
            _status.Text = "Connecting...";
            // Saved app+cert connects silently; browser mode pops the sign-in.
            Connection = ConnectionResolver.Resolve(FindForm()!, _settings, url);
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
            var row = new ListViewItem(new[] { web.Title, "Subsite" }, "site") { Tag = web };
            row.ForeColor = Brand.Primary;
            _items.Items.Add(row);
        }
        foreach (var list in lists)
        {
            var row = new ListViewItem(new[] { list.Title, $"{(list.IsLibrary ? "Library" : "List")}, {list.ItemCount} items" },
                list.IsLibrary ? "library" : "list") { Tag = list };
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
            }, FileTypeIcons.KeyFor(entry.Name, entry.IsFolder)) { Tag = entry };
            _items.Items.Add(row);
        }
        _status.Text = $"{CurrentList?.Title}: {entries.Count(e => e.IsFolder)} folders, {entries.Count(e => !e.IsFolder)} files"
            + (_isTarget ? "  (drop items here to migrate)" : "  (drag items to the target pane)");
    }
}
