using CleverPoint.Migrator.App.Services;
using CleverPoint.Migrator.App.Theme;

namespace CleverPoint.Migrator.App.Screens;

/// <summary>
/// Shell window: brand header, slim left navigation (Home / History /
/// Settings), content host, system tray integration. Designed to fit
/// 1366x768 laptops without clipping.
/// </summary>
public class MainForm : Form
{
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Brand.Surface };
    private readonly NotifyIcon _tray = new();
    private readonly AppSettings _settings = AppSettings.Load();

    public MainForm()
    {
        Text = "CleverPoint Migrator";
        MinimumSize = new Size(1100, 680);
        Size = new Size(1200, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Brand.Surface;
        Font = Brand.Body;
        Icon = AppIcon.Create();

        Controls.Add(_content);
        Controls.Add(BuildNav());
        Controls.Add(BuildHeader());
        SetupTray();

        ShowScreen(new HomeScreen(_settings, ShowScreen));
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Brand.Primary };
        header.Controls.Add(new Label
        {
            Text = "CleverPoint Migrator",
            ForeColor = Color.White,
            Font = Brand.Title,
            AutoSize = true,
            Location = new Point(18, 12),
        });
        var version = new Label
        {
            Text = $"v{Application.ProductVersion.Split('+')[0]}",
            ForeColor = Color.FromArgb(180, 255, 255, 255),
            Font = Brand.Small,
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        header.Controls.Add(version);
        header.Resize += (_, _) => version.Location = new Point(header.Width - version.Width - 16, 22);
        return header;
    }

    private Control BuildNav()
    {
        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            Width = 168,
            BackColor = Brand.SurfaceAlt,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(8, 16, 8, 8),
        };
        nav.Controls.Add(NavButton("Home", () => ShowScreen(new HomeScreen(_settings, ShowScreen))));
        nav.Controls.Add(NavButton("History", () => ShowScreen(new HistoryScreen(_settings, ShowScreen))));
        nav.Controls.Add(NavButton("Settings", () => ShowScreen(new SettingsScreen(_settings))));
        return nav;
    }

    private static Button NavButton(string text, Action onClick)
    {
        var button = new Button
        {
            Text = text,
            Width = 148,
            Height = 40,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Brand.TextPrimary,
            BackColor = Brand.SurfaceAlt,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            Font = Brand.Heading,
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Brand.Surface;
        button.Click += (_, _) => onClick();
        return button;
    }

    public void ShowScreen(Control screen)
    {
        _content.SuspendLayout();
        foreach (Control old in _content.Controls) old.Dispose();
        _content.Controls.Clear();
        screen.Dock = DockStyle.Fill;
        _content.Controls.Add(screen);
        _content.ResumeLayout();
    }

    private void SetupTray()
    {
        _tray.Icon = Icon;
        _tray.Text = "CleverPoint Migrator";
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("Settings", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; ShowScreen(new SettingsScreen(_settings)); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _tray.Visible = false; Application.Exit(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        _tray.Visible = true;

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized && _settings.MinimizeToTray)
                Hide();
        };
        FormClosed += (_, _) => _tray.Visible = false;
    }
}

/// <summary>Programmatic app icon: two panels + arc arrow on a brand-blue disc.</summary>
public static class AppIcon
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Brand.Primary);
            g.FillEllipse(bg, 0, 0, 63, 63);
            using var panel = new SolidBrush(Color.White);
            g.FillRectangle(panel, 13, 26, 12, 18);
            g.FillRectangle(panel, 39, 26, 12, 18);
            using var arc = new Pen(Brand.Accent, 3.5f) { EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor };
            g.DrawArc(arc, 16, 12, 32, 22, 200, 140);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
