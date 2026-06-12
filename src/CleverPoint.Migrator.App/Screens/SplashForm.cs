using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using CleverPoint.Migrator.App.Theme;

namespace CleverPoint.Migrator.App.Screens;

/// <summary>
/// Circular, borderless splash with the logo mark. Rendered as a per-pixel
/// alpha layered window: edges are truly smooth, with no transparency-key
/// fringe and no jagged region clipping.
/// </summary>
public class SplashForm : Form
{
    private const int WsExLayered = 0x80000;

    public SplashForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(220, 220);
        ShowInTaskbar = false;
        TopMost = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExLayered;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Render();
    }

    private void Render()
    {
        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new LinearGradientBrush(new Rectangle(0, 0, Width, Height), Brand.Primary, Brand.PrimaryDark, 60f);
            g.FillEllipse(bg, 1, 1, Width - 2, Height - 2);

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
        Push(bmp);
    }

    /// <summary>Hands the ARGB bitmap to the window manager (UpdateLayeredWindow).</summary>
    private void Push(Bitmap bmp)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        var oldBitmap = SelectObject(memDc, hBitmap);
        try
        {
            var size = new SIZE(bmp.Width, bmp.Height);
            var source = new POINT(0, 0);
            var topPos = new POINT(Left, Top);
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref topPos, ref size, memDc, ref source, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(memDc, oldBitmap);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int Cx, Cy; public SIZE(int cx, int cy) { Cx = cx; Cy = cy; } }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
}
