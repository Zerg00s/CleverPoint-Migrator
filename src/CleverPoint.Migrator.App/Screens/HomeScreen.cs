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

        // First-run empty state: the one thing to do is add a connection.
        if (settings.Connections.Count == 0)
        {
            BuildWelcomeState(settings, navigate);
            return;
        }

        var newMigration = new Button
        {
            Text = "New migration",
            AutoSize = true,
            Padding = new Padding(28, 14, 28, 14),
            BackColor = Brand.Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Brand.Heading,
            Location = new Point(24, 24),
            Cursor = Cursors.Hand,
        };
        newMigration.FlatAppearance.BorderSize = 0;
        // The split source/target explorer IS the migration experience.
        newMigration.Click += (_, _) => navigate(new ExplorerScreen(settings));
        Controls.Add(newMigration);

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
                // Color by the RUN status first: a run that died before
                // copying anything has 0 item failures but is still red.
                row.Cells[2].Style.ForeColor = run.Failed > 0 || run.Status == "Failed" ? Brand.Fail
                    : run.Warnings > 0 || run.Status is "CompletedWithIssues" or "Interrupted" ? Brand.Warn : Brand.Ok;
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

    /// <summary>Large friendly first-run state: an illustration and one call to action.</summary>
    private void BuildWelcomeState(AppSettings settings, Action<Control> navigate)
    {
        var canvas = new Panel { Dock = DockStyle.Fill };
        canvas.Paint += (_, e) =>
        {
            // Simple hand-drawn-style illustration: two sites and an arc.
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var cx = canvas.Width / 2;
            using var panelBrush = new SolidBrush(Color.FromArgb(40, Brand.Primary));
            using var border = new Pen(Brand.Primary, 2);
            g.FillRectangle(panelBrush, cx - 180, 90, 120, 150);
            g.DrawRectangle(border, cx - 180, 90, 120, 150);
            g.FillRectangle(panelBrush, cx + 60, 90, 120, 150);
            g.DrawRectangle(border, cx + 60, 90, 120, 150);
            using var arc = new Pen(Brand.Accent, 5) { EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor };
            g.DrawArc(arc, cx - 120, 30, 240, 120, 200, 140);
            for (var i = 0; i < 3; i++)
            {
                g.DrawLine(border, cx - 162, 120 + i * 34, cx - 78, 120 + i * 34);
                g.DrawLine(border, cx + 78, 120 + i * 34, cx + 162, 120 + i * 34);
            }
        };

        var welcome = new Label
        {
            Text = "Welcome to CleverPoint Migrator",
            Font = Brand.Title,
            ForeColor = Brand.TextPrimary,
            AutoSize = true,
        };
        var prompt = new Label
        {
            Text = "Connect a SharePoint tenant to start copying lists, libraries and files.",
            Font = Brand.Body,
            ForeColor = Brand.TextSecondary,
            AutoSize = true,
        };
        var addConnection = new Button
        {
            Text = "Add connection",
            AutoSize = true,
            Padding = new Padding(28, 14, 28, 14),
            BackColor = Brand.Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Brand.Heading,
            Cursor = Cursors.Hand,
        };
        addConnection.FlatAppearance.BorderSize = 0;
        addConnection.Click += (_, _) =>
        {
            // Straight into connection creation; land on the normal home once added.
            using var editor = new ConnectionEditor(settings);
            if (editor.ShowDialog(FindForm()) == DialogResult.OK && settings.Connections.Count > 0)
                navigate(new HomeScreen(settings, navigate));
        };

        canvas.Controls.AddRange(new Control[] { welcome, prompt, addConnection });
        canvas.Resize += (_, _) =>
        {
            welcome.Location = new Point((canvas.Width - welcome.Width) / 2, 270);
            prompt.Location = new Point((canvas.Width - prompt.Width) / 2, 310);
            addConnection.Location = new Point((canvas.Width - addConnection.Width) / 2, 348);
        };
        Controls.Add(canvas);
    }
}
