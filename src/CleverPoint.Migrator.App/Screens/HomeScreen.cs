using CleverPoint.Migrator.App.Services;
using CleverPoint.Migrator.App.Theme;
using CleverPoint.Migrator.Core.History;

namespace CleverPoint.Migrator.App.Screens;

/// <summary>
/// Office-worker-simple home: one primary action ("New migration"), the
/// saved connections, and recent runs. Nothing else in the way.
/// </summary>
public class HomeScreen : UserControl
{
    public HomeScreen(AppSettings settings, Action<Control> navigate)
    {
        BackColor = Brand.Surface;
        Padding = new Padding(24);

        var newMigration = new Button
        {
            Text = "New migration",
            Width = 200,
            Height = 52,
            BackColor = Brand.Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Brand.Heading,
            Location = new Point(24, 24),
            Cursor = Cursors.Hand,
        };
        newMigration.FlatAppearance.BorderSize = 0;
        newMigration.Click += (_, _) =>
        {
            using var wizard = new MigrationWizard(settings);
            wizard.ShowDialog(FindForm());
        };
        Controls.Add(newMigration);

        var hint = new Label
        {
            Text = "Copy lists, libraries, files and folders between SharePoint sites.\n" +
                   "Pick a source, pick a target, go. Defaults are safe: metadata,\n" +
                   "attachments and structure are preserved.",
            Font = Brand.Body,
            ForeColor = Brand.TextSecondary,
            AutoSize = true,
            Location = new Point(250, 30),
        };
        Controls.Add(hint);

        Controls.Add(new Label
        {
            Text = "Recent migrations",
            Font = Brand.Heading,
            ForeColor = Brand.TextPrimary,
            AutoSize = true,
            Location = new Point(24, 104),
        });

        var recent = new DataGridView
        {
            Location = new Point(24, 132),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            ReadOnly = true,
            AllowUserToAddRows = false,
            RowHeadersVisible = false,
            BackgroundColor = Brand.SurfaceAlt,
            BorderStyle = BorderStyle.None,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        recent.Size = new Size(Width - 48, Height - 160);
        Resize += (_, _) => recent.Size = new Size(Width - 48, Height - 160);
        recent.Columns.Add("name", "Name");
        recent.Columns.Add("when", "Started");
        recent.Columns.Add("status", "Status");
        recent.Columns.Add("counts", "Result");

        try
        {
            using var store = new HistoryStore(AppSettings.HistoryDbPath);
            foreach (var run in store.GetRuns(15))
            {
                var row = recent.Rows[recent.Rows.Add(
                    run.Name, run.StartedUtc.ToLocalTime().ToString("g"), run.Status,
                    $"{run.Copied} copied, {run.Skipped} skipped, {run.Failed} failed")];
                row.Cells[2].Style.ForeColor = run.Failed > 0 ? Brand.Fail : run.Warnings > 0 ? Brand.Warn : Brand.Ok;
            }
        }
        catch { /* empty history is fine on first run */ }

        if (recent.Rows.Count == 0)
        {
            Controls.Add(new Label
            {
                Text = "No migrations yet. Your runs will show up here.",
                ForeColor = Brand.TextSecondary,
                AutoSize = true,
                Location = new Point(28, 140),
            });
        }
        else
        {
            Controls.Add(recent);
        }
    }
}
