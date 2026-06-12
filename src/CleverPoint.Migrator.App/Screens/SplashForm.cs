using System.Drawing.Drawing2D;
using CleverPoint.Migrator.App.Theme;

namespace CleverPoint.Migrator.App.Screens;

/// <summary>
/// Circular, borderless, transparent-edged splash with the logo mark.
/// No minimize/maximize chrome; closes itself when MainForm is ready.
/// </summary>
public class SplashForm : Form
{
    public SplashForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(220, 220);
        BackColor = Color.Magenta;             // transparency key color
        TransparencyKey = Color.Magenta;
        ShowInTaskbar = false;
        TopMost = true;

        // Circular region.
        using var path = new GraphicsPath();
        path.AddEllipse(0, 0, Width, Height);
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var bg = new LinearGradientBrush(ClientRectangle, Brand.Primary, Brand.PrimaryDark, 60f);
        g.FillEllipse(bg, 0, 0, Width - 1, Height - 1);

        // Logo mark: two rounded panels with an arc arrow (source -> target).
        using var panel = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
        g.FillRectangle(panel, 50, 85, 38, 50);
        g.FillRectangle(panel, 132, 85, 38, 50);
        using var arc = new Pen(Brand.Accent, 6) { EndCap = LineCap.ArrowAnchor };
        g.DrawArc(arc, 60, 50, 100, 60, 200, 140);

        using var font = new Font("Segoe UI Semibold", 10.5f);
        var text = "CleverPoint Migrator";
        var size = g.MeasureString(text, font);
        g.DrawString(text, font, Brushes.White, (Width - size.Width) / 2, 152);
    }
}
