using System.Text.Json;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Lab for the broken "create new document" (doc.aspx server error) on
/// libraries the engine migrated into. Builds three libraries side by side
/// on the affected site - native, engine-created, engine-merged - and diffs
/// EVERYTHING the server exposes, then probes doc.aspx behavior directly.
/// </summary>
public static class NewDocLab
{
    private const string SiteUrl = "https://cleverpointlab.sharepoint.com/sites/Migrationson365Group";

    // Volatile/identity properties that always differ and prove nothing.
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "odata.editLink", "odata.id", "odata.etag", "Title", "Created", "LastItemModifiedDate",
        "LastItemUserModifiedDate", "LastItemDeletedDate", "ItemCount", "EntityTypeName", "ListItemEntityTypeFullName",
        "DocumentTemplateUrl", "DefaultViewUrl", "DefaultEditFormUrl", "DefaultDisplayFormUrl", "DefaultNewFormUrl",
        "ParentWebUrl", "ParentWebPath", "Description",
    };

    public static async Task RunAsync()
    {
        var stamp = DateTime.UtcNow.ToString("HHmmss");
        var target = Program.Target.ForWeb(SiteUrl);
        var source = Program.TestSite ?? Program.Source.ForWeb($"{Program.Source.SiteUrl}/migtest");

        // 1. NATIVE baseline library.
        var nativeTitle = $"ProbeNative{stamp}";
        using (var ctx = target.CreateContext())
        {
            ctx.Web.Lists.Add(new ListCreationInformation { Title = nativeTitle, TemplateType = 101, Url = nativeTitle });
            await ctx.ExecuteQueryAsync();
        }

        // 2. Engine-CREATED library (full structure + content copy).
        var createdTitle = $"ProbeMigrated{stamp}";
        var createdResult = await CopyEngine.CopyListAsync(source, target, TestAssets.SourceLibTitle,
            new CopyOptions { TargetListTitle = createdTitle, TargetListUrl = createdTitle });
        Console.WriteLine($"  engine-created copy: {createdResult.Summary()}");

        // 3. Engine-MERGED library (native first, then structure+content INTO it).
        var mergedTitle = $"ProbeMerged{stamp}";
        using (var ctx = target.CreateContext())
        {
            ctx.Web.Lists.Add(new ListCreationInformation { Title = mergedTitle, TemplateType = 101, Url = mergedTitle });
            await ctx.ExecuteQueryAsync();
        }
        var mergedResult = await CopyEngine.CopyListAsync(source, target, TestAssets.SourceLibTitle,
            new CopyOptions { TargetListTitle = mergedTitle });
        Console.WriteLine($"  engine-merged copy: {mergedResult.Summary()}");

        // ---- Dump and diff everything -------------------------------------
        var native = await DumpAsync(target, nativeTitle);
        var created = await DumpAsync(target, createdTitle);
        var merged = await DumpAsync(target, mergedTitle);

        Console.WriteLine("\n  ==== created vs native: differing list properties ====");
        Diff(native.ListProps, created.ListProps);
        Console.WriteLine("\n  ==== merged vs native: differing list properties ====");
        Diff(native.ListProps, merged.ListProps);

        Console.WriteLine($"\n  Forms folder: native=[{string.Join(", ", native.FormsFiles)}]");
        Console.WriteLine($"  Forms folder: created=[{string.Join(", ", created.FormsFiles)}]");
        Console.WriteLine($"  Forms folder: merged=[{string.Join(", ", merged.FormsFiles)}]");

        Console.WriteLine($"\n  content types: native=[{string.Join(", ", native.ContentTypes)}]");
        Console.WriteLine($"  content types: created=[{string.Join(", ", created.ContentTypes)}]");
        Console.WriteLine($"  content types: merged=[{string.Join(", ", merged.ContentTypes)}]");

        Console.WriteLine($"\n  extra fields on created (vs native): {string.Join(", ", created.Fields.Except(native.Fields))}");
        Console.WriteLine($"  extra fields on merged (vs native): {string.Join(", ", merged.Fields.Except(native.Fields))}");

        // ---- Try to reproduce doc.aspx server-side ------------------------
        foreach (var (label, dump) in new[] { ("native", native), ("created", created), ("merged", merged) })
        {
            var status = await ProbeDocAspxAsync(target, dump.RootFolderUrl, dump.TemplateUrl);
            Console.WriteLine($"  doc.aspx probe [{label}]: {status}");
        }

        Program.Check("newdoc lab ran (read the diffs above)", true);
    }

    private sealed record LibDump(Dictionary<string, string> ListProps, List<string> FormsFiles,
        List<string> ContentTypes, List<string> Fields, string RootFolderUrl, string? TemplateUrl);

    private static async Task<LibDump> DumpAsync(Core.Csom.SpConnection conn, string title)
    {
        using var listDoc = await conn.Rest.GetJsonAsync(
            $"{conn.SiteUrl}/_api/web/lists/GetByTitle('{Uri.EscapeDataString(title)}')");
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in listDoc.RootElement.EnumerateObject())
            if (p.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                props[p.Name] = p.Value.ToString();

        using var rootDoc = await conn.Rest.GetJsonAsync(
            $"{conn.SiteUrl}/_api/web/lists/GetByTitle('{Uri.EscapeDataString(title)}')/RootFolder?$select=ServerRelativeUrl");
        var root = rootDoc.RootElement.GetProperty("ServerRelativeUrl").GetString()!;

        var formsFiles = new List<string>();
        try
        {
            var esc = Uri.EscapeDataString($"{root}/Forms".Replace("'", "''"));
            using var forms = await conn.Rest.GetJsonAsync(
                $"{conn.SiteUrl}/_api/web/GetFolderByServerRelativePath(decodedUrl='{esc}')/Files?$select=Name,Length");
            formsFiles.AddRange(forms.RootElement.GetProperty("value").EnumerateArray()
                .Select(f => $"{f.GetProperty("Name").GetString()}:{f.GetProperty("Length").GetString()}"));
        }
        catch (Exception ex) { formsFiles.Add($"(error: {ex.Message[..Math.Min(60, ex.Message.Length)]})"); }

        var cts = new List<string>();
        using (var ctDoc = await conn.Rest.GetJsonAsync(
            $"{conn.SiteUrl}/_api/web/lists/GetByTitle('{Uri.EscapeDataString(title)}')/ContentTypes?$select=Name,DocumentTemplateUrl"))
        {
            cts.AddRange(ctDoc.RootElement.GetProperty("value").EnumerateArray()
                .Select(c => $"{c.GetProperty("Name").GetString()}({c.GetProperty("DocumentTemplateUrl").GetString()})"));
        }

        var fields = new List<string>();
        using (var fDoc = await conn.Rest.GetJsonAsync(
            $"{conn.SiteUrl}/_api/web/lists/GetByTitle('{Uri.EscapeDataString(title)}')/Fields?$select=InternalName,Hidden&$filter=Hidden eq false"))
        {
            fields.AddRange(fDoc.RootElement.GetProperty("value").EnumerateArray()
                .Select(f => f.GetProperty("InternalName").GetString()!));
        }

        var tpl = props.TryGetValue("DocumentTemplateUrl", out var dt) && dt.Length > 0 ? dt : null;
        return new LibDump(props, formsFiles, cts, fields, root, tpl);
    }

    private static void Diff(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        foreach (var key in a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k))
        {
            if (Noise.Contains(key)) continue;
            var va = a.GetValueOrDefault(key, "(absent)");
            var vb = b.GetValueOrDefault(key, "(absent)");
            if (va != vb) Console.WriteLine($"    {key}: native='{va}'  engine='{vb}'");
        }
    }

    /// <summary>GETs the new-document page the way the browser does and reports the HTTP status.</summary>
    private static async Task<string> ProbeDocAspxAsync(Core.Csom.SpConnection conn, string rootUrl, string? templateUrl)
    {
        var docliburl = Uri.EscapeDataString($"https://{new Uri(conn.SiteUrl).Host}{rootUrl}");
        var tpl = Uri.EscapeDataString($"https://{new Uri(conn.SiteUrl).Host}{templateUrl ?? rootUrl + "/Forms/template.dotx"}");
        var url = $"{conn.SiteUrl}/_layouts/15/doc.aspx?sourcedoc=&action=editnew&docliburl={docliburl}&templateurl={tpl}&mode=view";
        try
        {
            var body = await conn.Rest.SendAsync(HttpMethod.Get, url, null, null);
            var snippet = body.Length > 80 ? body[..80].Replace('\n', ' ') : body;
            return $"HTTP 200, {body.Length} bytes ({snippet}...)";
        }
        catch (Core.Http.SpRestException ex)
        {
            return $"HTTP {ex.StatusCode}";
        }
    }
}
