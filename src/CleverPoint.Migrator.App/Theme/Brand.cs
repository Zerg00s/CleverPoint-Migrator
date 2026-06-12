namespace CleverPoint.Migrator.App.Theme;

/// <summary>
/// CleverPoint Migrator brand: deep blue + teal on light surfaces, chosen
/// for WCAG-readable contrast. One place to keep the app from looking like
/// a default gray WinForms box.
/// </summary>
public static class Brand
{
    public static readonly Color Primary = Color.FromArgb(0x1F, 0x4E, 0x79);      // deep blue
    public static readonly Color PrimaryDark = Color.FromArgb(0x16, 0x3A, 0x5C);
    public static readonly Color Accent = Color.FromArgb(0x17, 0x9C, 0x8E);       // teal
    public static readonly Color Surface = Color.FromArgb(0xF7, 0xF9, 0xFB);
    public static readonly Color SurfaceAlt = Color.White;
    public static readonly Color TextPrimary = Color.FromArgb(0x21, 0x25, 0x29);
    public static readonly Color TextSecondary = Color.FromArgb(0x5A, 0x64, 0x6E);
    public static readonly Color Ok = Color.FromArgb(0x1E, 0x7E, 0x34);
    public static readonly Color Warn = Color.FromArgb(0xB8, 0x86, 0x0B);
    public static readonly Color Fail = Color.FromArgb(0xB0, 0x2A, 0x37);
    public static readonly Color Border = Color.FromArgb(0xDE, 0xE2, 0xE6);

    public static readonly Font Title = new("Segoe UI Semibold", 16f);
    public static readonly Font Heading = new("Segoe UI Semibold", 11.5f);
    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font Small = new("Segoe UI", 8.5f);

    public static Color StatusColor(string status) => status switch
    {
        "Failed" => Fail,
        "Warning" => Warn,
        "Skipped" => TextSecondary,
        _ => Ok,
    };
}
