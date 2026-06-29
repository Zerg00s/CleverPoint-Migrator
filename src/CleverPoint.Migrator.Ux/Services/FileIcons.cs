namespace CleverPoint.Migrator.Ux.Services;

/// <summary>
/// Maps SharePoint items to the official Microsoft Fluent file-type icons that
/// ship in wwwroot/file-icons (SVG). Returns a web path the UI renders as &lt;img&gt;.
/// </summary>
public static class FileIcons
{
    public static string Url(string name, int size = 48) => $"file-icons/{size}/{name}.svg";

    /// <summary>Type-specific icon for a list/library by its SharePoint template.</summary>
    public static string ForList(SpListInfo l) => Url(IconForTemplate(l.BaseTemplate, l.IsLibrary));

    /// <summary>Icon for a log row. File rows use the real extension (docx, pptx, txt,
    /// one, onetoc2, ...); everything else falls back to the item-type icon.</summary>
    public static string ForRecord(string? itemType, string? sourcePath)
    {
        // Only "File" rows carry a real extension. A "OneNote" row is a notebook root
        // (a folder with no extension), so it keeps its notebook icon via ForRecordType.
        if (string.Equals(itemType, "file", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(sourcePath))
            return Url(ForExtension(Path.GetExtension(sourcePath)));
        return ForRecordType(itemType);
    }

    /// <summary>Icon for a log row's item type (File / Page / Item / Folder / ...).</summary>
    public static string ForRecordType(string? itemType) => Url((itemType ?? "").ToLowerInvariant() switch
    {
        "file" => "genericfile",
        "page" => "sponews",
        "item" => "listitem",
        "folder" => "folder",
        "list" => "splist",
        "user" => "contact",
        "sitecolumn" or "column" or "field" => "listform",
        "contenttype" or "content type" => "docset",
        "view" => "listform",
        "onenote" => "onetoc",
        "progress" => "copilot",
        _ => "spo",
    });

    /// <summary>User-friendly label for a log row's item type ("SiteColumn" -> "Site Column").</summary>
    public static string FriendlyType(string? itemType) => (itemType ?? "").ToLowerInvariant() switch
    {
        "file" => "File",
        "folder" => "Folder",
        "page" => "Page",
        "item" => "List item",
        "list" => "List",
        "user" => "User",
        "sitecolumn" or "column" or "field" => "Site column",
        "contenttype" or "content type" => "Content type",
        "view" => "View",
        "run" => "Run",
        "onenote" => "OneNote notebook",
        "progress" => "Progress",
        "" => "",
        // Fall back to splitting CamelCase so any unmapped type still reads nicely.
        _ => System.Text.RegularExpressions.Regex.Replace(itemType!, "(?<=[a-z])(?=[A-Z])", " "),
    };

    private static string IconForTemplate(int template, bool isLibrary) => template switch
    {
        101 or 700 => "documentsfolder",         // Document library
        109 => "picturesfolder",                  // Picture library
        851 => "picturesfolder",                  // Asset library (Site Assets)
        115 => "form",                            // Form library (XML forms)
        119 or 544 or 850 => "sponews",           // Wiki / Site Pages library
        106 => "calendar",                        // Events / calendar
        105 => "contact",                         // Contacts
        103 or 170 => "link",                     // Links / promoted links
        107 or 171 => "todoitem",                 // Tasks
        100 => "splist",                          // Generic / custom list
        _ => isLibrary ? "documentsfolder" : "splist",
    };

    public static string ForEntry(SpFolderEntry e) =>
        e.IsOneNote ? Url("onetoc")                              // notebook, shown as a OneNote unit
        : e.IsFolder ? Url("folder")
        : Url(ForExtension(Path.GetExtension(e.Name)));

    private static string ForExtension(string ext)
    {
        ext = ext.TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "doc" or "docm" or "docx" or "docb" or "odt" => "docx",
            "dot" or "dotx" or "dotm" => "dotx",
            "xls" or "xlsx" or "xlsm" or "xlsb" or "ods" => "xlsx",
            "xlt" or "xltx" or "xltm" => "xltx",
            "csv" => "csv",
            "ppt" or "pptx" or "pptm" or "odp" => "pptx",
            "pps" or "ppsx" or "ppsm" => "ppsx",
            "pot" or "potx" or "potm" => "potx",
            "pdf" => "pdf",
            "txt" or "log" or "ini" or "cfg" => "txt",
            "md" => "md",
            "rtf" => "rtf",
            "zip" or "7z" or "rar" or "tar" or "gz" or "cab" => "zip",
            "png" or "jpg" or "jpeg" or "gif" or "bmp" or "tif" or "tiff" or "webp" or "heic" or "ico" or "svg" => "photo",
            "mp4" or "mov" or "avi" or "wmv" or "mkv" or "m4v" or "webm" or "mpg" or "mpeg" => "video",
            "mp3" or "wav" or "wma" or "m4a" or "aac" or "flac" or "ogg" => "audio",
            "one" => "one",
            "onetoc" or "onetoc2" => "onetoc",
            "vsd" or "vsdx" or "vsdm" => "vsdx",
            "vss" or "vssx" => "vssx",
            "vst" or "vstx" => "vstx",
            "mpp" => "mpp",
            "mpt" => "mpt",
            "htm" or "html" or "aspx" or "asp" or "xhtml" => "html",
            "xml" or "xsd" or "xsl" => "xml",
            "xsn" => "xsn",
            "json" or "js" or "ts" or "cs" or "css" or "scss" or "ps1" or "psm1" or "sql" or "py" or "java" or "cpp" or "c" or "h" or "go" or "rb" or "php" or "sh" or "yml" or "yaml" => "code",
            "ipynb" => "ipynb",
            "accdb" or "mdb" => "accdb",
            "exe" or "msi" or "bat" or "cmd" or "dll" => "exe",
            "pub" => "pub",
            "msg" or "eml" => "email",
            "vsix" or "nupkg" => "archive",
            "ttf" or "otf" or "woff" or "woff2" => "font",
            "mpw" or "model" or "fbx" or "obj" or "stl" or "glb" => "model",
            _ => "genericfile",
        };
    }
}
