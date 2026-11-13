namespace CleverPoint.Migrator.Core.Reports;

/// <summary>Addresses written into report rows.</summary>
public static class Links
{
    /// <summary>
    /// A server-relative path as it has to stand in an address. SharePoint hands paths out as people
    /// read them, and a file may be called "Invoice #12 (50%).pdf": left as they are, the "#" would
    /// cut the address short and the "%" would make it invalid. Only those two are replaced, so that
    /// an address stays readable and one without them is unchanged.
    /// </summary>
    public static string Escape(string path) =>
        path.Contains('%') || path.Contains('#') ? path.Replace("%", "%25").Replace("#", "%23") : path;
}
