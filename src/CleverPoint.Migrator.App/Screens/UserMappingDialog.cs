using CleverPoint.Migrator.App.Services;
using CleverPoint.Migrator.App.Theme;
using CleverPoint.Migrator.Core.Operations;

namespace CleverPoint.Migrator.App.Screens;

/// <summary>
/// Identity mapping for one migration task: one row per mapping, searchable
/// user/group pickers fed from the connected sites (no Entra access needed),
/// the built-in System Account, an unresolved-user fallback, and CSV
/// import/export including a sample file.
/// </summary>
public class UserMappingDialog : Form
{
    public const string SystemAccountLogin = @"SHAREPOINT\system";
    private const string SystemAccountDisplay = @"System Account (SHAREPOINT\system)";

    public List<(string Type, string Source, string Target)> Rows { get; } = new();
    public string? UnresolvedFallback { get; private set; }

    private readonly AppSettings _settings;
    private readonly string _sourceSite;
    private readonly string _targetSite;
    private readonly ComboBox _type = new() { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _source = new() { Width = 250, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
    private readonly ComboBox _target = new() { Width = 250, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
    private readonly ComboBox _fallback = new() { Width = 280 };
    private readonly ListView _grid = new();
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 22, ForeColor = Brand.TextSecondary, Padding = new Padding(12, 4, 0, 0) };

    private List<string> _sourceUsers = new(), _targetUsers = new();
    private List<string> _sourceGroups = new(), _targetGroups = new();

    public UserMappingDialog(AppSettings settings, string sourceSite, string targetSite,
        List<(string Type, string Source, string Target)> existing, string? fallback)
    {
        _settings = settings;
        _sourceSite = sourceSite;
        _targetSite = targetSite;
        Text = "User and group mapping";
        ClientSize = new Size(820, 540);
        MinimumSize = new Size(760, 460);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Brand.Surface;
        Font = Brand.Body;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;

        // --- Add-a-mapping row -------------------------------------------------
        var addBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 10, 12, 4) };
        _type.Items.AddRange(new object[] { "User", "Group" });
        _type.SelectedIndex = 0;
        _type.SelectedIndexChanged += (_, _) => FillPickers();
        var add = new Button { Text = "Add mapping", AutoSize = true, Padding = new Padding(12, 3, 12, 3), FlatStyle = FlatStyle.Flat, BackColor = Brand.Accent, ForeColor = Color.White, Margin = new Padding(10, 0, 0, 0) };
        add.FlatAppearance.BorderSize = 0;
        add.Click += (_, _) => AddRow();
        addBar.Controls.AddRange(new Control[]
        {
            InlineLabel("Type"), _type,
            InlineLabel("Source (missing at target)"), _source,
            InlineLabel("Target"), _target,
            add,
        });
        Controls.Add(addBar);

        // --- The mappings, one per row ----------------------------------------
        _grid.View = View.Details;
        _grid.FullRowSelect = true;
        _grid.Dock = DockStyle.Fill;
        _grid.BorderStyle = BorderStyle.None;
        _grid.BackColor = Brand.SurfaceAlt;
        _grid.Columns.Add("Type", 80);
        _grid.Columns.Add("Source", 320);
        _grid.Columns.Add("Target", 320);
        foreach (var row in existing)
            _grid.Items.Add(new ListViewItem(new[] { row.Type, row.Source, row.Target }));
        Controls.Add(_grid);
        _grid.BringToFront();

        // --- Unresolved fallback + file actions + OK/Cancel --------------------
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12, 6, 12, 10), FlowDirection = FlowDirection.LeftToRight };
        _fallback.Items.AddRange(new object[] { "Keep the signing-in account (default)", SystemAccountDisplay });
        _fallback.Text = fallback == null ? (string)_fallback.Items[0]
            : fallback.Equals(SystemAccountLogin, StringComparison.OrdinalIgnoreCase) ? SystemAccountDisplay : fallback;
        var remove = SubtleButton("Remove selected");
        remove.Click += (_, _) => { foreach (ListViewItem item in _grid.SelectedItems) _grid.Items.Remove(item); };
        var import = SubtleButton("Import CSV...");
        import.Click += (_, _) => ImportCsv();
        var export = SubtleButton("Export CSV...");
        export.Click += (_, _) => ExportCsv();
        var sample = SubtleButton("Save sample CSV...");
        sample.Click += (_, _) => SaveSample();
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, Padding = new Padding(20, 5, 20, 5), FlatStyle = FlatStyle.Flat, BackColor = Brand.Accent, ForeColor = Color.White, Margin = new Padding(20, 0, 0, 0) };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = SubtleButton("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        bottom.Controls.AddRange(new Control[]
        {
            InlineLabel("When a user is not found at the target:"), _fallback,
            remove, import, export, sample, ok, cancel,
        });
        Controls.Add(bottom);
        Controls.Add(_status);
        AcceptButton = ok;
        CancelButton = cancel;

        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK) return;
            Rows.Clear();
            foreach (ListViewItem item in _grid.Items)
                Rows.Add((item.SubItems[0].Text, item.SubItems[1].Text, item.SubItems[2].Text));
            var fb = _fallback.Text.Trim();
            UnresolvedFallback = fb.Length == 0 || fb.StartsWith("Keep the signing-in") ? null
                : fb == SystemAccountDisplay ? SystemAccountLogin : fb;
        };

        Shown += async (_, _) => await LoadDirectoriesAsync();
    }

    private static Label InlineLabel(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Brand.TextPrimary, Padding = new Padding(8, 6, 4, 0),
    };

    private static Button SubtleButton(string text) => new()
    {
        Text = text, AutoSize = true, Padding = new Padding(10, 4, 10, 4),
        FlatStyle = FlatStyle.Flat, Margin = new Padding(8, 0, 0, 0),
    };

    /// <summary>Site users and groups from BOTH sides feed the searchable pickers.</summary>
    private async Task LoadDirectoriesAsync()
    {
        _status.Text = "Loading users and groups from both sites...";
        try
        {
            var sourceConn = ConnectionResolver.Resolve(this, _settings, _sourceSite);
            var targetConn = ConnectionResolver.Resolve(this, _settings, _targetSite);
            (_sourceUsers, _sourceGroups) = await FetchAsync(sourceConn);
            (_targetUsers, _targetGroups) = await FetchAsync(targetConn);
            FillPickers();
            _status.Text = $"Pickers loaded: {_sourceUsers.Count} source users, {_targetUsers.Count} target users "
                + $"({_sourceGroups.Count}/{_targetGroups.Count} groups). Type to search, or enter any UPN/group name.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not load site users ({ex.Message}). You can still type names and UPNs manually.";
        }
    }

    private static async Task<(List<string> Users, List<string> Groups)> FetchAsync(Core.Csom.SpConnection conn)
    {
        var users = new List<string>();
        using (var doc = await conn.Rest.GetJsonAsync(
            $"{conn.SiteUrl}/_api/web/siteusers?$select=Title,Email,LoginName,PrincipalType&$top=500"))
        {
            foreach (var u in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                if (u.GetProperty("PrincipalType").GetInt32() != 1) continue; // people only
                var email = u.GetProperty("Email").GetString();
                var title = u.GetProperty("Title").GetString();
                if (string.IsNullOrWhiteSpace(email)) continue;
                users.Add($"{title} ({email})");
            }
        }
        var groups = new List<string>();
        using (var doc = await conn.Rest.GetJsonAsync($"{conn.SiteUrl}/_api/web/sitegroups?$select=Title&$top=500"))
        {
            foreach (var g in doc.RootElement.GetProperty("value").EnumerateArray())
                groups.Add(g.GetProperty("Title").GetString() ?? "");
        }
        return (users.OrderBy(u => u).ToList(), groups.Where(g => g.Length > 0).OrderBy(g => g).ToList());
    }

    private void FillPickers()
    {
        var isGroup = _type.SelectedIndex == 1;
        _source.Items.Clear();
        _source.Items.AddRange((isGroup ? _sourceGroups : _sourceUsers).Cast<object>().ToArray());
        _target.Items.Clear();
        if (!isGroup) _target.Items.Add(SystemAccountDisplay);
        _target.Items.AddRange((isGroup ? _targetGroups : _targetUsers).Cast<object>().ToArray());
    }

    private void AddRow()
    {
        var source = ExtractIdentity(_source.Text);
        var target = _target.Text == SystemAccountDisplay ? SystemAccountLogin : ExtractIdentity(_target.Text);
        if (source.Length == 0 || target.Length == 0)
        {
            _status.Text = "Pick or type both a source and a target first.";
            return;
        }
        _grid.Items.Add(new ListViewItem(new[] { (string)_type.SelectedItem!, source, target }));
        _source.Text = "";
        _target.Text = "";
    }

    /// <summary>"Display Name (email)" -> email; anything else passes through.</summary>
    private static string ExtractIdentity(string text)
    {
        text = text.Trim();
        var open = text.LastIndexOf('(');
        return open > 0 && text.EndsWith(')') ? text[(open + 1)..^1].Trim() : text;
    }

    private void ImportCsv()
    {
        using var dialog = new OpenFileDialog { Filter = "CSV files|*.csv|All files|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var (users, groups) = UserMappingStore.LoadCsv(dialog.FileName);
            foreach (var (s, t) in users) _grid.Items.Add(new ListViewItem(new[] { "User", s, t }));
            foreach (var (s, t) in groups) _grid.Items.Add(new ListViewItem(new[] { "Group", s, t }));
            _status.Text = $"Imported {users.Count} user and {groups.Count} group mappings.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"That file could not be read as a mapping CSV: {ex.Message}",
                "Import", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ExportCsv()
    {
        using var dialog = new SaveFileDialog { Filter = "CSV files|*.csv", FileName = "user-mapping.csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        UserMappingStore.SaveCsv(dialog.FileName, _grid.Items.Cast<ListViewItem>()
            .Select(i => (i.SubItems[0].Text, i.SubItems[1].Text, i.SubItems[2].Text)));
        MigrationWizard.OfferToOpen(this, dialog.FileName);
    }

    private void SaveSample()
    {
        using var dialog = new SaveFileDialog { Filter = "CSV files|*.csv", FileName = "user-mapping-sample.csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        UserMappingStore.SaveCsv(dialog.FileName, new[]
        {
            ("User", "first.last@oldtenant.com", "first.last@newtenant.com"),
            ("User", "departed.user@oldtenant.com", SystemAccountLogin),
            ("Group", "Old Site Members", "New Site Members"),
        });
        MigrationWizard.OfferToOpen(this, dialog.FileName);
    }
}
