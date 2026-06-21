namespace CleverPoint.Migrator.Ux.Services;

/// <summary>
/// Maps SharePoint items to the official Microsoft Fluent file-type icons that
/// ship in wwwroot/file-icons (SVG). Returns a web path the UI renders as &lt;img&gt;.
/// </summary>
public static class FileIcons
{
    public static string Url(string name, int size = 48) => $"file-icons/{size}/{name}.svg";

    public static string ForList(bool isLibrary) => Url(isLibrary ? "documentsfolder" : "splist");

    public static string ForEntry(SpFolderEntry e) =>
        e.IsFolder ? Url("folder") : Url(ForExtension(Path.GetExtension(e.Name)));

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
