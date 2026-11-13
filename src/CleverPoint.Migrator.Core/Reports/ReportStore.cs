using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CleverPoint.Migrator.Core.Hosting;

namespace CleverPoint.Migrator.Core.Reports;

/// <summary>How one site (or pair) of a run ended.</summary>
public sealed class SiteRecord
{
    public string Key { get; set; } = "";
    public string Site { get; set; } = "";
    public string? Target { get; set; }
    /// <summary>ok | failed | denied | notFound | signIn. ("writing" only ever appears as a crash marker.)</summary>
    public string Status { get; set; } = "ok";
    public string? Error { get; set; }
    public long Rows { get; set; }
    public double Seconds { get; set; }
    public DateTime FinishedUtc { get; set; }
    /// <summary>The site's share of the run's summary numbers.</summary>
    public Dictionary<string, long>? Counts { get; set; }
    /// <summary>Crash marker only: how long each table was before this site's rows were added.</summary>
    public Dictionary<string, long>? Offsets { get; set; }

    [JsonIgnore] public bool Ok => Status == "ok";
}

/// <summary>A filter on one column of a table. Op is "=" (equal, any case) or "~" (contains, any case).</summary>
public sealed record RowFilter(string Column, string Op, string Value);

/// <summary>
/// The files of one report run.
///
/// Rows are kept as one JSON object per line, one file per table, so a report over hundreds of sites
/// never has to fit in memory: it is appended while it runs and streamed when it is read or exported.
///
/// A site's rows are first written to a staging file of its own and only added to the tables when the
/// site finishes without an error. A failed, cancelled or crashed site therefore leaves no partial
/// rows, and running it again cannot produce duplicates.
/// </summary>
public sealed class ReportStore
{
    /// <summary>A line offset is remembered for every this-many rows, so a page deep in a large table is reached without reading what is before it.</summary>
    private const int IndexEvery = 1000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _commit = new();

    public static string RootFolder => Path.Combine(UxSettings.Folder, "reports");
    public static string FolderFor(string runId) => Path.Combine(RootFolder, runId);

    public string Folder { get; }
    private string TablesFolder => Path.Combine(Folder, "tables");
    private string WorkFolder => Path.Combine(Folder, "work");
    private string SitesPath => Path.Combine(Folder, "sites.ndjson");
    private string CountsPath => Path.Combine(Folder, "counts.json");
    private string ColumnsPath => Path.Combine(Folder, "columns.json");
    public string TablePath(string table) => Path.Combine(TablesFolder, Safe(table) + ".ndjson");
    private string IndexPath(string table) => Path.Combine(TablesFolder, Safe(table) + ".idx");

    public ReportStore(string folder)
    {
        Folder = folder;
    }

    private static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    // ---- writing ---------------------------------------------------------------------------------

    /// <summary>The rows of one site while it is being scanned.</summary>
    public sealed class SiteWriter : IDisposable
    {
        private readonly string _folder;
        private readonly Dictionary<string, StreamWriter> _tables = new(StringComparer.Ordinal);
        /// <summary>Per table, the columns that had a value in at least one row.</summary>
        internal Dictionary<string, HashSet<string>> Used { get; } = new(StringComparer.Ordinal);
        public long Rows { get; private set; }

        internal SiteWriter(string folder)
        {
            _folder = folder;
            Directory.CreateDirectory(folder);
        }

        public void Write(string table, Dictionary<string, object?> row)
        {
            // Empty values are left out: most rows have several, and the files get large.
            var compact = new Dictionary<string, object?>(row.Count);
            foreach (var (name, value) in row)
            {
                if (value is null || value is string { Length: 0 }) continue;
                compact[name] = value is DateTime date ? date.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") : value;
            }
            var line = JsonSerializer.Serialize(compact, Json);
            lock (_tables)
            {
                if (!_tables.TryGetValue(table, out var writer))
                {
                    writer = new StreamWriter(Path.Combine(_folder, Safe(table) + ".ndjson"), append: false, new UTF8Encoding(false));
                    _tables[table] = writer;
                    Used[table] = new HashSet<string>(StringComparer.Ordinal);
                }
                var used = Used[table];
                foreach (var name in compact.Keys) used.Add(name);
                writer.Write(line);
                writer.Write('\n');
                Rows++;
            }
        }

        internal IReadOnlyList<(string Table, string Path)> Close()
        {
            lock (_tables)
            {
                foreach (var writer in _tables.Values) writer.Dispose();
                var files = _tables.Keys.Select(t => (t, Path.Combine(_folder, Safe(t) + ".ndjson"))).ToList();
                _tables.Clear();
                return files;
            }
        }

        public void Dispose()
        {
            Close();
            try { Directory.Delete(_folder, recursive: true); } catch { /* left for the next clean-up */ }
        }
    }

    public SiteWriter BeginSite(int index) => new(Path.Combine(WorkFolder, index.ToString()));

    /// <summary>The site finished: its rows join the tables and its record is written.</summary>
    public void Commit(SiteWriter writer, SiteRecord record)
    {
        var files = writer.Close();
        lock (_commit)
        {
            Directory.CreateDirectory(TablesFolder);
            var counts = ReadCounts();
            // A marker first, so a crash in the middle of the copy can be undone on resume.
            var marker = new SiteRecord
            {
                Key = record.Key, Site = record.Site, Target = record.Target, Status = "writing", FinishedUtc = DateTime.UtcNow,
                Offsets = files.ToDictionary(f => f.Table, f => File.Exists(TablePath(f.Table)) ? new FileInfo(TablePath(f.Table)).Length : 0L),
            };
            AppendSite(marker);
            foreach (var (table, path) in files)
                counts[table] = Append(table, path, counts.GetValueOrDefault(table));
            WriteCounts(counts);
            MergeUsedColumns(writer.Used);
            record.Rows = writer.Rows;
            AppendSite(record);
        }
        writer.Dispose();
    }

    /// <summary>The site failed: its rows are thrown away and only its record is kept.</summary>
    public void Discard(SiteWriter writer, SiteRecord record)
    {
        writer.Dispose();
        lock (_commit) AppendSite(record);
    }

    /// <summary>Appends a staging file to its table, extending the line index. Returns the table's new row count.</summary>
    private long Append(string table, string stagingPath, long rowsBefore)
    {
        using var source = File.OpenRead(stagingPath);
        using var target = new FileStream(TablePath(table), FileMode.Append, FileAccess.Write, FileShare.Read);
        using var index = new FileStream(IndexPath(table), FileMode.Append, FileAccess.Write, FileShare.Read);
        var position = target.Position;
        var rows = rowsBefore;
        var buffer = new byte[1 << 16];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                rows++;
                if (rows % IndexEvery == 0) index.Write(BitConverter.GetBytes(position + i + 1));
            }
            target.Write(buffer, 0, read);
            position += read;
        }
        return rows;
    }

    private void AppendSite(SiteRecord record)
    {
        Directory.CreateDirectory(Folder);
        File.AppendAllText(SitesPath, JsonSerializer.Serialize(record, Json) + "\n", new UTF8Encoding(false));
    }

    /// <summary>
    /// Called before a run continues: removes what an interrupted run left half-done (staging files, and
    /// the rows of a site whose copy into the tables was cut short).
    /// </summary>
    public void Repair()
    {
        lock (_commit)
        {
            try { if (Directory.Exists(WorkFolder)) Directory.Delete(WorkFolder, recursive: true); } catch { /* nothing to undo there */ }
            var last = AllSiteRecords().LastOrDefault();
            if (last is not { Status: "writing", Offsets: not null }) return;
            var counts = ReadCounts();
            foreach (var (table, length) in last.Offsets)
            {
                if (!File.Exists(TablePath(table))) continue;
                using (var stream = new FileStream(TablePath(table), FileMode.Open, FileAccess.Write)) stream.SetLength(length);
                counts[table] = Reindex(table);
            }
            WriteCounts(counts);
        }
    }

    private long Reindex(string table)
    {
        using var source = File.OpenRead(TablePath(table));
        using var index = new FileStream(IndexPath(table), FileMode.Create, FileAccess.Write);
        long rows = 0, position = 0;
        var buffer = new byte[1 << 16];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                rows++;
                if (rows % IndexEvery == 0) index.Write(BitConverter.GetBytes(position + i + 1));
            }
            position += read;
        }
        return rows;
    }

    // ---- reading ---------------------------------------------------------------------------------

    private Dictionary<string, long> ReadCounts()
    {
        try
        {
            if (File.Exists(CountsPath))
                return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(CountsPath)) ?? new();
        }
        catch { /* rebuilt below */ }
        return new Dictionary<string, long>(StringComparer.Ordinal);
    }

    private void WriteCounts(Dictionary<string, long> counts) => File.WriteAllText(CountsPath, JsonSerializer.Serialize(counts));

    private void MergeUsedColumns(Dictionary<string, HashSet<string>> seen)
    {
        if (seen.Count == 0) return;
        var all = ReadUsedColumns();
        var changed = false;
        foreach (var (table, names) in seen)
        {
            if (!all.TryGetValue(table, out var known)) all[table] = known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in names) changed |= known.Add(name);
        }
        if (changed) File.WriteAllText(ColumnsPath, JsonSerializer.Serialize(all));
    }

    private Dictionary<string, HashSet<string>> ReadUsedColumns()
    {
        try
        {
            if (File.Exists(ColumnsPath))
                return JsonSerializer.Deserialize<Dictionary<string, HashSet<string>>>(File.ReadAllText(ColumnsPath)) ?? new();
        }
        catch { /* treated as nothing known */ }
        return new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    }

    /// <summary>The columns of a table that have a value in at least one row of the run.</summary>
    public IReadOnlySet<string> UsedColumns(string table)
    {
        lock (_commit) return ReadUsedColumns().GetValueOrDefault(table) ?? new HashSet<string>();
    }

    /// <summary>Rows per table.</summary>
    public IReadOnlyDictionary<string, long> RowCounts()
    {
        lock (_commit) return ReadCounts();
    }

    public long TotalRows() => RowCounts().Values.Sum();

    private List<SiteRecord> AllSiteRecords()
    {
        var records = new List<SiteRecord>();
        if (!File.Exists(SitesPath)) return records;
        using var stream = new FileStream(SitesPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            try
            {
                if (JsonSerializer.Deserialize<SiteRecord>(line, Json) is { } record) records.Add(record);
            }
            catch (JsonException) { /* a line cut short by a crash */ }
        }
        return records;
    }

    /// <summary>How each site ended, the latest attempt of each, in the order they first finished.</summary>
    public List<SiteRecord> Sites()
    {
        var latest = new Dictionary<string, SiteRecord>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var record in AllSiteRecords())
        {
            if (record.Status == "writing") continue;
            if (!latest.ContainsKey(record.Key)) order.Add(record.Key);
            latest[record.Key] = record;
        }
        return order.Select(key => latest[key]).ToList();
    }

    /// <summary>The run's summary numbers: each report's own counters, added up over the sites that finished.</summary>
    public Dictionary<string, long> Summary()
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var site in Sites().Where(s => s.Ok && s.Counts is not null))
            foreach (var (name, value) in site.Counts!)
                totals[name] = totals.GetValueOrDefault(name) + value;
        return totals;
    }

    /// <summary>Every row of a table, read as it goes.</summary>
    public IEnumerable<Dictionary<string, JsonElement>> ReadRows(string table, long skip = 0)
    {
        var path = TablePath(table);
        if (!File.Exists(path)) yield break;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        // Jump over whole blocks of rows with the line index, then step through the rest.
        var blocks = skip / IndexEvery;
        if (blocks > 0 && File.Exists(IndexPath(table)))
        {
            using var index = new FileStream(IndexPath(table), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var available = index.Length / sizeof(long);
            blocks = Math.Min(blocks, available);
            if (blocks > 0)
            {
                index.Seek((blocks - 1) * sizeof(long), SeekOrigin.Begin);
                var bytes = new byte[sizeof(long)];
                index.ReadExactly(bytes);
                stream.Seek(BitConverter.ToInt64(bytes), SeekOrigin.Begin);
                skip -= blocks * IndexEvery;
            }
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (skip > 0) { skip--; continue; }
            if (line.Length == 0) continue;
            Dictionary<string, JsonElement>? row;
            try { row = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line); }
            catch (JsonException) { continue; }
            if (row is not null) yield return row;
        }
    }

    /// <summary>
    /// One page of a table. Without filters the page is reached directly; with filters the table is read
    /// from the start. <c>More</c> says whether rows follow the page.
    /// </summary>
    public (List<Dictionary<string, JsonElement>> Rows, bool More) ReadPage(string table, long offset, int limit, IReadOnlyList<RowFilter>? filters = null)
    {
        var rows = new List<Dictionary<string, JsonElement>>();
        var more = false;
        if (filters is not { Count: > 0 })
        {
            foreach (var row in ReadRows(table, offset))
            {
                if (rows.Count == limit) { more = true; break; }
                rows.Add(row);
            }
            return (rows, more);
        }
        long seen = 0;
        foreach (var row in ReadRows(table))
        {
            if (!Matches(row, filters)) continue;
            if (seen++ < offset) continue;
            if (rows.Count == limit) { more = true; break; }
            rows.Add(row);
        }
        return (rows, more);
    }

    /// <summary>
    /// The rows of a table that come first when it is sorted by one column: the largest folders, the
    /// newest files. The table is read once and only the wanted rows are kept, so this works on a table
    /// of any size. Numbers sort as numbers, everything else as text; rows without a value come last.
    /// </summary>
    public (List<Dictionary<string, JsonElement>> Rows, long Matching) ReadSorted(string table, string column, bool descending, int offset, int limit,
        IReadOnlyList<RowFilter>? filters = null)
    {
        var keep = offset + limit;
        // The queue holds the best rows so far with the worst of them on top, ready to be pushed out.
        var best = new PriorityQueue<Dictionary<string, JsonElement>, SortKey>(Comparer<SortKey>.Create((a, b) => -a.CompareTo(b, descending)));
        long matching = 0, sequence = 0;
        foreach (var row in ReadRows(table))
        {
            if (filters is { Count: > 0 } && !Matches(row, filters)) continue;
            matching++;
            var pair = row.FirstOrDefault(p => p.Key.Equals(column, StringComparison.OrdinalIgnoreCase));
            var key = SortKey.Of(pair.Key is null ? default : pair.Value, sequence++);
            if (best.Count < keep) best.Enqueue(row, key);
            else if (best.TryPeek(out _, out var worst) && key.CompareTo(worst, descending) < 0)
            {
                best.Dequeue();
                best.Enqueue(row, key);
            }
        }
        var ordered = best.UnorderedItems.OrderBy(i => i.Priority, Comparer<SortKey>.Create((a, b) => a.CompareTo(b, descending))).Select(i => i.Element);
        return (ordered.Skip(offset).Take(limit).ToList(), matching);
    }

    private readonly record struct SortKey(int Kind, double Number, string Text, long Sequence)
    {
        // Kind: 0 = a number, 1 = text, 2 = no value.
        public static SortKey Of(JsonElement value, long sequence) => value.ValueKind switch
        {
            JsonValueKind.Number => new SortKey(0, value.GetDouble(), "", sequence),
            JsonValueKind.Undefined or JsonValueKind.Null => new SortKey(2, 0, "", sequence),
            _ => new SortKey(1, 0, CellText(value), sequence),
        };

        /// <summary>Negative when this row comes before the other in the wanted order.</summary>
        public int CompareTo(SortKey other, bool descending)
        {
            // Rows without a value are last whichever way the sort goes.
            if (Kind == 2 || other.Kind == 2) return Kind != other.Kind ? Kind.CompareTo(other.Kind) : Sequence.CompareTo(other.Sequence);
            var order = Kind != other.Kind ? Kind.CompareTo(other.Kind)
                : Kind == 0 ? Number.CompareTo(other.Number)
                : string.Compare(Text, other.Text, StringComparison.OrdinalIgnoreCase);
            if (order == 0) return Sequence.CompareTo(other.Sequence);
            return descending ? -order : order;
        }
    }

    /// <summary>How many rows of a table pass the filters.</summary>
    public long CountRows(string table, IReadOnlyList<RowFilter>? filters = null) =>
        filters is not { Count: > 0 } ? RowCounts().GetValueOrDefault(table) : ReadRows(table).LongCount(row => Matches(row, filters));

    public static bool Matches(Dictionary<string, JsonElement> row, IReadOnlyList<RowFilter> filters)
    {
        foreach (var filter in filters)
        {
            // Column "*": the text may be in any cell of the row.
            if (filter.Column == "*")
            {
                if (!row.Values.Any(v => CellText(v).Contains(filter.Value, StringComparison.OrdinalIgnoreCase))) return false;
                continue;
            }
            var pair = row.FirstOrDefault(p => p.Key.Equals(filter.Column, StringComparison.OrdinalIgnoreCase));
            var text = pair.Key is null ? "" : CellText(pair.Value);
            var ok = filter.Op == "~"
                ? text.Contains(filter.Value, StringComparison.OrdinalIgnoreCase)
                : text.Equals(filter.Value, StringComparison.OrdinalIgnoreCase);
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>A cell as text, the way exports and filters see it.</summary>
    public static string CellText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => value.GetRawText(),
    };

    /// <summary>Removes the run's files.</summary>
    public void Delete()
    {
        if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
    }
}
