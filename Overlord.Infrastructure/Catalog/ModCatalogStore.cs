using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Overlord.Core.Catalog;

namespace Overlord.Infrastructure.Catalog;

/// <summary>
/// Local SQLite copy of the ficsit.app catalogue with an FTS5 index, so search is instant, works
/// offline, and doesn't send a request per keystroke.
/// </summary>
public sealed partial class ModCatalogStore
{
    private readonly string _connectionString;

    public ModCatalogStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = true }.ToString();
        using var db = Open();
        Execute(db, Schema);
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
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM mods";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Inserts or updates mods and their search entries in one transaction.</summary>
    public void Upsert(IEnumerable<CatalogMod> mods, DateTimeOffset seenAt)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();

        foreach (var mod in mods)
        {
            // A new release means the cached version list is stale. Compare against the stored row
            // before overwriting it; other fields (even updated_at) change without new versions.
            using (var invalidate = db.CreateCommand())
            {
                invalidate.CommandText = """
                    DELETE FROM mod_versions WHERE reference = $reference AND EXISTS (
                        SELECT 1 FROM mods WHERE id = $id AND COALESCE(last_version_date, '') <> COALESCE($lastVersion, ''))
                    """;
                invalidate.Parameters.AddWithValue("$reference", mod.Reference);
                invalidate.Parameters.AddWithValue("$id", mod.Id);
                invalidate.Parameters.AddWithValue("$lastVersion", mod.LastVersionDate is { } lvd ? Format(lvd) : DBNull.Value);
                invalidate.ExecuteNonQuery();
            }

            using var upsert = db.CreateCommand();
            upsert.CommandText = """
                INSERT INTO mods (id, reference, name, short_description, full_description, logo, authors, tags,
                                  downloads, popularity, hotness, updated_at, last_version_date,
                                  compat_release, compat_experimental, seen_at)
                VALUES ($id, $reference, $name, $short, $full, $logo, $authors, $tags,
                        $downloads, $popularity, $hotness, $updated, $lastVersion, $compatRelease, $compatExp, $seen)
                ON CONFLICT(id) DO UPDATE SET
                    reference = excluded.reference, name = excluded.name,
                    short_description = excluded.short_description, full_description = excluded.full_description,
                    logo = excluded.logo, authors = excluded.authors, tags = excluded.tags,
                    downloads = excluded.downloads, popularity = excluded.popularity, hotness = excluded.hotness,
                    updated_at = excluded.updated_at, last_version_date = excluded.last_version_date,
                    compat_release = excluded.compat_release, compat_experimental = excluded.compat_experimental,
                    seen_at = excluded.seen_at
                RETURNING rowid
                """;
            upsert.Parameters.AddWithValue("$id", mod.Id);
            upsert.Parameters.AddWithValue("$reference", mod.Reference);
            upsert.Parameters.AddWithValue("$name", mod.Name);
            upsert.Parameters.AddWithValue("$short", mod.ShortDescription);
            upsert.Parameters.AddWithValue("$full", (object?)mod.FullDescription ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$logo", (object?)mod.LogoUrl ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$authors", JsonSerializer.Serialize(mod.Authors, CatalogJsonContext.Default.IReadOnlyListString));
            upsert.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(mod.Tags, CatalogJsonContext.Default.IReadOnlyListString));
            upsert.Parameters.AddWithValue("$downloads", mod.Downloads);
            upsert.Parameters.AddWithValue("$popularity", mod.Popularity);
            upsert.Parameters.AddWithValue("$hotness", mod.Hotness);
            upsert.Parameters.AddWithValue("$updated", Format(mod.UpdatedAt));
            upsert.Parameters.AddWithValue("$lastVersion", mod.LastVersionDate is { } lv ? Format(lv) : DBNull.Value);
            upsert.Parameters.AddWithValue("$compatRelease", (int)mod.CompatibilityRelease);
            upsert.Parameters.AddWithValue("$compatExp", (int)mod.CompatibilityExperimental);
            upsert.Parameters.AddWithValue("$seen", Format(seenAt));
            var rowId = Convert.ToInt64(upsert.ExecuteScalar(), CultureInfo.InvariantCulture);

            using var fts = db.CreateCommand();
            fts.CommandText = """
                DELETE FROM mods_fts WHERE rowid = $rowid;
                INSERT INTO mods_fts (rowid, name, reference, short_description, full_description, authors, tags, image_text)
                VALUES ($rowid, $name, $reference, $short, $full, $authors, $tags, $images);
                """;
            fts.Parameters.AddWithValue("$rowid", rowId);
            fts.Parameters.AddWithValue("$name", mod.Name);
            fts.Parameters.AddWithValue("$reference", SplitIdentifier(mod.Reference));
            fts.Parameters.AddWithValue("$short", mod.ShortDescription);
            fts.Parameters.AddWithValue("$full", SearchText.Plain(mod.FullDescription));
            fts.Parameters.AddWithValue("$authors", string.Join(' ', mod.Authors));
            fts.Parameters.AddWithValue("$tags", string.Join(' ', mod.Tags));
            fts.Parameters.AddWithValue("$images", SearchText.ImageText(mod.FullDescription));
            fts.ExecuteNonQuery();

        }

        tx.Commit();
    }

    /// <summary>Removes mods a full sync did not see (deleted or hidden on ficsit.app).</summary>
    public int DeleteNotSeenSince(DateTimeOffset since)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            DELETE FROM mods_fts WHERE rowid IN (SELECT rowid FROM mods WHERE seen_at < $since);
            DELETE FROM mods WHERE seen_at < $since;
            SELECT changes();
            """;
        cmd.Parameters.AddWithValue("$since", Format(since));
        var removed = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        tx.Commit();
        return removed;
    }

    public CatalogMod? Get(string reference)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT {ModColumns} FROM mods m WHERE m.reference = $reference";
        cmd.Parameters.AddWithValue("$reference", reference);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadMod(reader) : null;
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
        using var cmd = db.CreateCommand();
        if (words.Count == 0)
        {
            cmd.CommandText = $"SELECT {ModColumns} FROM mods m ORDER BY m.popularity DESC LIMIT $limit";
        }
        else
        {
            // Words are letters and digits only, so quoting them cannot break FTS5 syntax.
            cmd.CommandText = $"""
                SELECT {ModColumns}
                FROM mods_fts JOIN mods m ON m.rowid = mods_fts.rowid
                WHERE mods_fts MATCH $match
                ORDER BY (m.name = $raw COLLATE NOCASE OR m.reference = $raw COLLATE NOCASE) DESC,
                         {Bm25}, m.popularity DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$match", string.Join(' ', words.Select(w => $"\"{w}\"*")));
            cmd.Parameters.AddWithValue("$raw", query.Trim());
        }
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<CatalogMod>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadMod(reader));
        return results;
    }

    public void SaveVersions(string reference, IReadOnlyList<CatalogVersion> versions, DateTimeOffset fetchedAt)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO mod_versions (reference, json, fetched_at) VALUES ($reference, $json, $fetched)
            ON CONFLICT(reference) DO UPDATE SET json = excluded.json, fetched_at = excluded.fetched_at
            """;
        cmd.Parameters.AddWithValue("$reference", reference);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(versions, CatalogJsonContext.Default.IReadOnlyListCatalogVersion));
        cmd.Parameters.AddWithValue("$fetched", Format(fetchedAt));
        cmd.ExecuteNonQuery();
    }

    /// <returns>Cached versions fetched at or after <paramref name="notBefore"/>, else null.</returns>
    public IReadOnlyList<CatalogVersion>? GetVersions(string reference, DateTimeOffset notBefore)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json FROM mod_versions WHERE reference = $reference AND fetched_at >= $notBefore";
        cmd.Parameters.AddWithValue("$reference", reference);
        cmd.Parameters.AddWithValue("$notBefore", Format(notBefore));
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize(json, CatalogJsonContext.Default.ListCatalogVersion) : null;
    }

    public DateTimeOffset? GetState(string key)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM sync_state WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() is string s ? Parse(s) : null;
    }

    public void SetState(string key, DateTimeOffset value)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_state (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", Format(value));
        cmd.ExecuteNonQuery();
    }

    private const string ModColumns = """
        m.id, m.reference, m.name, m.short_description, m.full_description, m.logo, m.authors, m.tags,
        m.downloads, m.popularity, m.hotness, m.updated_at, m.last_version_date, m.compat_release, m.compat_experimental
        """;

    private static CatalogMod ReadMod(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        r.GetString(2),
        r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        JsonSerializer.Deserialize(r.GetString(6), CatalogJsonContext.Default.ListString) ?? [],
        JsonSerializer.Deserialize(r.GetString(7), CatalogJsonContext.Default.ListString) ?? [],
        r.GetInt32(8),
        r.GetInt32(9),
        r.GetInt32(10),
        Parse(r.GetString(11)),
        r.IsDBNull(12) ? null : Parse(r.GetString(12)),
        (CompatibilityState)r.GetInt32(13),
        (CompatibilityState)r.GetInt32(14));

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

    private static void Execute(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
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
