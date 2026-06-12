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

        // One action bar UNDER both panes, with breathing room.
        var copy = new Button
        {
            Text = "Copy to target  >",
            AutoSize = true,
            Padding = new Padding(32, 10, 32, 10),
            FlatStyle = FlatStyle.Flat,
            BackColor = Brand.Accent,
            ForeColor = Color.White,
            Font = Brand.Heading,
            Cursor = Cursors.Hand,
        };
        copy.FlatAppearance.BorderSize = 0;
        copy.Click += (_, _) => OnDropMigration(_source.CurrentSelection());
        var actionBar = new Panel { Dock = DockStyle.Bottom, Height = 76, BackColor = Brand.Surface };
        actionBar.Controls.Add(copy);
        actionBar.Resize += (_, _) => copy.Location = new Point((actionBar.Width - copy.Width) / 2, (actionBar.Height - copy.Height) / 2);

        Controls.Add(split);
        Controls.Add(actionBar);
        split.BringToFront();
        Load += (_, _) =>
        {
            split.SplitterDistance = Width / 2;
            copy.Location = new Point((actionBar.Width - copy.Width) / 2, (actionBar.Height - copy.Height) / 2);
        };
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

        // Surgical selection: any mix of files and folders becomes an exact
        // path list (folders bring their whole subtree); selected LIST ITEMS
        // become an ID filter. Nothing selected = the whole list.
        var listItems = items.Where(i => !i.IsFolder && i.ItemId > 0).ToList();
        var pathEntries = items.Where(i => i.ServerRelativeUrl.Length > 0).ToList();
        var selectedPaths = pathEntries.Select(e => e.ServerRelativeUrl).ToList();
        var itemIds = listItems.Select(i => i.ItemId).ToList();
        // Scanning can start at the open folder - all selections live under it.
        var folderScope = selectedPaths.Count > 0 ? _source.CurrentFolder : null;

        // New list on the SAME site gets " - Copy" so it never collides with the source.
        var sameSite = string.Equals(_source.Connection.SiteUrl.TrimEnd('/'), _target.Connection.SiteUrl.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
        var targetName = _target.CurrentList?.Title ?? (sameSite ? $"{sourceList.Title} - Copy" : sourceList.Title);

        // No confirmation popup here: the wizard shows (and lets the user
        // change) every detail, and nothing runs until a copy button is clicked.
        using var wizard = new MigrationWizard(_settings);
        wizard.Preset(_source.Connection.SiteUrl, sourceList.Title,
            _target.Connection.SiteUrl, targetName, folderScope, selectedPaths, itemIds);
        // An existing target list was opened: this is clearly a content copy.
        if (_target.CurrentList != null)
            wizard.UseContentOnly();
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

        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 64, ColumnCount = 3 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = role, Font = Brand.Heading, ForeColor = Brand.Primary, AutoSize = true }, 0, 0);
        header.Controls.Add(_siteUrl, 0, 1);
        header.Controls.Add(_connect, 1, 1);
        var refresh = new Button { Text = "Refresh", AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(6, 0, 0, 0) };
        refresh.Click += async (_, _) => await RefreshAsync();
        header.Controls.Add(refresh, 2, 1);
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
        }
    }

    /// <summary>The current multi-selection (files, folders, list items) for the copy action.</summary>
    public List<SpFolderEntry> CurrentSelection() =>
        _items.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<SpFolderEntry>().ToList();

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

    /// <summary>Reloads whatever is on screen, bypassing the cache.</summary>
    private async Task RefreshAsync()
    {
        if (Connection == null) return;
        try
        {
            if (CurrentList == null) await ShowSiteAsync(fresh: true);
            else if (_currentFolder != null) await ShowFolderAsync(_currentFolder);
            else await ShowListItemsAsync(CurrentList);
        }
        catch (Exception ex)
        {
            _status.Text = $"Refresh failed: {Short(ex.Message)}";
        }
    }

    private static string Short(string message) =>
        message.Length > 160 ? message[..160] + "..." : message;

    private async Task ShowSiteAsync(bool fresh = false)
    {
        if (Connection == null) return;
        _status.Text = "Loading site...";
        var lists = await Task.Run(() => _browser.GetListsAsync(Connection, useCache: !fresh));
        var webs = await Task.Run(() => _browser.GetSubwebsAsync(Connection, useCache: !fresh));
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
        try
        {
            await DrillCoreAsync();
        }
        catch (Exception ex)
        {
            // Browsing problems (throttling, permissions, deleted items) land
            // in the status bar - never in a crash dialog.
            _status.Text = $"Could not open: {Short(ex.Message)}";
        }
    }

    private async Task DrillCoreAsync()
    {
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
                // Libraries browse as folders/files; generic lists show their items.
                if (list.IsLibrary) await ShowFolderAsync(list.ServerRelativeUrl);
                else await ShowListItemsAsync(list);
                break;
            case SpFolderEntry { IsFolder: true } folder:
                await ShowFolderAsync(folder.ServerRelativeUrl);
                break;
        }
    }

    private async Task ShowListItemsAsync(SpListInfo list)
    {
        if (Connection == null) return;
        _status.Text = "Loading items...";
        _currentFolder = null;
        var entries = await Task.Run(() => _browser.GetListItemsAsync(Connection, list));
        _items.Items.Clear();
        _items.Items.Add(new ListViewItem(new[] { "..", "Back to site" }));
        foreach (var entry in entries)
            _items.Items.Add(new ListViewItem(new[] { entry.Name, $"Item #{entry.ItemId}" }, "list") { Tag = entry });
        var shown = entries.Count >= 500 ? "first 500 items" : $"{entries.Count} items";
        _status.Text = $"{list.Title}: {shown}"
            + (_isTarget ? "" : "  (select items to copy just those, or copy with nothing selected for all)");
    }

    private async Task ShowFolderAsync(string folderUrl)
    {
        if (Connection == null) return;
        _status.Text = "Loading folder...";
        _currentFolder = folderUrl;
        var entries = await Task.Run(() => _browser.GetFolderAsync(Connection, folderUrl, CurrentList?.ServerRelativeUrl));
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
            + (_isTarget ? "  (drop items here to migrate)"
                : "  (Ctrl+click any mix of files and folders to copy just those; nothing selected = everything)");
    }
}
