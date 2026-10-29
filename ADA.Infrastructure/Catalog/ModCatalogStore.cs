using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ADA.Core.Catalog;
using ADA.Core.Settings;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using LinqToDB.Mapping;
using Microsoft.Data.Sqlite;

namespace ADA.Infrastructure.Catalog;

/// <summary>
/// Local SQLite copy of the ficsit.app catalogue with an FTS5 index, so search is instant, works
/// offline, and doesn't send a request per keystroke.
/// </summary>
public sealed partial class ModCatalogStore
{
    // Bump whenever the tables change shape. 3: tables created by linq2db's CreateTable.
    private const int SCHEMA_VERSION = 3;

    // CreateFormat templates: {0} name, {1} type, {2} NULL specifier, {3} identity.
    private const string UNIQUE = "{0} {1} {2} UNIQUE";
    private const string UNIQUE_NOCASE = "{0} {1} {2} UNIQUE COLLATE NOCASE";
    private const string COLLATE_NOCASE = "{0} {1} {2} COLLATE NOCASE";

    // FTS5 virtual tables have no linq2db CREATE TABLE support.
    private const string CREATE_SEARCH_INDEX = """
        CREATE VIRTUAL TABLE IF NOT EXISTS mods_fts USING fts5(
            name, reference, short_description, full_description, authors, tags, image_text,
            tokenize = 'unicode61 remove_diacritics 2')
        """;

    private const string NOCASE = "NOCASE";

    private readonly DataOptions _options;

    /// <summary>
    /// Opens or creates the database. A catalogue from an older schema is dropped and rebuilt, so the
    /// next sync is a full one.
    /// </summary>
    public ModCatalogStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = true }.ToString();
        _options = new DataOptions().UseConnectionString(ProviderName.SQLiteMS, connectionString);

        using var db = Open();
        db.Execute("PRAGMA journal_mode = WAL");
        // The catalogue is a cache: one from an older ADA is rebuilt rather than migrated (the
        // next sync is then a full one).
        var current = db.Execute<int>("PRAGMA user_version") == SCHEMA_VERSION;
        if (!current)
        {
            db.DropTable<ModRow>(tableOptions: TableOptions.DropIfExists);
            db.DropTable<ModSearchRow>(tableOptions: TableOptions.DropIfExists);
            db.DropTable<VersionsRow>(tableOptions: TableOptions.DropIfExists);
            db.DropTable<StateRow>(tableOptions: TableOptions.DropIfExists);
        }
        db.CreateTable<ModRow>(tableOptions: TableOptions.CreateIfNotExists);
        db.Execute(CREATE_SEARCH_INDEX);
        db.CreateTable<VersionsRow>(tableOptions: TableOptions.CreateIfNotExists);
        db.CreateTable<StateRow>(tableOptions: TableOptions.CreateIfNotExists);
        if (!current)
        {
            db.Execute($"PRAGMA user_version = {SCHEMA_VERSION}");
        }
    }

    public int Count()
    {
        using var db = Open();
        return db.GetTable<ModRow>().Count();
    }

    /// <summary>Inserts or updates mods and their search entries in one transaction.</summary>
    internal void Upsert(IEnumerable<CatalogMod> mods, DateTimeOffset seenAt)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var seen = Format(seenAt);
        var modRows = db.GetTable<ModRow>();
        var searchRows = db.GetTable<ModSearchRow>();

        foreach (var mod in mods)
        {
            var row = ModRow.From(mod, seen);

            // A new release means the cached version list is stale. Compare against the stored row
            // before overwriting it; other fields (even updated_at) change without new versions.
            db.GetTable<VersionsRow>()
                .Where(v => v.Reference == row.Reference
                            && modRows.Any(m => m.Id == row.Id && (m.LastVersionDate ?? "") != (row.LastVersionDate ?? "")))
                .Delete();

            // linq2db's SQLite upsert only matches on the primary key (rowid), not on id. Inside the
            // transaction, look-up-then-write is equivalent and hands back the rowid mods_fts needs.
            var existing = modRows.Where(m => m.Id == row.Id).Select(m => (long?)m.RowId).SingleOrDefault();
            long rowId;
            if (existing is { } id)
            {
                row.RowId = rowId = id;
                db.Update(row);
            }
            else
            {
                rowId = db.InsertWithInt64Identity(row);
            }

            searchRows.Where(s => s.RowId == rowId).Delete();
            db.Insert(new ModSearchRow
            {
                RowId = rowId,
                Name = mod.Name,
                Reference = SplitIdentifier(mod.Reference),
                ShortDescription = mod.ShortDescription,
                FullDescription = SearchText.Plain(mod.FullDescription),
                Authors = string.Join(' ', mod.Authors),
                Tags = string.Join(' ', mod.Tags),
                ImageText = SearchText.ImageText(mod.FullDescription),
            });
        }

        tx.Commit();
    }

    /// <summary>Removes mods a full sync did not see (deleted or hidden on ficsit.app).</summary>
    internal int DeleteNotSeenSince(DateTimeOffset since)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var cutoff = Format(since);
        var stale = db.GetTable<ModRow>().Where(m => m.SeenAt.CompareTo(cutoff) < 0);
        db.GetTable<ModSearchRow>().Where(s => stale.Select(m => m.RowId).Contains(s.RowId)).Delete();
        var removed = stale.Delete();
        tx.Commit();
        return removed;
    }

    /// <summary>Null when the mod isn't in the catalogue.</summary>
    public CatalogMod? Get(string reference)
    {
        using var db = Open();
        return db.GetTable<ModRow>().SingleOrDefault(m => m.Reference == reference)?.ToMod();
    }

    /// <summary>
    /// Recent releases from the local cache, newest first; metadata-only edits and undated entries are
    /// excluded. <paramref name="limit"/> is clamped to the Dashboard setting's range.
    /// </summary>
    public IReadOnlyList<CatalogMod> GetRecentlyUpdated(int limit)
    {
        using var db = Open();
        return db.GetTable<ModRow>()
            .Where(m => m.LastVersionDate != null)
            .OrderByDescending(m => m.LastVersionDate)
            .ThenBy(m => Sql.Collate(m.Name, NOCASE))
            .ThenBy(m => m.Reference)
            .Take(Math.Clamp(limit, AppSettings.MIN_DASHBOARD_LATEST_MODS_COUNT, AppSettings.MAX_DASHBOARD_LATEST_MODS_COUNT))
            .AsEnumerable()
            .Select(r => r.ToMod())
            .ToList();
    }

    /// <summary>
    /// Full-text search over names, references, descriptions, authors, tags and image text. Each
    /// word is a prefix match, so "conv bel" finds "Conveyor Belt". An empty query lists the most
    /// popular mods.
    /// </summary>
    public IReadOnlyList<CatalogMod> Search(string query, int limit = 50)
    {
        var words = SearchWord().Matches(query).Select(m => m.Value).ToList();

        using var db = Open();
        var mods = db.GetTable<ModRow>();
        if (words.Count == 0)
        {
            return mods.OrderByDescending(m => m.Popularity).Take(limit).AsEnumerable().Select(r => r.ToMod()).ToList();
        }

        // Words are letters and digits only, so quoting them cannot break FTS5 syntax.
        var match = string.Join(' ', words.Select(w => $"\"{w}\"*"));
        var raw = query.Trim();
        var sqlite = Sql.Ext.SQLite();
        return (
            from s in db.GetTable<ModSearchRow>()
            join m in mods on s.RowId equals m.RowId
            where sqlite.Match(s, match)
            // bm25 weights, in mods_fts column order: a match in the name counts far more than one
            // deep in a long description.
            orderby Sql.Collate(m.Name, NOCASE) == raw || m.Reference == raw descending,
                    sqlite.FTS5bm25(s, 10.0, 8.0, 4.0, 1.0, 3.0, 3.0, 1.5),
                    m.Popularity descending
            select m)
            .Take(limit)
            .AsEnumerable()
            .Select(r => r.ToMod())
            .ToList();
    }

    /// <summary>Every tag used by a mod, with how many mods use it, most used first.</summary>
    public IReadOnlyList<(string Tag, int Count)> Tags()
    {
        using var db = Open();
        return db.GetTable<ModRow>().Select(m => m.Tags).AsEnumerable()
            .SelectMany(json => JsonSerializer.Deserialize(json, CatalogJsonContext.Default.ListString) ?? [])
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.First(), g.Count()))
            .OrderByDescending(t => t.Item2)
            .ThenBy(t => t.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Caches a mod's version list, replacing any earlier one; <see cref="GetVersions"/> treats it as
    /// fetched at <paramref name="fetchedAt"/>.
    /// </summary>
    internal void SaveVersions(string reference, IReadOnlyList<CatalogVersion> versions, DateTimeOffset fetchedAt)
    {
        using var db = Open();
        db.GetTable<VersionsRow>().Upsert(new VersionsRow
        {
            Reference = reference,
            Json = JsonSerializer.Serialize(versions, CatalogJsonContext.Default.IReadOnlyListCatalogVersion),
            FetchedAt = Format(fetchedAt),
        });
    }

    /// <returns>Cached versions fetched at or after <paramref name="notBefore"/>, else null.</returns>
    internal IReadOnlyList<CatalogVersion>? GetVersions(string reference, DateTimeOffset notBefore)
    {
        using var db = Open();
        var cutoff = Format(notBefore);
        var json = db.GetTable<VersionsRow>()
            .Where(v => v.Reference == reference && v.FetchedAt.CompareTo(cutoff) >= 0)
            .Select(v => v.Json)
            .SingleOrDefault();
        return json is null ? null : JsonSerializer.Deserialize(json, CatalogJsonContext.Default.ListCatalogVersion);
    }

    /// <summary>Sync timestamps by key, such as <see cref="CatalogSync"/>'s watermark and last-sync times.</summary>
    internal DateTimeOffset? GetState(string key)
    {
        using var db = Open();
        var value = db.GetTable<StateRow>().Where(s => s.Key == key).Select(s => s.Value).SingleOrDefault();
        return value is null ? null : Parse(value);
    }

    /// <inheritdoc cref="GetState"/>
    internal void SetState(string key, DateTimeOffset value)
    {
        using var db = Open();
        db.GetTable<StateRow>().Upsert(new StateRow { Key = key, Value = Format(value) });
    }

    /// <summary>A <c>mods</c> row as stored: lists as JSON, dates as text.</summary>
    [Table("mods")]
    private sealed class ModRow
    {
        // Shared with mods_fts, which links its entries to mods by rowid.
        [Column("rowid"), PrimaryKey, Identity] public long RowId { get; set; }
        [Column("id", CreateFormat = UNIQUE), NotNull] public string Id { get; set; } = "";
        [Column("reference", CreateFormat = UNIQUE_NOCASE), NotNull] public string Reference { get; set; } = "";
        [Column("name"), NotNull] public string Name { get; set; } = "";
        [Column("short_description"), NotNull] public string ShortDescription { get; set; } = "";
        [Column("full_description")] public string? FullDescription { get; set; }
        [Column("logo")] public string? Logo { get; set; }
        [Column("authors"), NotNull] public string Authors { get; set; } = "[]";
        [Column("tags"), NotNull] public string Tags { get; set; } = "[]";
        [Column("downloads")] public int Downloads { get; set; }
        [Column("popularity")] public int Popularity { get; set; }
        [Column("hotness")] public int Hotness { get; set; }
        [Column("views")] public int Views { get; set; }
        [Column("latest"), NotNull] public string Latest { get; set; } = "[]";
        [Column("updated_at"), NotNull] public string UpdatedAt { get; set; } = "";
        [Column("last_version_date")] public string? LastVersionDate { get; set; }
        [Column("compat_release")] public int CompatRelease { get; set; }
        [Column("compat_experimental")] public int CompatExperimental { get; set; }
        [Column("seen_at"), NotNull] public string SeenAt { get; set; } = "";

        /// <summary>The row to write for <paramref name="mod"/>, last seen by a sync at <paramref name="seenAt"/>.</summary>
        public static ModRow From(CatalogMod mod, string seenAt) => new()
        {
            Id = mod.Id,
            Reference = mod.Reference,
            Name = mod.Name,
            ShortDescription = mod.ShortDescription,
            FullDescription = mod.FullDescription,
            Logo = mod.LogoUrl,
            Authors = JsonSerializer.Serialize(mod.Authors, CatalogJsonContext.Default.IReadOnlyListString),
            Tags = JsonSerializer.Serialize(mod.Tags, CatalogJsonContext.Default.IReadOnlyListString),
            Downloads = mod.Downloads,
            Popularity = mod.Popularity,
            Hotness = mod.Hotness,
            Views = mod.Views,
            Latest = JsonSerializer.Serialize(mod.Latest ?? [], CatalogJsonContext.Default.IReadOnlyListLatestVersion),
            UpdatedAt = Format(mod.UpdatedAt),
            LastVersionDate = mod.LastVersionDate is { } lv ? Format(lv) : null,
            CompatRelease = (int)mod.CompatibilityRelease,
            CompatExperimental = (int)mod.CompatibilityExperimental,
            SeenAt = seenAt,
        };

        public CatalogMod ToMod() => new(
            Id, Reference, Name, ShortDescription, FullDescription, Logo,
            JsonSerializer.Deserialize(Authors, CatalogJsonContext.Default.ListString) ?? [],
            JsonSerializer.Deserialize(Tags, CatalogJsonContext.Default.ListString) ?? [],
            Downloads, Popularity, Hotness,
            Parse(UpdatedAt),
            LastVersionDate is null ? null : Parse(LastVersionDate),
            (CompatibilityState)CompatRelease,
            (CompatibilityState)CompatExperimental,
            Views,
            JsonSerializer.Deserialize(Latest, CatalogJsonContext.Default.ListLatestVersion) ?? []);
    }

    /// <summary>A <c>mods_fts</c> entry: the searchable text of the <c>mods</c> row with the same rowid.</summary>
    [Table("mods_fts")]
    private sealed class ModSearchRow
    {
        [Column("rowid")] public long RowId { get; set; }
        [Column("name")] public string Name { get; set; } = "";
        [Column("reference")] public string Reference { get; set; } = "";
        [Column("short_description")] public string ShortDescription { get; set; } = "";
        [Column("full_description")] public string FullDescription { get; set; } = "";
        [Column("authors")] public string Authors { get; set; } = "";
        [Column("tags")] public string Tags { get; set; } = "";
        [Column("image_text")] public string ImageText { get; set; } = "";
    }

    [Table("mod_versions")]
    private sealed class VersionsRow
    {
        [Column("reference", CreateFormat = COLLATE_NOCASE), PrimaryKey, NotNull] public string Reference { get; set; } = "";
        [Column("json"), NotNull] public string Json { get; set; } = "";
        [Column("fetched_at"), NotNull] public string FetchedAt { get; set; } = "";
    }

    [Table("sync_state")]
    private sealed class StateRow
    {
        [Column("key"), PrimaryKey, NotNull] public string Key { get; set; } = "";
        [Column("value"), NotNull] public string Value { get; set; } = "";
    }

    // "WeatherExtended" → "WeatherExtended Weather Extended", so a search for "weather" matches
    // the reference as well as the display name.
    private static string SplitIdentifier(string reference) =>
        $"{reference} {CamelBoundary().Replace(reference.Replace('_', ' '), " ")}";

    // Fixed-width UTC strings sort and compare correctly as text.
    private static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private DataConnection Open() => new(_options);

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex SearchWord();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex CamelBoundary();
}

[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(IReadOnlyList<CatalogVersion>))]
[JsonSerializable(typeof(List<CatalogVersion>))]
[JsonSerializable(typeof(IReadOnlyList<LatestVersion>))]
[JsonSerializable(typeof(List<LatestVersion>))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext;
