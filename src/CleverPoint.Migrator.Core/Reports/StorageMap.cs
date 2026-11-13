using System.Text.Json;

namespace CleverPoint.Migrator.Core.Reports;

/// <summary>
/// One box of the storage map: a tenant, a site, a library, a folder or a file, with what it takes in
/// storage. For everything but a file the numbers include all that is inside it.
/// </summary>
public sealed class MapNode
{
    public string Name { get; set; } = "";
    /// <summary>all | group | site | web | library | folder | file | others</summary>
    public string Kind { get; set; } = "folder";
    public string? Url { get; set; }
    public string? Extension { get; set; }
    public long Total { get; set; }
    public long Current { get; set; }
    public long Versions { get; set; }
    public long Files { get; set; }
    /// <summary>What the files are (one of <see cref="FileKinds.All"/>), for a file or a group of files of one kind. Null for a mix or when not known.</summary>
    public string? FileKind { get; set; }
    /// <summary>The kind that takes the most storage here: the node's own kind, or for a folder the largest among what is in it.</summary>
    public string? MainKind { get; set; }
    public MapNode? Parent { get; set; }
    public List<MapNode> Children { get; } = new();

    /// <summary>The share of this node's storage that is older versions, 0 to 1.</summary>
    public double VersionShare => Total > 0 ? Math.Clamp(Versions / (double)Total, 0, 1) : 0;
    public bool CanOpen => Children.Count > 0;

    public MapNode Add(MapNode child)
    {
        child.Parent = this;
        Children.Add(child);
        return child;
    }

    /// <summary>From the top of the map down to this node.</summary>
    public List<MapNode> Path()
    {
        var path = new List<MapNode>();
        for (var node = this; node is not null; node = node.Parent) path.Insert(0, node);
        return path;
    }
}

/// <summary>
/// A node's rectangle on the map. Level 1 is a direct child of the node being looked at.
/// A <see cref="Container"/> has the rectangles of its contents drawn over it.
/// </summary>
public readonly record struct MapRect(MapNode Node, double X, double Y, double Width, double Height, int Level, int Group, bool Container);

/// <summary>
/// Builds the storage map of a report run and lays it out as a treemap: every box has an area in
/// proportion to its storage, boxes inside a box are its contents.
/// </summary>
public static class StorageMap
{
    /// <summary>The reports whose results can be shown as a map.</summary>
    public static bool Supports(string reportId) => reportId is "storage" or "sites" or "onedrives";

    public static MapNode Build(string reportId, ReportStore store) => reportId switch
    {
        "storage" => FromStorage(store),
        "sites" => FromSites(store, "sites", "Sites", groupByType: true),
        "onedrives" => FromSites(store, "onedrives", "OneDrives", groupByType: false),
        _ => new MapNode { Name = "Nothing to show", Kind = "all" },
    };

    /// <summary>A run with more folders than this has its smallest folders left out of the map.</summary>
    private const int LeaveOutAbove = 150_000;
    /// <summary>Then a folder is kept when it takes at least one part in this many of everything scanned.</summary>
    private const int SmallestShare = 100_000;

    private const string OtherStorage = "Lists, attachments and system storage";

    /// <summary>The kind given to storage that is not files in a library: the recycle bin, lists, what SharePoint keeps for itself.</summary>
    public const string SystemKind = "Recycle bin and system";

    /// <summary>The reports whose results can also be shown by kind of file.</summary>
    public static bool SupportsKinds(string reportId) => reportId is "storage";

    /// <summary>
    /// The storage of a run by what the files are: the kind of file (PDF, video, images), then the
    /// file type, then the sites that hold files of that type.
    /// </summary>
    public static MapNode BuildKinds(ReportStore store)
    {
        var root = new MapNode { Name = "All files", Kind = "kinds" };
        var kinds = new Dictionary<string, MapNode>(StringComparer.OrdinalIgnoreCase);
        var types = new Dictionary<string, MapNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in store.ReadRows("types"))
        {
            var total = Number(row, "totalBytes");
            if (total <= 0) continue;
            var extension = Text(row, "extension");
            var kind = FileKinds.Of(extension);
            if (!kinds.TryGetValue(kind, out var kindNode)) kinds[kind] = kindNode = root.Add(new MapNode { Name = kind, Kind = "kind", FileKind = kind });
            if (!types.TryGetValue(extension, out var typeNode))
                types[extension] = typeNode = kindNode.Add(new MapNode
                {
                    Name = extension.StartsWith('(') ? "No extension" : "." + extension, Kind = "type", Extension = extension, FileKind = kind,
                });
            var site = Text(row, "site");
            typeNode.Add(new MapNode
            {
                Name = LastPart(site), Kind = "site", Url = site, Extension = extension, FileKind = kind, Total = total,
                Current = Number(row, "currentBytes"), Versions = Number(row, "versionBytes"), Files = Number(row, "files"),
            });
        }
        RollUp(root);
        MarkKinds(root);
        return root;
    }

    private static readonly string[] Slots = FileKinds.All.Append(SystemKind).ToArray();

    /// <summary>
    /// Gives every node the kind that takes the most of its storage, so that a box too small to show
    /// what is in it still has a telling color. Returns the node's storage per kind; the last number
    /// is what has no known kind.
    /// </summary>
    private static long[]? MarkKinds(MapNode node)
    {
        if (node.Children.Count == 0)
        {
            node.MainKind = node.FileKind;
            return null;
        }
        var totals = new long[Slots.Length + 1];
        foreach (var child in node.Children)
        {
            var inner = MarkKinds(child);
            if (inner is null)
            {
                var slot = Array.FindIndex(Slots, k => k.Equals(child.FileKind, StringComparison.OrdinalIgnoreCase));
                totals[slot < 0 ? Slots.Length : slot] += child.Total;
            }
            else
            {
                for (var i = 0; i < totals.Length; i++) totals[i] += inner[i];
            }
        }
        var best = 0;
        for (var i = 1; i < totals.Length; i++)
            if (totals[i] > totals[best]) best = i;
        node.MainKind = node.FileKind ?? (best < Slots.Length && totals[best] > 0 ? Slots[best] : null);
        return totals;
    }

    // ---- from the sites and onedrives reports: one box per site, sized by the storage the tenant reports ----

    private static MapNode FromSites(ReportStore store, string table, string title, bool groupByType)
    {
        var root = new MapNode { Name = title, Kind = "all" };
        var groups = new Dictionary<string, MapNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in store.ReadRows(table))
        {
            var bytes = Number(row, "storageUsedBytes");
            if (bytes <= 0) continue;
            var url = Text(row, "url");
            var name = Text(row, "title") is { Length: > 0 } siteTitle ? siteTitle : url;
            if (table == "onedrives" && Text(row, "ownerName") is { Length: > 0 } owner) name = owner;
            var parent = root;
            if (groupByType)
            {
                var type = Text(row, "siteType") is { Length: > 0 } siteType ? siteType : "Other";
                if (!groups.TryGetValue(type, out parent!)) groups[type] = parent = root.Add(new MapNode { Name = type + "s", Kind = "group" });
            }
            // The tenant's own figure has no split into versions, so all of it counts as current.
            parent.Add(new MapNode { Name = name, Kind = "site", Url = url, Total = bytes, Current = bytes, Files = Number(row, "fileCount") });
        }
        RollUp(root);
        return root;
    }

    // ---- from the storage report: site, subsite, library, folders, and the largest files ----

    private static MapNode FromStorage(ReportStore store)
    {
        var root = new MapNode { Name = "All sites", Kind = "all" };
        var sites = new Dictionary<string, MapNode>(StringComparer.OrdinalIgnoreCase);
        var webs = new Dictionary<string, MapNode>(StringComparer.OrdinalIgnoreCase);
        // Folder nodes by "web|library|path".
        var folders = new Dictionary<string, MapNode>(StringComparer.OrdinalIgnoreCase);
        var own = new Dictionary<MapNode, (long Files, long Bytes)>();

        // A very large run has more folders than a map can show or memory should hold. Then a folder
        // too small to be seen even as a dot is left out, and what it holds is kept as one box per
        // kind of file in the nearest folder above it. Every total stays what it was.
        var counts = store.RowCounts();
        var hasKinds = counts.GetValueOrDefault("folderKinds") > 0;
        long cutoff = 0;
        if (counts.GetValueOrDefault("folders") > LeaveOutAbove)
        {
            long everything = 0;
            foreach (var row in store.ReadRows("folders"))
                if (Number(row, "depth") == 0) everything += Number(row, "totalBytes");
            cutoff = everything / SmallestShare;
        }
        var leftOut = new Dictionary<(MapNode Above, string Kind), (long Files, long Total, long Current, long Versions, long Folders)>();

        MapNode Web(string site, string web)
        {
            if (!sites.TryGetValue(site, out var siteNode))
                sites[site] = siteNode = root.Add(new MapNode { Name = LastPart(site), Kind = "site", Url = site });
            if (web.Equals(site, StringComparison.OrdinalIgnoreCase)) return siteNode;
            if (!webs.TryGetValue(web, out var webNode))
                webs[web] = webNode = siteNode.Add(new MapNode
                {
                    Name = web.StartsWith(site + "/", StringComparison.OrdinalIgnoreCase) ? web[(site.Length + 1)..] : LastPart(web), Kind = "web", Url = web,
                });
            return webNode;
        }

        // Rows arrive library by library with paths in order, so a folder's parent is always there first.
        foreach (var row in store.ReadRows("folders"))
        {
            var site = Text(row, "site");
            var web = Text(row, "webUrl");
            var library = Text(row, "library");
            var path = Text(row, "path");
            var key = $"{web}|{library}|";
            if (path.Length > 0 && Number(row, "totalBytes") < cutoff)
            {
                // With kinds recorded, what it holds is added up from them below. Without, it is added
                // here, once, for the topmost folder left out (the ones inside it are part of its total).
                if (!hasKinds && folders.TryGetValue(key + (path.Contains('/') ? path[..path.LastIndexOf('/')] : ""), out var above))
                {
                    var sum = leftOut.GetValueOrDefault((above, ""));
                    leftOut[(above, "")] = (sum.Files + Number(row, "files"), sum.Total + Number(row, "totalBytes"), sum.Current + Number(row, "currentBytes"),
                        sum.Versions + Number(row, "versionBytes"), sum.Folders + 1);
                }
                continue;
            }
            var node = new MapNode
            {
                Name = path.Length == 0 ? library : path[(path.LastIndexOf('/') + 1)..],
                Kind = path.Length == 0 ? "library" : "folder",
                Url = Text(row, "folderUrl"),
                Total = Number(row, "totalBytes"),
                Current = Number(row, "currentBytes"),
                Versions = Number(row, "versionBytes"),
                Files = Number(row, "files"),
            };
            var parent = path.Length == 0
                ? Web(site, web)
                : folders.GetValueOrDefault(key + (path.Contains('/') ? path[..path.LastIndexOf('/')] : "")) ?? Web(site, web);
            parent.Add(node);
            folders[key + path] = node;
            own[node] = (Number(row, "ownFiles"), Number(row, "ownBytes"));
        }

        // The largest files become boxes of their own inside their folder.
        var listed = new Dictionary<MapNode, (long Files, long Bytes, long Current, long Versions)>();
        var listedOfKind = new Dictionary<(MapNode Folder, string Kind), (long Files, long Bytes, long Current, long Versions)>();
        foreach (var row in store.ReadRows("files"))
        {
            if (!folders.TryGetValue($"{Text(row, "webUrl")}|{Text(row, "library")}|{Text(row, "folder")}", out var folder)) continue;
            var extension = Text(row, "extension");
            var file = folder.Add(new MapNode
            {
                Name = Text(row, "fileName"), Kind = "file", Url = Text(row, "fileUrl"), Extension = extension, FileKind = FileKinds.Of(extension),
                Total = Number(row, "totalBytes"), Current = Number(row, "currentBytes"), Versions = Number(row, "versionBytes"), Files = 1,
            });
            var sum = listed.GetValueOrDefault(folder);
            listed[folder] = (sum.Files + 1, sum.Bytes + file.Total, sum.Current + file.Current, sum.Versions + file.Versions);
            var ofKind = listedOfKind.GetValueOrDefault((folder, file.FileKind));
            listedOfKind[(folder, file.FileKind)] = (ofKind.Files + 1, ofKind.Bytes + file.Total, ofKind.Current + file.Current, ofKind.Versions + file.Versions);
        }

        // What each folder holds directly, by kind of file. A run made before kinds were recorded has none of this.
        var kindsOf = new Dictionary<MapNode, List<(string Kind, long Files, long Total, long Current, long Versions)>>();
        foreach (var row in store.ReadRows("folderKinds"))
        {
            var key = $"{Text(row, "webUrl")}|{Text(row, "library")}|";
            var path = Text(row, "path");
            if (!folders.TryGetValue(key + path, out var folder))
            {
                // The folder was left out: its files count toward the nearest folder above that was kept.
                MapNode? above = null;
                while (path.Length > 0 && above is null)
                {
                    path = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
                    folders.TryGetValue(key + path, out above);
                }
                if (above is null) continue;
                var kind = Text(row, "kind");
                var sum = leftOut.GetValueOrDefault((above, kind));
                leftOut[(above, kind)] = (sum.Files + Number(row, "files"), sum.Total + Number(row, "totalBytes"), sum.Current + Number(row, "currentBytes"),
                    sum.Versions + Number(row, "versionBytes"), 0);
                continue;
            }
            if (!kindsOf.TryGetValue(folder, out var list)) kindsOf[folder] = list = new();
            list.Add((Text(row, "kind"), Number(row, "files"), Number(row, "totalBytes"), Number(row, "currentBytes"), Number(row, "versionBytes")));
        }

        foreach (var ((above, kind), sum) in leftOut)
        {
            if (sum.Total <= 0) continue;
            above.Add(new MapNode
            {
                Name = kind.Length == 0 ? $"{sum.Folders:N0} smaller folders" : $"{(kind == FileKinds.Other ? "Other files" : kind)} in smaller folders",
                Kind = "others", FileKind = kind.Length == 0 ? null : kind, Url = above.Url,
                Total = sum.Total, Current = Math.Min(sum.Current, sum.Total), Versions = Math.Min(sum.Versions, sum.Total), Files = sum.Files,
            });
        }

        // What a folder holds directly, beyond the files that got a box of their own: a box per kind of
        // file when the kinds are known, otherwise one box, "N other files".
        foreach (var (folder, (files, bytes)) in own)
        {
            var shown = listed.GetValueOrDefault(folder);
            if (kindsOf.TryGetValue(folder, out var kinds))
            {
                var rest = new List<(string Kind, long Files, long Total, long Current, long Versions)>();
                foreach (var kind in kinds)
                {
                    var boxed = listedOfKind.GetValueOrDefault((folder, kind.Kind));
                    var left = (kind.Kind, Files: kind.Files - boxed.Files, Total: kind.Total - boxed.Bytes,
                        Current: Math.Max(0, kind.Current - boxed.Current), Versions: Math.Max(0, kind.Versions - boxed.Versions));
                    if (left.Files > 0 && left.Total > 0) rest.Add(left);
                }
                if (rest.Count == 0) continue;
                // A folder with files of one kind and nothing else is a box of that kind by itself.
                if (rest.Count == 1 && shown.Files == 0 && folder.Children.Count == 0)
                {
                    folder.FileKind = rest[0].Kind;
                    continue;
                }
                foreach (var kind in rest)
                    folder.Add(new MapNode
                    {
                        Name = kind.Kind == FileKinds.Other ? "Other files" : kind.Kind, Kind = "others", FileKind = kind.Kind, Url = folder.Url,
                        Total = kind.Total, Current = Math.Min(kind.Current, kind.Total), Versions = Math.Min(kind.Versions, kind.Total), Files = kind.Files,
                    });
                continue;
            }
            var restFiles = files - shown.Files;
            var restBytes = bytes - shown.Bytes;
            if (restFiles <= 0 || restBytes <= 0) continue;
            var subfolders = folder.Children.Where(c => c.Kind != "file").ToList();
            // A folder with nothing but its own unlisted files is a box by itself and needs no box inside it.
            if (subfolders.Count == 0 && shown.Files == 0) continue;
            var restCurrent = Math.Max(0, folder.Current - subfolders.Sum(c => c.Current) - shown.Current);
            var restVersions = Math.Max(0, folder.Versions - subfolders.Sum(c => c.Versions) - shown.Versions);
            folder.Add(new MapNode
            {
                Name = shown.Files > 0 ? $"{restFiles:N0} other files" : $"{restFiles:N0} files here", Kind = "others", Url = folder.Url,
                Total = restBytes, Current = Math.Min(restCurrent, restBytes), Versions = Math.Min(restVersions, restBytes), Files = restFiles,
            });
        }

        // What the site uses beyond its libraries, so that a site's box is as large as SharePoint says the site is.
        var extras = new List<(MapNode Site, MapNode Node)>();
        foreach (var row in store.ReadRows("overview"))
        {
            if (!sites.TryGetValue(Text(row, "site"), out var siteNode)) continue;
            var bin = Number(row, "recycleBinBytes");
            if (bin > 0)
                extras.Add((siteNode, new MapNode { Name = "Recycle bin", Kind = "others", FileKind = SystemKind, Url = siteNode.Url + "/_layouts/15/AdminRecycleBin.aspx", Total = bin, Current = bin, Files = Number(row, "recycleBinItems") }));
            var other = Number(row, "otherBytes");
            if (other > 0)
                extras.Add((siteNode, new MapNode { Name = OtherStorage, Kind = "others", FileKind = SystemKind, Url = siteNode.Url + "/_layouts/15/storman.aspx", Total = other, Current = other }));
        }
        foreach (var (siteNode, node) in extras) siteNode.Add(node);

        // The larger lists are boxes inside that last one, when together they fit in it.
        var listsOf = new Dictionary<MapNode, List<MapNode>>();
        foreach (var row in store.ReadRows("lists"))
        {
            if (!sites.TryGetValue(Text(row, "site"), out var siteNode)) continue;
            if (!listsOf.TryGetValue(siteNode, out var found)) listsOf[siteNode] = found = new List<MapNode>();
            var bytes = Number(row, "totalBytes");
            found.Add(new MapNode { Name = Text(row, "list"), Kind = "list", FileKind = SystemKind, Url = Text(row, "listUrl"), Total = bytes, Current = bytes, Files = Number(row, "attachments") });
        }
        foreach (var (siteNode, node) in extras)
        {
            if (node.Name != OtherStorage || !listsOf.TryGetValue(siteNode, out var found)) continue;
            var inLists = found.Sum(l => l.Total);
            if (inLists > node.Total) continue;
            foreach (var list in found) node.Add(list);
            if (node.Total - inLists > 0)
                node.Add(new MapNode { Name = "Smaller lists and system storage", Kind = "others", FileKind = SystemKind, Url = node.Url, Total = node.Total - inLists, Current = node.Total - inLists });
        }

        foreach (var site in root.Children) RollUpContainers(site);
        RollUp(root, onlyThis: true);
        MarkKinds(root);
        return root;
    }

    /// <summary>Sites and subsites have no row of their own: they are the sum of what is in them.</summary>
    private static void RollUpContainers(MapNode node)
    {
        if (node.Kind is not ("site" or "web" or "all" or "group")) return;
        foreach (var child in node.Children) RollUpContainers(child);
        RollUp(node, onlyThis: true);
    }

    private static void RollUp(MapNode node, bool onlyThis = false)
    {
        if (!onlyThis) foreach (var child in node.Children.Where(c => c.Children.Count > 0)) RollUp(child);
        if (node.Children.Count == 0) return;
        node.Total = node.Children.Sum(c => c.Total);
        node.Current = node.Children.Sum(c => c.Current);
        node.Versions = node.Children.Sum(c => c.Versions);
        node.Files = node.Children.Sum(c => c.Files);
    }

    private static string LastPart(string url)
    {
        var trimmed = url.TrimEnd('/');
        var path = Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) ? Uri.UnescapeDataString(parsed.AbsolutePath).Trim('/') : trimmed;
        return path.Length == 0 ? (parsed?.Host ?? trimmed) : path[(path.LastIndexOf('/') + 1)..];
    }

    private static string Text(Dictionary<string, JsonElement> row, string name) =>
        row.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static long Number(Dictionary<string, JsonElement> row, string name) =>
        row.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number ? (value.TryGetInt64(out var whole) ? whole : (long)value.GetDouble()) : 0;

    // ---- layout ----------------------------------------------------------------------------------

    /// <summary>
    /// Lays out what is in a node as rectangles inside a canvas, going down level by level for as long
    /// as the boxes stay large enough to see. Only the boxes directly under the node keep a strip for
    /// their name; below that the contents fill their box edge to edge, so nothing is lost to frames.
    /// </summary>
    /// <param name="header">Height kept free at the top of a top-level box for its name.</param>
    /// <param name="maxRects">Stop going deeper once this many rectangles are placed.</param>
    public static List<MapRect> Layout(MapNode node, double width, double height, int levels = 14, double header = 15, int maxRects = 3200)
    {
        var rects = new List<MapRect>();
        var frontier = new List<MapRect>();
        var top = Place(node.Children, 0, 0, width, height);
        for (var i = 0; i < top.Count; i++) frontier.Add(new MapRect(top[i].Node, top[i].X, top[i].Y, top[i].W, top[i].H, 1, i, false));
        var placed = frontier.Count;

        for (var level = 1; frontier.Count > 0; level++)
        {
            var next = new List<MapRect>();
            // Largest boxes first, so that when the budget runs out it is the small ones that stay plain.
            foreach (var box in frontier.OrderByDescending(b => b.Width * b.Height))
            {
                var least = level == 1 ? 26 : 7;
                if (level >= levels || placed >= maxRects || box.Node.Children.Count == 0 || box.Width < least || box.Height < least)
                {
                    rects.Add(box);
                    continue;
                }
                var head = level == 1 && box.Width >= 60 && box.Height >= 46 ? header : 0;
                var pad = level == 1 ? 1 : 0;
                var before = next.Count;
                foreach (var inner in Place(box.Node.Children, box.X + pad, box.Y + head + pad, box.Width - 2 * pad, box.Height - head - 2 * pad))
                {
                    if (inner.W < 1 || inner.H < 1) continue;
                    next.Add(new MapRect(inner.Node, inner.X, inner.Y, inner.W, inner.H, level + 1, box.Group, false));
                }
                placed += next.Count - before;
                rects.Add(box with { Container = next.Count > before });
            }
            frontier = next;
        }
        return rects;
    }

    /// <summary>
    /// The squarified treemap: fills the rectangle strip by strip, starting a new strip whenever adding
    /// to the current one would make its boxes longer and thinner.
    /// </summary>
    private static List<(MapNode Node, double X, double Y, double W, double H)> Place(IEnumerable<MapNode> nodes, double x, double y, double width, double height)
    {
        var placed = new List<(MapNode, double, double, double, double)>();
        var items = nodes.Where(n => n.Total > 0).OrderByDescending(n => n.Total).ToList();
        if (items.Count == 0 || width <= 0 || height <= 0) return placed;
        var scale = width * height / items.Sum(n => (double)n.Total);

        var strip = new List<(MapNode Node, double Area)>();
        double stripArea = 0;

        double Worst(double sum, double smallest, double largest, double side) =>
            Math.Max(side * side * largest / (sum * sum), sum * sum / (side * side * smallest));

        void Flush()
        {
            if (strip.Count == 0) return;
            if (width >= height)
            {
                // A column on the left, boxes stacked top to bottom.
                var w = Math.Min(stripArea / height, width);
                var top = y;
                foreach (var (node, area) in strip)
                {
                    var h = w > 0 ? area / w : 0;
                    placed.Add((node, x, top, w, h));
                    top += h;
                }
                x += w;
                width -= w;
            }
            else
            {
                // A row along the top, boxes side by side.
                var h = Math.Min(stripArea / width, height);
                var left = x;
                foreach (var (node, area) in strip)
                {
                    var w = h > 0 ? area / h : 0;
                    placed.Add((node, left, y, w, h));
                    left += w;
                }
                y += h;
                height -= h;
            }
            strip.Clear();
            stripArea = 0;
        }

        foreach (var node in items)
        {
            var area = node.Total * scale;
            var side = Math.Min(width, height);
            if (strip.Count > 0)
            {
                var now = Worst(stripArea, strip[^1].Area, strip[0].Area, side);
                var then = Worst(stripArea + area, area, strip[0].Area, side);
                if (then > now) Flush();
            }
            strip.Add((node, area));
            stripArea += area;
        }
        Flush();
        return placed;
    }
}
