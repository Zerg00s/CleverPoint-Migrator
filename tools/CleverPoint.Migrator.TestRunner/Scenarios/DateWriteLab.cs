using CleverPoint.Migrator.Core.Model;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Ground-truth date lab. CSOM's own DateTime parsing may convert timezones
/// on BOTH write and read, so the only objective observer is the raw REST
/// JSON string of the stored value. Writes a known instant (2021-03-03T09:00Z)
/// through UOV with each DateTime kind and reads back via raw REST.
/// Also prints what CSOM deserialization returns for the same value, so the
/// read-side convention is pinned down too.
/// </summary>
public static class DateWriteLab
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? Program.Source.ForWeb($"{Program.Source.SiteUrl}/migtest");
        var known = new DateTime(2021, 3, 3, 9, 0, 0, DateTimeKind.Utc);
        Console.WriteLine($"  machine TZ: {TimeZoneInfo.Local.Id} (offset now {TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow)})");
        using var ctx = site.CreateContext();
        var list = ctx.Web.Lists.GetByTitle(TestAssets.SourceLibTitle);
        ctx.Load(list.RootFolder, f => f.ServerRelativeUrl);
        await ctx.ExecuteQueryAsync();
        var root = list.RootFolder.ServerRelativeUrl;

        foreach (var (label, value) in new (string, DateTime)[]
        {
            ("Utc-kind 09:00Z", known),
            ("Local-kind (machine-local of 09:00Z)", known.ToLocalTime()),
            ("Unspecified UTC-digits 09:00", DateTime.SpecifyKind(known, DateTimeKind.Unspecified)),
        })
        {
            var name = $"date-lab-{Guid.NewGuid():N}.bin";
            var folder = ctx.Web.GetFolderByServerRelativePath(ResourcePath.FromDecodedUrl(root));
            var f = folder.Files.Add(new FileCreationInformation { Url = name, Content = new byte[] { 1 }, Overwrite = true });
            ctx.Load(f, x => x.ListItemAllFields.Id);
            await ctx.ExecuteQueryAsync();
            var item = f.ListItemAllFields;
            var id = item.Id;
            item["Modified"] = value;
            item.UpdateOverwriteVersion();
            await ctx.ExecuteQueryAsync();

            // OBJECTIVE read: raw REST JSON string, no DateTime parsing.
            var escRoot = Uri.EscapeDataString(root.Replace("'", "''"));
            using var raw = await site.Rest.GetJsonAsync(
                $"{site.SiteUrl}/_api/web/GetList(@a1)/items({id})?$select=Modified&@a1='{escRoot}'");
            var storedIso = raw.RootElement.GetProperty("Modified").GetString();

            // CSOM read of the same value, fresh object.
            var fresh = ctx.Web.GetFileByServerRelativePath(ResourcePath.FromDecodedUrl($"{root}/{name}")).ListItemAllFields;
            ctx.Load(fresh);
            await ctx.ExecuteQueryAsync();
            var csomRead = (DateTime)fresh["Modified"];

            var exact = storedIso == "2021-03-03T09:00:00Z";
            Console.WriteLine($"  {label}: REST stored='{storedIso}' {(exact ? "EXACT" : "WRONG")}  | CSOM reads digits={csomRead:yyyy-MM-ddTHH:mm:ss} kind={csomRead.Kind}");

            try { ctx.Web.GetFileByServerRelativePath(ResourcePath.FromDecodedUrl($"{root}/{name}")).DeleteObject(); await ctx.ExecuteQueryAsync(); }
            catch { }
        }
        Program.Check("date ground-truth lab ran", true);
    }
}
