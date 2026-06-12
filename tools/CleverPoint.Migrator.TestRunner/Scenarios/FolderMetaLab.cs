using System.Globalization;
using CleverPoint.Migrator.Core.Csom;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner.Scenarios;

/// <summary>
/// Live experiment: which write pattern preserves Author/Editor AND
/// Created/Modified on FOLDER items on THIS tenant?
///   A: one UpdateOverwriteVersion with FieldUserValue users + dates.
///   B: one ValidateUpdateListItem with claims users + locale-formatted dates
///      (converted to the web's regional timezone).
/// The winner becomes the engine's folder metadata strategy.
/// </summary>
public static class FolderMetaLab
{
    public static async Task RunAsync()
    {
        var site = Program.TestSite ?? await TestAssets.EnsureTestSiteAsync(Program.Source);
        Program.TestSite = site;
        if (TestAssets.SourceUsers.Count < 2)
        {
            Program.Check("folder-lab: needs 2+ users", false, "not enough real users");
            return;
        }

        var author = TestAssets.SourceUsers[0];
        var editor = TestAssets.SourceUsers[1];
        var createdUtc = new DateTime(2021, 4, 7, 13, 45, 0, DateTimeKind.Utc);
        var modifiedUtc = new DateTime(2022, 11, 19, 8, 30, 0, DateTimeKind.Utc);

        using var ctx = site.CreateContext();
        var list = ctx.Web.Lists.GetByTitle(TestAssets.SourceListTitle);
        ctx.Load(list.RootFolder, f => f.ServerRelativeUrl);
        await ctx.ExecuteQueryAsync();

        // ---- Strategy A: UpdateOverwriteVersion with users + dates ----
        var itemA = await CreateFolderAsync(ctx, list, "Lab-A");
        var aOk = false;
        var aDetail = "";
        try
        {
            var authorUser = ctx.Web.EnsureUser(author.Login);
            var editorUser = ctx.Web.EnsureUser(editor.Login);
            ctx.Load(authorUser, u => u.Id);
            ctx.Load(editorUser, u => u.Id);
            await ctx.ExecuteQueryAsync();

            itemA["Author"] = new FieldUserValue { LookupId = authorUser.Id };
            itemA["Editor"] = new FieldUserValue { LookupId = editorUser.Id };
            itemA["Created"] = createdUtc.ToLocalTime();
            itemA["Modified"] = modifiedUtc.ToLocalTime();
            itemA.UpdateOverwriteVersion();
            await ctx.ExecuteQueryAsync();
            (aOk, aDetail) = await ReadBackAsync(ctx, list, itemA.Id, author.Email, editor.Email, createdUtc, modifiedUtc);
        }
        catch (Exception ex)
        {
            aDetail = $"threw: {ex.Message}";
        }
        Program.Check("folder-lab A (UOV users+dates)", aOk, aDetail);

        // ---- Strategy B: single ValidateUpdateListItem with locale dates ----
        var itemB = await CreateFolderAsync(ctx, list, "Lab-B");
        var bOk = false;
        var bDetail = "";
        try
        {
            var tz = ctx.Web.RegionalSettings.TimeZone;
            ctx.Load(tz);
            var createdSite = tz.UTCToLocalTime(createdUtc);
            var modifiedSite = tz.UTCToLocalTime(modifiedUtc);
            await ctx.ExecuteQueryAsync();

            var formValues = new List<ListItemFormUpdateValue>
            {
                new() { FieldName = "Author", FieldValue = $"[{{\"Key\":\"{author.Login}\"}}]" },
                new() { FieldName = "Editor", FieldValue = $"[{{\"Key\":\"{editor.Login}\"}}]" },
                new() { FieldName = "Created", FieldValue = createdSite.Value.ToString("M/d/yyyy h:mm tt", CultureInfo.InvariantCulture) },
                new() { FieldName = "Modified", FieldValue = modifiedSite.Value.ToString("M/d/yyyy h:mm tt", CultureInfo.InvariantCulture) },
            };
            var validation = itemB.ValidateUpdateListItem(formValues, false, "", false, false, "");
            await ctx.ExecuteQueryAsync();
            var errors = validation.Where(v => v.HasException).Select(v => $"{v.FieldName}: {v.ErrorMessage}").ToList();
            if (errors.Count > 0)
                bDetail = string.Join("; ", errors);
            else
                (bOk, bDetail) = await ReadBackAsync(ctx, list, itemB.Id, author.Email, editor.Email, createdUtc, modifiedUtc);
        }
        catch (Exception ex)
        {
            bDetail = $"threw: {ex.Message}";
        }
        Program.Check("folder-lab B (VULI users+locale dates)", bOk, bDetail);
    }

    private static async Task<ListItem> CreateFolderAsync(ClientContext ctx, List list, string name)
    {
        // Delete leftover from a previous run, then create fresh.
        try
        {
            var existing = ctx.Web.GetFolderByServerRelativeUrl($"{list.RootFolder.ServerRelativeUrl}/{name}");
            existing.DeleteObject();
            await ctx.ExecuteQueryAsync();
        }
        catch (ServerException) { /* did not exist */ }

        var item = list.AddItem(new ListItemCreationInformation
        {
            UnderlyingObjectType = FileSystemObjectType.Folder,
            LeafName = name,
        });
        item["Title"] = name;
        item.Update();
        ctx.Load(item, i => i.Id);
        await ctx.ExecuteQueryAsync();
        return item;
    }

    private static async Task<(bool Ok, string Detail)> ReadBackAsync(ClientContext ctx, List list, int id,
        string expectedAuthorEmail, string expectedEditorEmail, DateTime expectedCreatedUtc, DateTime expectedModifiedUtc)
    {
        var item = list.GetItemById(id);
        ctx.Load(item);
        await ctx.ExecuteQueryAsync();

        var author = item.FieldValues["Author"] as FieldUserValue;
        var editor = item.FieldValues["Editor"] as FieldUserValue;
        var created = DateTime.SpecifyKind((DateTime)item.FieldValues["Created"], DateTimeKind.Utc);
        var modified = DateTime.SpecifyKind((DateTime)item.FieldValues["Modified"], DateTimeKind.Utc);

        var problems = new List<string>();
        if (!string.Equals(author?.Email, expectedAuthorEmail, StringComparison.OrdinalIgnoreCase))
            problems.Add($"Author={author?.LookupValue}({author?.Email})");
        if (!string.Equals(editor?.Email, expectedEditorEmail, StringComparison.OrdinalIgnoreCase))
            problems.Add($"Editor={editor?.LookupValue}({editor?.Email})");
        if (Math.Abs((created - expectedCreatedUtc).TotalMinutes) > 1)
            problems.Add($"Created={created:yyyy-MM-dd HH:mm}Z exp {expectedCreatedUtc:yyyy-MM-dd HH:mm}Z");
        if (Math.Abs((modified - expectedModifiedUtc).TotalMinutes) > 1)
            problems.Add($"Modified={modified:yyyy-MM-dd HH:mm}Z exp {expectedModifiedUtc:yyyy-MM-dd HH:mm}Z");

        return problems.Count == 0
            ? (true, "all four fields round-tripped")
            : (false, string.Join("; ", problems));
    }
}
