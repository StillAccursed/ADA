using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Overlord.Core.Catalog;

namespace Overlord.Infrastructure.Catalog;

/// <summary>
/// Local SQLite copy of the ficsit.app catalogue with an FTS5 index, so search is instant, works
/// offline, and doesn't send a request per keystroke.
/// </summary>
[DapperAot]
public sealed partial class ModCatalogStore
{
    private readonly string _connectionString;

    public ModCatalogStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = true }.ToString();
        using var db = Open();
        db.Execute(Schema);
    }

    private const string Schema = """
        PRAGMA journal_mode = WAL;
        CREATE TABLE IF NOT EXISTS mods (
            rowid INTEGER PRIMARY KEY,
            id TEXT NOT NULL UNIQUE,
            reference TEXT NOT NULL UNIQUE COLLATE NOCASE,
            name TEXT NOT NULL,
            short_description TEXT NOT NULL,
            full_description TEXT,
            logo TEXT,
            authors TEXT NOT NULL,
            tags TEXT NOT NULL,
            downloads INTEGER NOT NULL,
            popularity INTEGER NOT NULL,
            hotness INTEGER NOT NULL,
            updated_at TEXT NOT NULL,
            last_version_date TEXT,
            compat_release INTEGER NOT NULL,
            compat_experimental INTEGER NOT NULL,
            seen_at TEXT NOT NULL);
        CREATE VIRTUAL TABLE IF NOT EXISTS mods_fts USING fts5(
            name, reference, short_description, full_description, authors, tags, image_text,
            tokenize = 'unicode61 remove_diacritics 2');
        CREATE TABLE IF NOT EXISTS mod_versions (
            reference TEXT PRIMARY KEY COLLATE NOCASE,
            json TEXT NOT NULL,
            fetched_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS sync_state (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL);
        """;

    // bm25 weights, in mods_fts column order: a match in the name counts far more than one deep
    // in a long description.
    private const string Bm25 = "bm25(mods_fts, 10.0, 8.0, 4.0, 1.0, 3.0, 3.0, 1.5)";

    public int Count()
    {
        using var db = Open();
        return db.ExecuteScalar<int>("SELECT COUNT(*) FROM mods");
    }

    /// <summary>Inserts or updates mods and their search entries in one transaction.</summary>
    public void Upsert(IEnumerable<CatalogMod> mods, DateTimeOffset seenAt)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var seen = Format(seenAt);

        foreach (var mod in mods)
        {
            var row = new
            {
                mod.Id,
                mod.Reference,
                mod.Name,
                Short = mod.ShortDescription,
                Full = mod.FullDescription,
                Logo = mod.LogoUrl,
                Authors = JsonSerializer.Serialize(mod.Authors, CatalogJsonContext.Default.IReadOnlyListString),
                Tags = JsonSerializer.Serialize(mod.Tags, CatalogJsonContext.Default.IReadOnlyListString),
                mod.Downloads,
                mod.Popularity,
                mod.Hotness,
                Updated = Format(mod.UpdatedAt),
                LastVersion = mod.LastVersionDate is { } lv ? Format(lv) : null,
                CompatRelease = (int)mod.CompatibilityRelease,
                CompatExp = (int)mod.CompatibilityExperimental,
                Seen = seen,
            };

            // A new release means the cached version list is stale. Compare against the stored row
            // before overwriting it; other fields (even updated_at) change without new versions.
            db.Execute("""
                DELETE FROM mod_versions WHERE reference = $Reference AND EXISTS (
                    SELECT 1 FROM mods WHERE id = $Id AND COALESCE(last_version_date, '') <> COALESCE($LastVersion, ''))
                """, row, tx);

            var rowId = db.ExecuteScalar<long>("""
                INSERT INTO mods (id, reference, name, short_description, full_description, logo, authors, tags,
                                  downloads, popularity, hotness, updated_at, last_version_date,
                                  compat_release, compat_experimental, seen_at)
                VALUES ($Id, $Reference, $Name, $Short, $Full, $Logo, $Authors, $Tags,
                        $Downloads, $Popularity, $Hotness, $Updated, $LastVersion, $CompatRelease, $CompatExp, $Seen)
                ON CONFLICT(id) DO UPDATE SET
                    reference = excluded.reference, name = excluded.name,
                    short_description = excluded.short_description, full_description = excluded.full_description,
                    logo = excluded.logo, authors = excluded.authors, tags = excluded.tags,
                    downloads = excluded.downloads, popularity = excluded.popularity, hotness = excluded.hotness,
                    updated_at = excluded.updated_at, last_version_date = excluded.last_version_date,
                    compat_release = excluded.compat_release, compat_experimental = excluded.compat_experimental,
                    seen_at = excluded.seen_at
                RETURNING rowid
                """, row, tx);

            db.Execute("""
                DELETE FROM mods_fts WHERE rowid = $RowId;
                INSERT INTO mods_fts (rowid, name, reference, short_description, full_description, authors, tags, image_text)
                VALUES ($RowId, $Name, $Reference, $Short, $Full, $Authors, $Tags, $Images);
                """,
                new
                {
                    RowId = rowId,
                    mod.Name,
                    Reference = SplitIdentifier(mod.Reference),
                    Short = mod.ShortDescription,
                    Full = SearchText.Plain(mod.FullDescription),
                    Authors = string.Join(' ', mod.Authors),
                    Tags = string.Join(' ', mod.Tags),
                    Images = SearchText.ImageText(mod.FullDescription),
                }, tx);
        }

        tx.Commit();
    }

    /// <summary>Removes mods a full sync did not see (deleted or hidden on ficsit.app).</summary>
    public int DeleteNotSeenSince(DateTimeOffset since)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var args = new { Since = Format(since) };
        db.Execute("DELETE FROM mods_fts WHERE rowid IN (SELECT rowid FROM mods WHERE seen_at < $Since)", args, tx);
        var removed = db.Execute("DELETE FROM mods WHERE seen_at < $Since", args, tx);
        tx.Commit();
        return removed;
    }

    public CatalogMod? Get(string reference)
    {
        using var db = Open();
        return db.QuerySingleOrDefault<ModRow>($"SELECT {ModColumns} FROM mods m WHERE m.reference = $Reference",
            new { Reference = reference })?.ToMod();
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
        var rows = words.Count == 0
            ? db.Query<ModRow>($"SELECT {ModColumns} FROM mods m ORDER BY m.popularity DESC LIMIT $Limit", new { Limit = limit })
            // Words are letters and digits only, so quoting them cannot break FTS5 syntax.
            : db.Query<ModRow>($"""
                SELECT {ModColumns}
                FROM mods_fts JOIN mods m ON m.rowid = mods_fts.rowid
                WHERE mods_fts MATCH $Match
                ORDER BY (m.name = $Raw COLLATE NOCASE OR m.reference = $Raw COLLATE NOCASE) DESC,
                         {Bm25}, m.popularity DESC
                LIMIT $Limit
                """,
                new { Match = string.Join(' ', words.Select(w => $"\"{w}\"*")), Raw = query.Trim(), Limit = limit });
        return rows.Select(r => r.ToMod()).ToList();
    }

    public void SaveVersions(string reference, IReadOnlyList<CatalogVersion> versions, DateTimeOffset fetchedAt)
    {
        using var db = Open();
        db.Execute("""
            INSERT INTO mod_versions (reference, json, fetched_at) VALUES ($Reference, $Json, $Fetched)
            ON CONFLICT(reference) DO UPDATE SET json = excluded.json, fetched_at = excluded.fetched_at
            """,
            new
            {
                Reference = reference,
                Json = JsonSerializer.Serialize(versions, CatalogJsonContext.Default.IReadOnlyListCatalogVersion),
                Fetched = Format(fetchedAt),
            });
    }

    /// <returns>Cached versions fetched at or after <paramref name="notBefore"/>, else null.</returns>
    public IReadOnlyList<CatalogVersion>? GetVersions(string reference, DateTimeOffset notBefore)
    {
        using var db = Open();
        var json = db.ExecuteScalar<string?>("SELECT json FROM mod_versions WHERE reference = $Reference AND fetched_at >= $NotBefore",
            new { Reference = reference, NotBefore = Format(notBefore) });
        return json is null ? null : JsonSerializer.Deserialize(json, CatalogJsonContext.Default.ListCatalogVersion);
    }

    public DateTimeOffset? GetState(string key)
    {
        using var db = Open();
        var value = db.ExecuteScalar<string?>("SELECT value FROM sync_state WHERE key = $Key", new { Key = key });
        return value is null ? null : Parse(value);
    }

    public void SetState(string key, DateTimeOffset value)
    {
        using var db = Open();
        db.Execute("""
            INSERT INTO sync_state (key, value) VALUES ($Key, $Value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """, new { Key = key, Value = Format(value) });
    }

    // Aliased to ModRow's property names.
    private const string ModColumns = """
        m.id AS Id, m.reference AS Reference, m.name AS Name, m.short_description AS ShortDescription,
        m.full_description AS FullDescription, m.logo AS Logo, m.authors AS Authors, m.tags AS Tags,
        m.downloads AS Downloads, m.popularity AS Popularity, m.hotness AS Hotness, m.updated_at AS UpdatedAt,
        m.last_version_date AS LastVersionDate, m.compat_release AS CompatRelease, m.compat_experimental AS CompatExperimental
        """;

    /// <summary>A <c>mods</c> row as stored: lists as JSON, dates as text.</summary>
    private sealed class ModRow
    {
        public string Id { get; set; } = "";
        public string Reference { get; set; } = "";
        public string Name { get; set; } = "";
        public string ShortDescription { get; set; } = "";
        public string? FullDescription { get; set; }
        public string? Logo { get; set; }
        public string Authors { get; set; } = "[]";
        public string Tags { get; set; } = "[]";
        public int Downloads { get; set; }
        public int Popularity { get; set; }
        public int Hotness { get; set; }
        public string UpdatedAt { get; set; } = "";
        public string? LastVersionDate { get; set; }
        public int CompatRelease { get; set; }
        public int CompatExperimental { get; set; }

        public CatalogMod ToMod() => new(
            Id, Reference, Name, ShortDescription, FullDescription, Logo,
            JsonSerializer.Deserialize(Authors, CatalogJsonContext.Default.ListString) ?? [],
            JsonSerializer.Deserialize(Tags, CatalogJsonContext.Default.ListString) ?? [],
            Downloads, Popularity, Hotness,
            Parse(UpdatedAt),
            LastVersionDate is null ? null : Parse(LastVersionDate),
            (CompatibilityState)CompatRelease,
            (CompatibilityState)CompatExperimental);
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

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex SearchWord();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex CamelBoundary();
}

[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(IReadOnlyList<CatalogVersion>))]
[JsonSerializable(typeof(List<CatalogVersion>))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext;
