using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ADA.Core.Profiles;
using ADA.Infrastructure.IO;

namespace ADA.Infrastructure.Profiles;

/// <summary>
/// The user's profiles, one <c>.olp</c> file each in <c>Config/Profiles/</c>, and which profile each
/// game is using (<c>Config/profiles.json</c>, keyed by the game's folder).
/// </summary>
public sealed partial class ProfileStore(string profilesFolder, string stateFile)
{
    private readonly Lock _gate = new();

    /// <summary>Raised outside the store lock after a profile or active selection is saved, on the saving thread.</summary>
    internal event EventHandler? Changed;

    public string Folder { get; } = profilesFolder;

    /// <summary>Every readable profile, by name. A damaged file is skipped rather than stopping the app.</summary>
    public IReadOnlyList<ModProfile> List()
    {
        lock (_gate)
        {
            return Files().Select(f => f.Profile).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    /// <summary>Null when no profile has this name (names match ignoring case).</summary>
    public ModProfile? Get(string name)
    {
        lock (_gate)
        {
            return Find(name)?.Profile;
        }
    }

    /// <summary>Creates or replaces the profile with this name (names match ignoring case).</summary>
    /// <exception cref="InvalidProfileException">The name or a mod is invalid.</exception>
    public void Save(ModProfile profile)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Folder);
            OlpFile.Write(Find(profile.Name)?.Path ?? NewPath(profile.Name), profile);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Adds a validated imported profile without replacing an existing name or changing any active
    /// selection. Name allocation and writing share the store lock; collisions get a numbered suffix.
    /// Raises <see cref="Changed"/> after saving, on the calling thread.
    /// </summary>
    internal ModProfile AddImported(ModProfile profile)
    {
        OlpFile.Serializer(profile);
        ModProfile imported;
        lock (_gate)
        {
            var name = profile.Name.Trim();
            var candidate = name;
            for (var n = 2; Find(candidate) is not null; n++)
            {
                var suffix = $" ({n})";
                candidate = name[..Math.Min(name.Length, OlpFile.MAX_NAME_LENGTH - suffix.Length)] + suffix;
            }
            imported = profile with { Name = candidate };
            Directory.CreateDirectory(Folder);
            OlpFile.Write(NewPath(candidate), imported);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return imported;
    }

    /// <summary>
    /// Removes the saved profile and every active selection of its name, leaving installation files
    /// and left-alone decisions untouched. Saves cleared selections before removing the file so a
    /// later record cannot recreate it. A failed file removal restores the original selections.
    /// Raises <see cref="Changed"/> after success, on the calling thread.
    /// </summary>
    internal void Delete(string name)
    {
        name = name.Trim();
        lock (_gate)
        {
            var file = Find(name) ?? throw new InvalidProfileException("This profile no longer exists.");
            var original = LoadState();
            var active = new Dictionary<string, string>(original.Active, StringComparer.Ordinal);
            foreach (var game in active.Where(entry => entry.Value.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Key).ToList())
            {
                active.Remove(game);
            }
            SaveState(original with { Active = active });
            try
            {
                File.Delete(file.Path);
            }
            catch
            {
                SaveState(original);
                throw;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <returns>Why the name can't be used for a new profile, or null if it can.</returns>
    public string? ProblemWithNewName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "Give the profile a name.";
        }

        if (trimmed.Length > OlpFile.MAX_NAME_LENGTH)
        {
            return $"Profile names are at most {OlpFile.MAX_NAME_LENGTH} characters.";
        }

        return Get(trimmed) is null ? null : $"There's already a profile called \"{trimmed}\".";
    }

    /// <summary>
    /// The profile the game uses, or null until one is created or chosen for it. There is no
    /// implicit fallback profile: an unsaved placeholder could otherwise be applied as an empty switch.
    /// </summary>
    internal string? ActiveName(string gamePath)
    {
        lock (_gate)
        {
            return LoadState().Active.GetValueOrDefault(gamePath);
        }
    }

    /// <summary>
    /// Records the game's active profile and raises <see cref="Changed"/>. Moves no mods; switching is
    /// <see cref="GameProfiles"/>' job.
    /// </summary>
    internal void SetActive(string gamePath, string name)
    {
        lock (_gate)
        {
            var state = LoadState();
            state.Active[gamePath] = name;
            SaveState(state);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets which profile a game used and the folders it left alone. Raises <see cref="Changed"/> if anything was recorded.</summary>
    internal void ClearActive(string gamePath)
    {
        bool changed;
        lock (_gate)
        {
            var state = LoadState();
            changed = state.Active.Remove(gamePath) | state.LeftAlone.Remove(gamePath);
            if (changed)
            {
                SaveState(state);
            }
        }
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Mod folders the user chose to leave alone in this game, so they aren't asked about again.</summary>
    internal IReadOnlySet<string> LeftAlone(string gamePath)
    {
        lock (_gate)
        {
            return (LoadState().LeftAlone.GetValueOrDefault(gamePath) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Adds folders to the game's left-alone set, kept across sessions. Doesn't raise <see cref="Changed"/>.</summary>
    internal void LeaveAlone(string gamePath, IEnumerable<string> locations)
    {
        lock (_gate)
        {
            var state = LoadState();
            state.LeftAlone[gamePath] = [.. (state.LeftAlone.GetValueOrDefault(gamePath) ?? []).Union(locations, StringComparer.OrdinalIgnoreCase)];
            SaveState(state);
        }
    }

    private IEnumerable<(string Path, ModProfile Profile)> Files()
    {
        if (!Directory.Exists(Folder))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(Folder, "*" + OlpFile.EXTENSION))
        {
            ModProfile profile;
            try
            {
                profile = OlpFile.Read(path);
            }
            catch (Exception e) when (e is InvalidProfileException or IOException or UnauthorizedAccessException)
            {
                continue;
            }
            yield return (path, profile);
        }
    }

    private (string Path, ModProfile Profile)? Find(string name)
    {
        foreach (var file in Files())
        {
            if (string.Equals(file.Profile.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }
        return null;
    }

    // Any name the user types becomes a safe, unique file name; the real name is inside the file.
    private string NewPath(string name)
    {
        var stem = UnsafeFileNameChars().Replace(name.Trim(), "_");
        if (ReservedOnWindows().IsMatch(stem))
        {
            stem = "_" + stem;
        }

        var path = Path.Combine(Folder, stem + OlpFile.EXTENSION);
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(Folder, $"{stem} ({n}){OlpFile.EXTENSION}");
        }

        return path;
    }

    private ProfileState LoadState()
    {
        try
        {
            if (File.Exists(stateFile))
            {
                using var stream = File.OpenRead(stateFile);
                if (JsonSerializer.Deserialize(stream, ProfileJsonContext.Default.ProfileState) is { } state)
                {
                    return state with { Active = new(state.Active, StringComparer.Ordinal), LeftAlone = new(state.LeftAlone, StringComparer.Ordinal) };
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Like settings: a damaged file falls back to defaults and is replaced on the next save.
        }
        return new ProfileState(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
    }

    private void SaveState(ProfileState state) =>
        AtomicFile.Write(stateFile, stream => JsonSerializer.Serialize(stream, state, ProfileJsonContext.Default.ProfileState));

    [GeneratedRegex(@"^(?:CON|PRN|AUX|NUL|COM\d|LPT\d)(?:\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedOnWindows();

    [GeneratedRegex(@"[^\p{L}\p{N} ._-]|^\.+")]
    private static partial Regex UnsafeFileNameChars();
}

/// <param name="Active">Game folder → profile name.</param>
/// <param name="LeftAlone">Game folder → mod folders (relative to <c>FactoryGame</c>) not to ask about again.</param>
internal sealed record ProfileState(Dictionary<string, string> Active, Dictionary<string, List<string>> LeftAlone);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(ProfileState))]
internal sealed partial class ProfileJsonContext : JsonSerializerContext;
