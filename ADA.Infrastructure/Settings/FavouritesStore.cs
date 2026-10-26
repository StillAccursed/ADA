using System.Text.Json;
using System.Text.Json.Serialization;
using ADA.Infrastructure.IO;

namespace ADA.Infrastructure.Settings;

/// <summary>Mods the user starred, by reference, in <c>Config/favourites.json</c>; shared by every game.</summary>
public sealed class FavouritesStore(string path)
{
    private readonly Lock _gate = new();
    private HashSet<string>? _favourites;

    public IReadOnlySet<string> All
    {
        get
        {
            lock (_gate)
                return new HashSet<string>(Load(), StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Stars or unstars a mod, and saves at once.</summary>
    public void Set(string reference, bool favourite)
    {
        lock (_gate)
        {
            var set = Load();
            if (favourite ? set.Add(reference) : set.Remove(reference))
            {
                AtomicFile.Write(path, stream => JsonSerializer.Serialize(stream,
                    set.Order(StringComparer.OrdinalIgnoreCase).ToList(), FavouritesJsonContext.Default.ListString));
            }
        }
    }

    // Like settings: a missing or damaged file means no favourites, and is replaced on the next save.
    private HashSet<string> Load()
    {
        if (_favourites is not null)
            return _favourites;
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                return _favourites = (JsonSerializer.Deserialize(stream, FavouritesJsonContext.Default.ListString) ?? [])
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return _favourites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
}

[JsonSerializable(typeof(List<string>))]
internal sealed partial class FavouritesJsonContext : JsonSerializerContext;
