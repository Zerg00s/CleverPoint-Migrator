using CleverPoint.Migrator.Core.Csom;
using Microsoft.SharePoint.Client;

namespace CleverPoint.Migrator.TestRunner;

/// <summary>
/// Provisions deterministic test content on the source tenant: a dedicated
/// subsite under DemoLargeSite, a custom list with varied field types and
/// back-dated authored items, and a document library with folders and files.
/// Seeded RNG so re-runs produce identical data.
/// </summary>
public static class TestAssets
{
    public const string SubsiteLeaf = "migtest";
    public const string SourceListTitle = "MigTest-Source";
    public const string SourceLibTitle = "MigTest-SrcLib";
    public const string LookupTargetTitle = "MigTest-LookupTarget";

    public static readonly string[] LookupValues = { "Alpha", "Bravo", "Charlie", "Delta" };

    /// <summary>Column formatting JSON applied to NumberCol (a simple data bar).</summary>
    public const string NumberColFormatJson = "{\"$schema\":\"https://developer.microsoft.com/json-schemas/sp/v2/column-formatting.schema.json\",\"elmType\":\"div\",\"txtContent\":\"@currentField\",\"style\":{\"background-color\":\"#e3f2fd\"}}";

    /// <summary>View formatting JSON applied to the custom view.</summary>
    public const string ViewFormatJson = "{\"$schema\":\"https://developer.microsoft.com/json-schemas/sp/v2/row-formatting.schema.json\",\"additionalRowClass\":\"=if([$FlagCol] == true, 'sp-field-severity--good', '')\"}";

    public static readonly string[] Choices = { "Red", "Green", "Blue", "Amber" };

    /// <summary>Users with email on the source site; populated by EnsureTestSiteAsync.</summary>
    public static List<(int Id, string Login, string Email, string Title)> SourceUsers = new();

    public static async Task<SpConnection> EnsureTestSiteAsync(SpConnection parent)
    {
        using var ctx = parent.CreateContext();
        ctx.Load(ctx.Web.Webs, ws => ws.Include(w => w.ServerRelativeUrl, w => w.Url));
        ctx.Load(ctx.Web, w => w.ServerRelativeUrl);
        await ctx.ExecuteQueryAsync();

        var expected = $"{ctx.Web.ServerRelativeUrl}/{SubsiteLeaf}";
        var existing = ctx.Web.Webs.AsEnumerable().FirstOrDefault(w =>
            w.ServerRelativeUrl.Equals(expected, StringComparison.OrdinalIgnoreCase));

        string subUrl;
        if (existing != null)
        {
            subUrl = existing.Url;
            Console.WriteLine($"  test subsite exists: {subUrl}");
        }
        else
        {
            var created = ctx.Web.Webs.Add(new WebCreationInformation
            {
                Url = SubsiteLeaf,
                Title = "Migrator Test",
                WebTemplate = "STS#3",
                UseSamePermissionsAsParentSite = true,
                Language = 1033,
            });
            ctx.Load(created, w => w.Url);
            await ctx.ExecuteQueryAsync();
            subUrl = created.Url;
            Console.WriteLine($"  created test subsite: {subUrl}");
        }

        var sub = parent.ForWeb(subUrl);

        // Collect real users (with email) for person fields and authorship.
        using var subCtx = sub.CreateContext();
        subCtx.Load(subCtx.Web.SiteUsers, us => us.Include(u => u.Id, u => u.LoginName, u => u.Email, u => u.Title, u => u.PrincipalType));
        await subCtx.ExecuteQueryAsync();
        SourceUsers = subCtx.Web.SiteUsers.AsEnumerable().AsEnumerable()
            .Where(u => u.PrincipalType == Microsoft.SharePoint.Client.Utilities.PrincipalType.User
                && !string.IsNullOrEmpty(u.Email))
            .Select(u => (u.Id, u.LoginName, u.Email!, u.Title ?? ""))
            .ToList();
        Console.WriteLine($"  {SourceUsers.Count} real user(s) available for authorship tests");
        return sub;
    }

    /// <summary>The small list that LookupCol points at. Returns its id.</summary>
    public static async Task<Guid> EnsureLookupTargetAsync(ClientContext ctx)
    {
        var lists = ctx.Web.Lists;
        ctx.Load(lists, ls => ls.Include(l => l.Title, l => l.Id));
        await ctx.ExecuteQueryAsync();
        var existing = lists.AsEnumerable().FirstOrDefault(l => l.Title.Equals(LookupTargetTitle, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing.Id;

        var list = ctx.Web.Lists.Add(new ListCreationInformation
        {
            Title = LookupTargetTitle,
            TemplateType = (int)ListTemplateType.GenericList,
            Url = "Lists/MigTestLookupTarget",
        });
        ctx.Load(list, l => l.Id);
        await ctx.ExecuteQueryAsync();
        foreach (var value in LookupValues)
        {
            var item = list.AddItem(new ListItemCreationInformation());
            item["Title"] = value;
            item.Update();
        }
        await ctx.ExecuteQueryAsync();
        return list.Id;
    }

    public static async Task RecreateSourceListAsync(SpConnection site)
    {
        using var ctx = site.CreateContext();
        var lookupListId = await EnsureLookupTargetAsync(ctx);
        var deleted = await DeleteIfExistsAsync(ctx, SourceListTitle);

        List list;
        if (!deleted)
        {
            // Retention kept the (now empty) list; its schema is intact.
            list = ctx.Web.Lists.GetByTitle(SourceListTitle);
            await EnsureExtrasAsync(ctx, list, lookupListId);
            await ReseedListItemsAsync(ctx, list);
            return;
        }

        list = ctx.Web.Lists.Add(new ListCreationInformation
        {
            Title = SourceListTitle,
            TemplateType = (int)ListTemplateType.GenericList,
            Url = "Lists/MigTestSource",
        });
        list.EnableVersioning = true;
        list.EnableFolderCreation = true;
        list.Update();
        await ctx.ExecuteQueryAsync();

        // Varied field types (lookup/taxonomy deliberately absent: out of scope).
        var fieldXml = new[]
        {
            "<Field Type='Text' Name='TextCol' DisplayName='Text Col' MaxLength='255' />",
            "<Field Type='Number' Name='NumberCol' DisplayName='Number Col' Decimals='2' />",
            "<Field Type='DateTime' Name='DateCol' DisplayName='Date Col' Format='DateTime' />",
            $"<Field Type='Choice' Name='ChoiceCol' DisplayName='Choice Col'><CHOICES>{string.Join("", Choices.Select(c => $"<CHOICE>{c}</CHOICE>"))}</CHOICES></Field>",
            "<Field Type='User' Name='PersonCol' DisplayName='Person Col' UserSelectionMode='PeopleOnly' />",
            "<Field Type='Note' Name='NotesCol' DisplayName='Notes Col' NumLines='6' RichText='FALSE' />",
            "<Field Type='Boolean' Name='FlagCol' DisplayName='Flag Col'><Default>0</Default></Field>",
            "<Field Type='Currency' Name='MoneyCol' DisplayName='Money Col' LCID='1033' />",
            "<Field Type='URL' Name='LinkCol' DisplayName='Link Col' Format='Hyperlink' />",
        };
        foreach (var xml in fieldXml)
            list.Fields.AddFieldAsXml(xml, true, AddFieldOptions.AddFieldInternalNameHint);
        await ctx.ExecuteQueryAsync();

        await EnsureExtrasAsync(ctx, list, lookupListId);
        await ReseedListItemsAsync(ctx, list);
    }

    /// <summary>
    /// Idempotently adds the newer test features to the source list: the
    /// lookup column, column formatting on NumberCol, and a formatted
    /// filtered custom view. Safe on both fresh and retention-surviving lists.
    /// </summary>
    private static async Task EnsureExtrasAsync(ClientContext ctx, List list, Guid lookupListId)
    {
        ctx.Load(list.Fields, fs => fs.Include(f => f.InternalName));
        ctx.Load(list.Views, vs => vs.Include(v => v.Title));
        await ctx.ExecuteQueryAsync();

        if (!list.Fields.AsEnumerable().Any(f => f.InternalName == "LookupCol"))
        {
            list.Fields.AddFieldAsXml(
                $"<Field Type='Lookup' Name='LookupCol' DisplayName='Lookup Col' List='{{{lookupListId}}}' ShowField='Title' />",
                true, AddFieldOptions.AddFieldInternalNameHint);
            await ctx.ExecuteQueryAsync();
        }

        var numberCol = list.Fields.GetByInternalNameOrTitle("NumberCol");
        numberCol.CustomFormatter = NumberColFormatJson;
        numberCol.UpdateAndPushChanges(true);
        await ctx.ExecuteQueryAsync();

        if (!list.Views.AsEnumerable().Any(v => v.Title == "Flagged Items"))
        {
            var view = list.Views.Add(new ViewCreationInformation
            {
                Title = "Flagged Items",
                Query = "<Where><Eq><FieldRef Name='FlagCol' /><Value Type='Boolean'>1</Value></Eq></Where>",
                RowLimit = 50,
                Paged = true,
                ViewFields = new[] { "LinkTitle", "FlagCol", "NumberCol", "ChoiceCol" },
            });
            view.CustomFormatter = ViewFormatJson;
            view.Update();
            await ctx.ExecuteQueryAsync();
        }
    }

    private static async Task ReseedListItemsAsync(ClientContext ctx, List list)
    {
        // Lookup target ids by display value (ids drift across reprovisions).
        var lookupIdByValue = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lookupList = ctx.Web.Lists.GetByTitle(LookupTargetTitle);
        var lookupItems = lookupList.GetItems(CamlQuery.CreateAllItemsQuery(100));
        ctx.Load(lookupItems);
        await ctx.ExecuteQueryAsync();
        foreach (var li in lookupItems)
            lookupIdByValue[li.FieldValues.GetValueOrDefault("Title")?.ToString() ?? ""] = li.Id;

        // A folder with items inside, to test folder handling in plain lists.
        var folder = list.AddItem(new ListItemCreationInformation
        {
            UnderlyingObjectType = FileSystemObjectType.Folder,
            LeafName = "Bucket-1",
        });
        folder["Title"] = "Bucket-1";
        folder.Update();
        await ctx.ExecuteQueryAsync();

        ctx.Load(list.RootFolder, f => f.ServerRelativeUrl);
        await ctx.ExecuteQueryAsync();

        // Back-date the folder with a REAL user so folder metadata
        // preservation is genuinely exercised by the copy tests.
        if (SourceUsers.Count > 0)
            await BackdateFolderItemAsync(ctx, folder, SourceUsers[0],
                new DateTime(2020, 2, 10, 14, 30, 0, DateTimeKind.Utc),
                new DateTime(2021, 7, 5, 9, 15, 0, DateTimeKind.Utc));

        // 25 deterministic items, back-dated, varied authors.
        var rng = new Random(42);
        var baseDate = new DateTime(2019, 3, 1, 8, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 25; i++)
        {
            var inFolder = i % 5 == 4;
            var creation = new ListItemCreationInformation();
            if (inFolder)
                creation.FolderUrl = $"{list.RootFolder.ServerRelativeUrl}/Bucket-1";
            var item = list.AddItem(creation);

            var created = baseDate.AddDays(rng.Next(0, 1800)).AddMinutes(rng.Next(0, 1440));
            var modified = created.AddDays(rng.Next(0, 90)).AddMinutes(rng.Next(0, 600));
            var user = SourceUsers.Count > 0 ? SourceUsers[i % SourceUsers.Count] : default;

            item["Title"] = $"Item {i + 1:D2} ({(inFolder ? "in folder" : "root")})";
            item["TextCol"] = $"Text value {rng.Next(1000, 9999)}";
            item["NumberCol"] = Math.Round(rng.NextDouble() * 1000, 2);
            item["DateCol"] = created.AddDays(-rng.Next(10, 500)).ToLocalTime();
            item["ChoiceCol"] = Choices[rng.Next(Choices.Length)];
            item["NotesCol"] = $"Multi-line note for item {i + 1}.\nSecond line {rng.Next(100)}.";
            item["FlagCol"] = i % 3 == 0;
            item["MoneyCol"] = Math.Round(rng.NextDouble() * 10000, 2);
            item["LinkCol"] = new FieldUrlValue { Url = $"https://example.com/{i + 1}", Description = $"Link {i + 1}" };
            var lookupValue = LookupValues[i % LookupValues.Length];
            if (lookupIdByValue.TryGetValue(lookupValue, out var lookupId))
                item["LookupCol"] = new FieldLookupValue { LookupId = lookupId };
            if (user.Id > 0)
            {
                item["PersonCol"] = new FieldUserValue { LookupId = user.Id };
                item["Author"] = new FieldUserValue { LookupId = user.Id };
                item["Editor"] = new FieldUserValue { LookupId = SourceUsers[(i + 1) % SourceUsers.Count].Id };
            }
            item["Created"] = DateTime.SpecifyKind(created, DateTimeKind.Utc).ToLocalTime();
            item["Modified"] = DateTime.SpecifyKind(modified, DateTimeKind.Utc).ToLocalTime();
            item.UpdateOverwriteVersion();
        }
        await ctx.ExecuteQueryAsync();

        // Attachments on two items (attachment copy is part of the engine contract).
        var attachQuery = list.GetItems(CamlQuery.CreateAllItemsQuery(5));
        ctx.Load(attachQuery);
        await ctx.ExecuteQueryAsync();
        foreach (var (item, n) in attachQuery.AsEnumerable()
                     .Where(i => i.FileSystemObjectType != FileSystemObjectType.Folder)
                     .Take(2).Select((x, n) => (x, n)))
        {
            var created = item["Created"];
            var modified = item["Modified"];
            item.AttachmentFiles.Add(new AttachmentCreationInformation
            {
                FileName = $"note-{n + 1}.txt",
                ContentStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"attachment {n + 1} content")),
            });
            await ctx.ExecuteQueryAsync();
            // Re-apply the back-dates the attachment write just clobbered.
            item["Created"] = created;
            item["Modified"] = modified;
            item.UpdateOverwriteVersion();
            await ctx.ExecuteQueryAsync();
        }

        Console.WriteLine($"  source list '{SourceListTitle}' provisioned: 25 items (+1 folder, 2 attachments), back-dated, authored");
    }

    public static async Task RecreateSourceLibraryAsync(SpConnection site)
    {
        using var ctx = site.CreateContext();
        var deleted = await DeleteIfExistsAsync(ctx, SourceLibTitle);

        List list;
        if (!deleted)
        {
            list = ctx.Web.Lists.GetByTitle(SourceLibTitle);
        }
        else
        {
            list = ctx.Web.Lists.Add(new ListCreationInformation
            {
                Title = SourceLibTitle,
                TemplateType = (int)ListTemplateType.DocumentLibrary,
                Url = "MigTestSrcLib",
            });
            list.EnableVersioning = true;
            list.Update();
            list.Fields.AddFieldAsXml(
                "<Field Type='Text' Name='DocCategory' DisplayName='Doc Category' MaxLength='100' />",
                true, AddFieldOptions.AddFieldInternalNameHint);
            await ctx.ExecuteQueryAsync();
        }

        ctx.Load(list.RootFolder, f => f.ServerRelativeUrl);
        await ctx.ExecuteQueryAsync();
        var root = list.RootFolder.ServerRelativeUrl;

        // Folder tree, back-dated with real users.
        var folderDate = new DateTime(2020, 9, 12, 11, 0, 0, DateTimeKind.Utc);
        var fi = 0;
        foreach (var path in new[] { "Folder-A", "Folder-A/Sub-1", "Folder-B" })
        {
            var parent = path.Contains('/') ? $"{root}/{path[..path.LastIndexOf('/')]}" : root;
            var added = ctx.Web.GetFolderByServerRelativeUrl(parent).Folders.Add($"{root}/{path}");
            await ctx.ExecuteQueryAsync();
            if (SourceUsers.Count > 0)
            {
                var item = added.ListItemAllFields;
                ctx.Load(item, i => i.Id);
                await ctx.ExecuteQueryAsync();
                await BackdateFolderItemAsync(ctx, item, SourceUsers[fi % SourceUsers.Count],
                    folderDate.AddDays(fi * 17), folderDate.AddDays(fi * 17 + 40));
            }
            fi++;
        }

        // 12 deterministic files in varied locations and sizes (1 KB - 600 KB).
        var rng = new Random(1234);
        var baseDate = new DateTime(2020, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        var locations = new[] { "", "Folder-A", "Folder-A/Sub-1", "Folder-B" };
        for (var i = 0; i < 12; i++)
        {
            var size = i switch { < 4 => 1024 + rng.Next(2048), < 8 => 50_000 + rng.Next(100_000), _ => 300_000 + rng.Next(300_000) };
            var bytes = new byte[size];
            rng.NextBytes(bytes);

            var location = locations[i % locations.Length];
            var folderUrl = string.IsNullOrEmpty(location) ? root : $"{root}/{location}";
            var file = ctx.Web.GetFolderByServerRelativeUrl(folderUrl).Files.Add(new FileCreationInformation
            {
                Url = $"doc-{i + 1:D2}.bin",
                ContentStream = new MemoryStream(bytes),
                Overwrite = true,
            });
            ctx.Load(file, f => f.ListItemAllFields.Id);
            await ctx.ExecuteQueryAsync();

            var created = baseDate.AddDays(rng.Next(0, 1400));
            var modified = created.AddDays(rng.Next(0, 60));
            var user = SourceUsers.Count > 0 ? SourceUsers[i % SourceUsers.Count] : default;
            var item = file.ListItemAllFields;
            item["DocCategory"] = $"Category-{(char)('A' + i % 3)}";
            if (user.Id > 0)
            {
                item["Author"] = new FieldUserValue { LookupId = user.Id };
                item["Editor"] = new FieldUserValue { LookupId = user.Id };
            }
            item["Created"] = DateTime.SpecifyKind(created, DateTimeKind.Utc).ToLocalTime();
            item["Modified"] = DateTime.SpecifyKind(modified, DateTimeKind.Utc).ToLocalTime();
            item.UpdateOverwriteVersion();
            await ctx.ExecuteQueryAsync();
        }
        Console.WriteLine($"  source library '{SourceLibTitle}' provisioned: 12 files in 4 locations, back-dated");
    }

    /// <summary>
    /// Back-dates a folder list item with a real user. Single
    /// UpdateOverwriteVersion with users + dates (lab-verified on this
    /// tenant; the two-step form-update pattern leaves Editor stamped as the
    /// connecting app).
    /// </summary>
    private static async Task BackdateFolderItemAsync(ClientContext ctx, ListItem folderItem,
        (int Id, string Login, string Email, string Title) user, DateTime createdUtc, DateTime modifiedUtc)
    {
        folderItem["Author"] = new FieldUserValue { LookupId = user.Id };
        folderItem["Editor"] = new FieldUserValue { LookupId = user.Id };
        folderItem["Created"] = DateTime.SpecifyKind(createdUtc, DateTimeKind.Utc).ToLocalTime();
        folderItem["Modified"] = DateTime.SpecifyKind(modifiedUtc, DateTimeKind.Utc).ToLocalTime();
        folderItem.UpdateOverwriteVersion();
        await ctx.ExecuteQueryAsync();
    }

    /// <summary>
    /// Removes a list, or (when a retention policy blocks deletion, as on
    /// this tenant) empties it so tests can reuse it. Returns true when the
    /// list is fully gone, false when it survives but is empty.
    /// </summary>
    public static async Task<bool> DeleteIfExistsAsync(ClientContext ctx, string listTitle)
    {
        var lists = ctx.Web.Lists;
        ctx.Load(lists, ls => ls.Include(l => l.Title));
        await ctx.ExecuteQueryAsync();
        var existing = lists.AsEnumerable().FirstOrDefault(l => l.Title.Equals(listTitle, StringComparison.OrdinalIgnoreCase));
        if (existing == null) return true;

        try
        {
            existing.DeleteObject();
            await ctx.ExecuteQueryAsync();
            return true;
        }
        catch (ServerException ex) when (ex.Message.Contains("retention", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("hold", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  '{listTitle}' is under retention; clearing items instead of deleting");
            await ClearListAsync(ctx, existing);
            return false;
        }
    }

    /// <summary>Deletes every item in a list (deepest paths first, so folder contents go before folders).</summary>
    public static async Task ClearListAsync(ClientContext ctx, List list)
    {
        while (true)
        {
            var query = new CamlQuery { ViewXml = "<View Scope='RecursiveAll'><RowLimit>200</RowLimit></View>" };
            var page = list.GetItems(query);
            ctx.Load(page, p => p.Include(i => i.Id, i => i.FileSystemObjectType));
            ctx.Load(page, p => p.Include(i => i["FileRef"]));
            await ctx.ExecuteQueryAsync();
            if (page.Count == 0) return;

            var ordered = page.AsEnumerable()
                .OrderByDescending(i => ((string)i["FileRef"]).Count(c => c == '/'))
                .ToList();
            foreach (var item in ordered)
                item.DeleteObject();
            await ctx.ExecuteQueryAsync();
        }
    }
}
