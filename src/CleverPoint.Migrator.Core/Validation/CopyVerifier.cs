using System.Security.Cryptography;
using CleverPoint.Migrator.Core.Model;
using CleverPoint.Migrator.Core.Operations;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.Core.Validation;

/// <summary>
/// Post-copy verification: re-reads source and target and compares item
/// fields, dates (exact UTC), authors/editors (by display name + email), and
/// file content hashes. Returns human-readable mismatch descriptions; empty
/// list means the copy is faithful.
/// </summary>
public class CopyVerifier
{
    private readonly ClientContext _sourceCtx;
    private readonly ClientContext _targetCtx;

    public CopyVerifier(ClientContext sourceCtx, ClientContext targetCtx)
    {
        _sourceCtx = sourceCtx;
        _targetCtx = targetCtx;
    }

    /// <summary>
    /// Optional authoritative pairing: source item id -> target item id (from
    /// HistoryStore). Without it, plain list items pair by Title, which is
    /// only safe for test data with unique titles.
    /// </summary>
    public Dictionary<int, int>? ItemMap { get; set; }

    public async Task<List<string>> VerifyAsync(List sourceList, List targetList,
        IEnumerable<string> compareFields, bool compareFileContent = false,
        Dictionary<string, string>? knownSourceHashes = null, bool compareUsers = true,
        int contentSampleEvery = 1)
    {
        var fileIndex = 0;
        var mismatches = new List<string>();

        var sourceItems = await LoadAsync(_sourceCtx, sourceList);
        var targetItems = await LoadAsync(_targetCtx, targetList);
        _sourceCtx.Load(sourceList, l => l.BaseType);
        await _sourceCtx.ExecuteQueryAsync();
        var sourceRoot = sourceList.RootFolder.ServerRelativeUrl;
        var targetRoot = targetList.RootFolder.ServerRelativeUrl;
        var isLibrary = sourceList.BaseType == BaseType.DocumentLibrary;

        // Pairing key: files/folders have stable relative paths. Plain list
        // items get ID-based FileRef leaves ("12_.000") that do NOT line up
        // between lists, so they pair by Title + parent path instead.
        var reverseMap = ItemMap?.ToDictionary(kv => kv.Value, kv => kv.Key);
        string KeyOf(ListItem i, string root)
        {
            var rel = Rel(root, (string)i["FileRef"]);
            if (isLibrary || i.FileSystemObjectType == FileSystemObjectType.Folder) return rel;
            // Authoritative id pairing when a map is supplied (duplicate-title safe).
            if (ItemMap != null)
            {
                var isSource = root.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase);
                if (isSource && ItemMap.ContainsKey(i.Id)) return $"map:{ItemMap[i.Id]}";
                if (!isSource && reverseMap!.ContainsKey(i.Id)) return $"map:{i.Id}";
            }
            var parent = rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";
            return $"{parent}|item:{i.FieldValues.GetValueOrDefault("Title")}";
        }

        var targetByPath = targetItems.ToDictionary(
            i => KeyOf(i, targetRoot), i => i, StringComparer.OrdinalIgnoreCase);

        foreach (var src in sourceItems)
        {
            var rel = KeyOf(src, sourceRoot);
            if (!targetByPath.TryGetValue(rel, out var tgt))
            {
                mismatches.Add($"MISSING on target: {rel}");
                continue;
            }

            foreach (var field in compareFields)
            {
                var sv = src.FieldValues.GetValueOrDefault(field);
                var tv = tgt.FieldValues.GetValueOrDefault(field);
                if (!ValuesEqual(sv, tv))
                    mismatches.Add($"{rel}: field '{field}' differs (source={Render(sv)}, target={Render(tv)})");
            }

            CompareDate(src, tgt, "Created", rel, mismatches);
            CompareDate(src, tgt, "Modified", rel, mismatches);
            if (compareUsers)
            {
                CompareUser(src, tgt, "Author", rel, mismatches);
                CompareUser(src, tgt, "Editor", rel, mismatches);
            }

            if (compareFileContent && src.FileSystemObjectType == FileSystemObjectType.File
                && fileIndex++ % contentSampleEvery == 0)
            {
                var srcHash = knownSourceHashes?.GetValueOrDefault((string)src["FileRef"])
                    ?? await HashFileAsync(_sourceCtx, (string)src["FileRef"]);
                var tgtHash = await HashFileAsync(_targetCtx, (string)tgt["FileRef"]);
                if (srcHash != tgtHash)
                    mismatches.Add($"{rel}: CONTENT HASH differs (source={srcHash[..12]}..., target={tgtHash[..12]}...)");
            }
        }

        // Anything extra on the target?
        var sourcePaths = new HashSet<string>(sourceItems.Select(i => KeyOf(i, sourceRoot)), StringComparer.OrdinalIgnoreCase);
        foreach (var extra in targetByPath.Keys.Where(p => !sourcePaths.Contains(p)))
            mismatches.Add($"EXTRA on target: {extra}");

        return mismatches;
    }

    public static async Task<string> HashFileAsync(ClientContext ctx, string serverRelativeUrl)
    {
        var file = ctx.Web.GetFileByServerRelativeUrl(serverRelativeUrl);
        var stream = file.OpenBinaryStream();
        await ctx.ExecuteQueryAsync();
        using var ms = new MemoryStream();
        await stream.Value.CopyToAsync(ms);
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }

    private static async Task<List<ListItem>> LoadAsync(ClientContext ctx, List list)
    {
        ctx.Load(list, l => l.RootFolder.ServerRelativeUrl);
        var items = new List<ListItem>();
        var query = new CamlQuery { ViewXml = "<View Scope='RecursiveAll'><RowLimit Paged='TRUE'>500</RowLimit></View>" };
        do
        {
            var page = list.GetItems(query);
            ctx.Load(page);
            ctx.Load(page, p => p.Include(i => i.Id, i => i.FileSystemObjectType),
                p => p.ListItemCollectionPosition);
            await ctx.ExecuteQueryAsync();
            items.AddRange(page);
            query.ListItemCollectionPosition = page.ListItemCollectionPosition;
        } while (query.ListItemCollectionPosition != null);
        return items;
    }

    private static string Rel(string root, string fileRef) =>
        fileRef.Length > root.Length ? fileRef[(root.Length + 1)..] : "";

    private static void CompareDate(ListItem src, ListItem tgt, string field, string rel, List<string> mismatches)
    {
        var sv = src.FieldValues.GetValueOrDefault(field);
        var tv = tgt.FieldValues.GetValueOrDefault(field);
        if (sv is not DateTime sd || tv is not DateTime td)
        {
            if (!Equals(sv, tv)) mismatches.Add($"{rel}: {field} missing on one side");
            return;
        }
        if (Math.Abs((sd - td).TotalSeconds) > 1)
            mismatches.Add($"{rel}: {field} differs (source={sd:yyyy-MM-dd HH:mm:ss}Z, target={td:yyyy-MM-dd HH:mm:ss}Z)");
    }

    /// <summary>
    /// System identities that are semantically interchangeable: app-authored
    /// content imports as "System Account" via the Migration API (app
    /// principals cannot be referenced in packages).
    /// </summary>
    private static readonly HashSet<string> SystemIdentities = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Account", "SharePoint App", "app@sharepoint",
    };

    private static void CompareUser(ListItem src, ListItem tgt, string field, string rel, List<string> mismatches)
    {
        var sv = src.FieldValues.GetValueOrDefault(field) as FieldUserValue;
        var tv = tgt.FieldValues.GetValueOrDefault(field) as FieldUserValue;
        if (sv == null && tv == null) return;
        if (sv == null || tv == null)
        {
            mismatches.Add($"{rel}: {field} present on one side only");
            return;
        }
        if (SystemIdentities.Contains(sv.LookupValue ?? "") && SystemIdentities.Contains(tv.LookupValue ?? ""))
            return;
        // Same-tenant copies should preserve the user exactly; cross-tenant
        // mapping is checked by email when available, falling back to display name.
        var se = sv.Email ?? "";
        var te = tv.Email ?? "";
        if (se.Length > 0 && te.Length > 0)
        {
            if (!se.Equals(te, StringComparison.OrdinalIgnoreCase))
                mismatches.Add($"{rel}: {field} differs (source={se}, target={te})");
        }
        else if (!string.Equals(sv.LookupValue, tv.LookupValue, StringComparison.OrdinalIgnoreCase))
        {
            mismatches.Add($"{rel}: {field} differs (source={sv.LookupValue}, target={tv.LookupValue})");
        }
    }

    private static bool ValuesEqual(object? a, object? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        return (a, b) switch
        {
            (FieldUserValue ua, FieldUserValue ub) => string.Equals(ua.Email ?? ua.LookupValue, ub.Email ?? ub.LookupValue, StringComparison.OrdinalIgnoreCase),
            // Lookups compare by display value: item ids legitimately differ between lists.
            (FieldLookupValue la2, FieldLookupValue lb2) => string.Equals(la2.LookupValue, lb2.LookupValue, StringComparison.OrdinalIgnoreCase),
            (FieldLookupValue[] laa, FieldLookupValue[] lbb) => laa.Select(v => v.LookupValue).SequenceEqual(lbb.Select(v => v.LookupValue), StringComparer.OrdinalIgnoreCase),
            (FieldUrlValue la, FieldUrlValue lb) => la.Url == lb.Url && la.Description == lb.Description,
            (DateTime da, DateTime db) => Math.Abs((da - db).TotalSeconds) <= 1,
            (string[] sa, string[] sb) => sa.SequenceEqual(sb),
            (double na, double nb) => Math.Abs(na - nb) < 0.000001,
            _ => a.ToString() == b.ToString(),
        };
    }

    private static string Render(object? v) => v switch
    {
        null => "(null)",
        FieldUserValue u => u.Email ?? u.LookupValue ?? $"#{u.LookupId}",
        FieldUrlValue l => l.Url,
        string[] arr => string.Join(";", arr),
        _ => v.ToString() ?? "",
    };
}
