using CleverPoint.Migrator.Core.Http;

namespace CleverPoint.Migrator.Core.Reports.Definitions;

/// <summary>Lists whose forms were replaced with Power Apps or an SPFx form customizer.</summary>
public sealed class CustomizedListsReport : ReportDefinition
{
    public override string Id => "customized-lists";
    public override string Title => "Customized list forms";
    public override string Group => "Discovery";
    public override string Summary => "Lists whose forms use Power Apps or an SPFx form customizer. Those forms do not come along when the list is copied.";

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("customizedOnly", "bool", true, "true = only lists with customized forms. false = every list, with its form type."),
        new ReportOption("includeSubsites", "bool", false, "Also scan every subsite of each site."),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("lists", "Lists",
            Text("webUrl"), Text("listTitle"), Text("listUrl"), Text("listType"), Text("formType"), Number("itemCount"),
            Text("newFormUrl"), Text("editFormUrl"), Text("displayFormUrl"), Text("spfxNewForm"), Text("spfxEditForm"), Text("spfxDisplayForm")),
    };

    public override async Task RunAsync(ReportContext context)
    {
        var customizedOnly = context.Options.Bool("customizedOnly");
        var rest = context.Connection.Rest;
        foreach (var web in await WebWalker.WebsAsync(context, context.Options.Bool("includeSubsites")))
        {
            context.Phase(web);
            var lists = await rest.GetAllAsync(
                $"{web}/_api/web/lists?$select=Id,Title,BaseType,BaseTemplate,ItemCount,DefaultNewFormUrl,DefaultEditFormUrl,DefaultDisplayFormUrl," +
                "RootFolder/ServerRelativeUrl&$expand=RootFolder&$filter=Hidden eq false&$top=500", context.Cancel);
            foreach (var list in lists)
            {
                context.Cancel.ThrowIfCancellationRequested();
                var id = list.Str("Id");
                var newForm = list.Str("DefaultNewFormUrl");
                var editForm = list.Str("DefaultEditFormUrl");
                var formType = "Default";
                string spfxNew = "", spfxEdit = "", spfxDisplay = "";

                if (newForm.Contains("PowerApps", StringComparison.OrdinalIgnoreCase) || editForm.Contains("PowerApps", StringComparison.OrdinalIgnoreCase))
                    formType = "PowerApps";

                // A Power Apps form that kept the default form URLs still leaves its mark in the list's properties.
                if (formType == "Default")
                {
                    try
                    {
                        using var properties = await rest.GetJsonAsync($"{web}/_api/web/lists(guid'{id}')/RootFolder/Properties");
                        var text = properties.RootElement.GetRawText();
                        if (text.Contains("PowerAppsFormProperties", StringComparison.OrdinalIgnoreCase) || text.Contains("PowerAppFormProperties", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("_PowerAppsId_", StringComparison.OrdinalIgnoreCase) || text.Contains("PowerAppsFormId", StringComparison.OrdinalIgnoreCase))
                            formType = "PowerApps";
                    }
                    catch (SpRestException) { /* a list whose properties cannot be read is treated as not customized this way */ }
                }

                // An SPFx form customizer is set per content type.
                if (formType == "Default")
                {
                    try
                    {
                        var contentTypes = await rest.GetAllAsync(
                            $"{web}/_api/web/lists(guid'{id}')/ContentTypes?$select=Name,NewFormClientSideComponentId,EditFormClientSideComponentId,DisplayFormClientSideComponentId",
                            context.Cancel);
                        foreach (var contentType in contentTypes)
                        {
                            if (spfxNew.Length == 0) spfxNew = contentType.Str("NewFormClientSideComponentId");
                            if (spfxEdit.Length == 0) spfxEdit = contentType.Str("EditFormClientSideComponentId");
                            if (spfxDisplay.Length == 0) spfxDisplay = contentType.Str("DisplayFormClientSideComponentId");
                        }
                        if (spfxNew.Length + spfxEdit.Length + spfxDisplay.Length > 0) formType = "SPFx";
                    }
                    catch (SpRestException) { /* same as above */ }
                }

                context.Count("lists");
                if (formType == "PowerApps") context.Count("powerApps");
                if (formType == "SPFx") context.Count("spfx");
                if (formType == "Default" && customizedOnly) continue;
                context.Write("lists", new Dictionary<string, object?>
                {
                    ["webUrl"] = web,
                    ["listTitle"] = list.Str("Title"),
                    ["listUrl"] = ListTypes.Url(web, list.Child("RootFolder").Str("ServerRelativeUrl")),
                    ["listType"] = ListTypes.Name(list.Num("BaseTemplate")),
                    ["formType"] = formType,
                    ["itemCount"] = list.Num("ItemCount"),
                    ["newFormUrl"] = newForm,
                    ["editFormUrl"] = editForm,
                    ["displayFormUrl"] = list.Str("DefaultDisplayFormUrl"),
                    ["spfxNewForm"] = spfxNew,
                    ["spfxEditForm"] = spfxEdit,
                    ["spfxDisplayForm"] = spfxDisplay,
                });
            }
        }
    }
}

/// <summary>Columns people added, as opposed to the ones SharePoint ships.</summary>
public sealed class CustomColumnsReport : ReportDefinition
{
    public override string Id => "custom-columns";
    public override string Title => "Custom columns";
    public override string Group => "Discovery";
    public override string Summary => "Columns added to lists and libraries, leaving out the ones SharePoint ships. Shows the schema a migration has to carry.";

    public override IReadOnlyList<ReportOption> Options => new[]
    {
        new ReportOption("includeSubsites", "bool", false, "Also scan every subsite of each site."),
    };

    public override IReadOnlyList<ReportTable> Tables => new[]
    {
        Table("columns", "Columns",
            Text("webUrl"), Text("listTitle"), Text("listUrl"), Number("itemCount"), Text("columnName"), Text("internalName"), Text("fieldType"),
            Text("group"), Flag("required"), Date("listCreated"), Date("listModified")),
    };

    public override async Task RunAsync(ReportContext context)
    {
        var rest = context.Connection.Rest;
        foreach (var web in await WebWalker.WebsAsync(context, context.Options.Bool("includeSubsites")))
        {
            context.Phase(web);
            var lists = await rest.GetAllAsync(
                $"{web}/_api/web/lists?$select=Id,Title,ItemCount,Created,LastItemModifiedDate,RootFolder/ServerRelativeUrl&$expand=RootFolder&$filter=Hidden eq false&$top=500",
                context.Cancel);
            foreach (var list in lists)
            {
                context.Cancel.ThrowIfCancellationRequested();
                List<System.Text.Json.JsonElement> fields;
                try
                {
                    fields = await rest.GetAllAsync(
                        $"{web}/_api/web/lists(guid'{list.Str("Id")}')/fields?$filter=Hidden eq false&$select=Title,InternalName,TypeAsString,Group,CanBeDeleted,FromBaseType,Required&$top=1000",
                        context.Cancel);
                }
                catch (Exception ex) when (Gone.Is(ex))
                {
                    context.Count(Gone.Counter);
                    continue;
                }
                var custom = 0;
                foreach (var field in fields)
                {
                    if (!BuiltInColumns.IsCustomGroup(field.Str("Group")) || field.Bool("FromBaseType") || !field.Bool("CanBeDeleted")) continue;
                    var internalName = field.Str("InternalName");
                    if (BuiltInColumns.Names.Contains(internalName)) continue;
                    context.Write("columns", new Dictionary<string, object?>
                    {
                        ["webUrl"] = web,
                        ["listTitle"] = list.Str("Title"),
                        ["listUrl"] = ListTypes.Url(web, list.Child("RootFolder").Str("ServerRelativeUrl")),
                        ["itemCount"] = list.Num("ItemCount"),
                        ["columnName"] = field.Str("Title"),
                        ["internalName"] = internalName,
                        ["fieldType"] = field.Str("TypeAsString"),
                        ["group"] = field.Str("Group"),
                        ["required"] = field.Bool("Required") ? true : null,
                        ["listCreated"] = list.When("Created"),
                        ["listModified"] = list.When("LastItemModifiedDate"),
                    });
                    custom++;
                }
                context.Count("listsScanned");
                context.Count("customColumns", custom);
                if (custom > 0) context.Count("listsWithCustomColumns");
            }
        }
    }
}

/// <summary>What tells a column SharePoint ships from one somebody added.</summary>
public static class BuiltInColumns
{
    private static readonly HashSet<string> BuiltInGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "_Hidden", "Base Columns", "Content Feedback", "Core Contact and Calendar Columns", "Core Document Columns",
        "Core Task and Issue Columns", "Display Template Columns", "Document and Record Management Columns", "Enterprise Keywords Group",
        "Extended Columns", "JavaScript Display Template", "Page Layout Columns", "Publishing Columns", "Ratings", "Reports", "Status Indicators",
    };

    /// <summary>A column counts as custom when its group is "Custom Columns" or any group SharePoint does not ship.</summary>
    public static bool IsCustomGroup(string group) => group.Length > 0 && !BuiltInGroups.Contains(group);

    /// <summary>Columns SharePoint files under "Custom Columns" although it created them itself.</summary>
    public static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        // Core
        "ID", "Title", "Created", "Modified", "Author", "Editor", "FileLeafRef", "FileDirRef", "FileRef", "File_x0020_Type",
        "_CopySource", "_CheckinComment", "_UIVersionString", "LinkFilenameNoMenu", "LinkFilename", "LinkFilename2", "DocIcon",
        "FileSizeDisplay", "Edit", "SelectTitle", "ItemChildCount", "FolderChildCount",
        // Compliance, labels, social, apps
        "_ComplianceFlags", "_ComplianceTag", "_ComplianceTagWrittenTime", "_ComplianceTagUserId", "_IsRecord", "_DisplayName",
        "ComplianceAssetId", "_CommentCount", "_LikeCount", "_CommentFlags", "AppAuthor", "AppEditor",
        "ParentVersionString", "ParentLeafName", "CheckoutUser", "CheckedOutTitle",
        "_dlc_DocId", "_dlc_DocIdUrl", "_dlc_DocIdPersistId", "_ColorTag", "_ColorHex",
        "ContentType", "ContentTypeId", "SMTotalSize", "SMLastModifiedDate", "SMTotalFileStreamSize", "SMTotalFileCount",
        "ProgId", "ScopeId", "VirusStatus", "_ShortcutUrl", "_ShortcutSiteId", "_ShortcutWebId", "_ShortcutUniqueId",
        "AccessPolicy", "BSN", "SyncClientId", "TemplateUrl", "A2ODMountCount", "Restricted", "OriginatorId", "NoExecute",
        "ContentVersion", "ComplianceFlags", "MediaServiceMetadata", "MediaServiceFastMetadata", "MediaServiceAutoTags", "MediaServiceOCR",
        "MediaServiceGenerationTime", "MediaServiceEventHashCode", "MediaServiceDateTaken", "MediaServiceAutoKeyPoints",
        "MediaServiceKeyPoints", "MediaServiceLocation", "MediaLengthInSeconds", "MediaServiceSearchProperties",
        "LCF", "TaxCatchAll", "TaxCatchAllLabel", "_ExtendedDescription",
        // Calendar
        "Location", "Geolocation", "EventDate", "EndDate", "Duration", "fAllDayEvent", "fRecurrence", "RecurrenceID", "RecurrenceData",
        "TimeZone", "XMLTZone", "MasterSeriesItemID", "Workspace", "WorkspaceLink", "EventType", "UID", "EventCanceled",
        "ParticipantsPicker", "Category", "Facilities", "FreeBusy", "Overbook", "BannerUrl", "Description",
        // Site pages
        "WikiField", "CanvasContent1", "BannerImageUrl", "PromotedState", "FirstPublishedDate", "LayoutWebpartsContent",
        "_OriginalSourceUrl", "_OriginalSourceSiteId", "_OriginalSourceWebId", "_OriginalSourceListId", "_OriginalSourceItemId",
        "_AuthorBylineId", "PageLayoutType", "_TopicHeader", "_SPSitePageFlags", "_SPCallToAction",
        // Tasks, contacts, announcements
        "StartDate", "DueDate", "PercentComplete", "Priority", "TaskStatus", "AssignedTo", "Body", "Predecessors", "TaskGroup",
        "TaskOutcome", "RelatedItems", "FirstName", "FullName", "Company", "JobTitle", "WorkPhone", "HomePhone", "CellPhone",
        "WorkFax", "WorkAddress", "WorkCity", "WorkState", "WorkZip", "WorkCountry", "WebPage", "Comments", "Expires",
        // Discussions, surveys, issues
        "DiscussionTitle", "DiscussionLastUpdated", "Threading", "ThreadIndex", "ThreadTopic", "ParentFolderId", "BodyAndMore",
        "CorrectBodyToShow", "TrimmedBody", "IsRootPost", "ReplyCount", "ParentItemID", "ParentItemEditor", "LastReplyBy",
        "IsQuestion", "BestAnswerId", "IsFeatured", "Completed", "IssueStatus", "V3Comments", "RelatedIssues",
        // Libraries and what many templates share
        "SharedWithUsers", "SharedWithDetails", "SharingHintHash", "_VirusInfo", "_VirusVendorID", "_VirusStatus", "CheckInComment",
        "Attachments", "Order", "GUID", "WorkflowVersion", "WorkflowInstanceID", "SelectFilename", "InstanceID", "UniqueId", "MetaInfo",
        "owshiddenversion", "_Level", "_IsCurrentVersion", "Last_x0020_Modified", "Created_x0020_Date", "FSObjType", "SortBehavior",
        "PermMask", "PrincipalCount", "ParentUniqueId", "StreamHash", "_HasCopyDestinations", "_ModerationStatus", "_ModerationComments",
    };
}
