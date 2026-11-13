namespace CleverPoint.Migrator.Core.Reports;

/// <summary>
/// Sorts file types into the kinds people talk about: PDF, Office documents, video, images. A storage
/// report by extension has hundreds of lines; by kind it has nine. The list is short on purpose: each
/// kind has a color of its own on the storage map, and only so many colors can be told apart.
/// </summary>
public static class FileKinds
{
    public const string Pdf = "PDF";
    public const string Office = "Office documents";
    public const string OneNote = "OneNote";
    public const string Images = "Images";
    public const string Video = "Video";
    public const string Audio = "Audio";
    public const string Archives = "Archives";
    public const string Email = "Email";
    public const string Other = "Other";

    /// <summary>Every kind, in the order their colors are given out. <see cref="Other"/> is last.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Office, Archives, Images, Audio, Video, Email, OneNote, Pdf, Other };

    private static readonly (string Kind, string[] Extensions)[] Groups =
    {
        (Pdf, new[] { "pdf" }),
        (Office, new[]
        {
            "doc", "docx", "docm", "dot", "dotx", "dotm", "rtf", "odt",
            "xls", "xlsx", "xlsm", "xlsb", "xlt", "xltx", "xltm", "csv", "ods",
            "ppt", "pptx", "pptm", "pps", "ppsx", "ppsm", "pot", "potx", "potm", "odp",
            "vsd", "vsdx", "vsdm", "vss", "vssx", "vst", "vstx", "mpp", "mpt", "pub", "accdb", "mdb", "xsn",
        }),
        (OneNote, new[] { "one", "onetoc", "onetoc2", "onepkg" }),
        (Images, new[] { "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "svg", "webp", "heic", "heif", "ico", "psd", "ai", "eps", "raw", "cr2", "nef", "dng", "emf", "wmf" }),
        (Video, new[] { "mp4", "mov", "avi", "wmv", "mkv", "m4v", "webm", "mpg", "mpeg", "flv", "mts", "m2ts", "3gp", "vob", "trec" }),
        (Audio, new[] { "mp3", "wav", "m4a", "wma", "aac", "flac", "ogg", "aif", "aiff", "mid" }),
        (Archives, new[] { "zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "iso", "cab", "bak", "vhd", "vhdx" }),
        (Email, new[] { "msg", "eml", "pst", "ost", "mbox" }),
    };

    private static readonly Dictionary<string, string> ByExtension = Index();

    private static Dictionary<string, string> Index()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, extensions) in Groups)
            foreach (var extension in extensions) index.TryAdd(extension, kind);
        return index;
    }

    /// <summary>The kind of a file by its extension, with or without the dot. Types not in the list are <see cref="Other"/>.</summary>
    public static string Of(string? extension) =>
        ByExtension.TryGetValue((extension ?? "").TrimStart('.'), out var kind) ? kind : Other;

    /// <summary>The place of a kind in <see cref="All"/>, or -1 for a name that is not a kind.</summary>
    public static int IndexOf(string? kind)
    {
        if (kind is null) return -1;
        for (var i = 0; i < All.Count; i++)
            if (All[i].Equals(kind, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
