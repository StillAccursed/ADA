namespace ADA.Core.Saves;

/// <summary>Who can join the session from the game's menu; the values match the game's enum.</summary>
public enum SessionVisibility : byte
{
    Private = 0,
    FriendsOnly = 1,
}

/// <summary>A mod recorded in a save when it was saved, as the game wrote it.</summary>
/// <param name="Reference">The mod reference, the same key ficsit.app and profiles use.</param>
/// <param name="Name">Display name at the time of saving; the reference when the save didn't record one.</param>
/// <param name="Version">The version that was installed when saving, if recorded.</param>
public sealed record SaveMod(string Reference, string Name, string? Version);

/// <summary>
/// What a save file's header says about it, read without decompressing the world that follows.
/// </summary>
/// <param name="HeaderVersion">Layout version of the header itself; 14 since 1.0.</param>
/// <param name="SaveVersion">Version of the world data's format.</param>
/// <param name="BuildVersion">Changelist of the game that wrote the save, comparable with <c>GameBuild.Changelist</c>.</param>
/// <param name="SaveName">The name shown in the game's load menu; null before header version 14, where the file name is used.</param>
/// <param name="SessionName">The playthrough this save belongs to. Blueprints are stored per session.</param>
/// <param name="PlayTime">Total in-game time for the session when this save was made.</param>
/// <param name="SavedAtUtc">When the game wrote the save, read as UTC.</param>
/// <param name="Visibility">Who could join; a value outside <see cref="SessionVisibility"/>'s names is one ADA doesn't know.</param>
/// <param name="IsModded">Whether the game marked the save as modded; it stays marked after mods are removed.</param>
/// <param name="Mods">Mods recorded in the save; empty when none were, or the list couldn't be read.</param>
/// <param name="Options">The map options, with account identity left out.</param>
/// <param name="SaveIdentifier">The game's id for this save; null where the header version's tail isn't read.</param>
/// <param name="IsPartitionedWorld">Whether the world uses 1.0's partitioned layout; null where the tail isn't read.</param>
/// <param name="AdvancedGameSettings">Whether Advanced Game Settings (creative mode) are on; null where the tail isn't read.</param>
public sealed record SaveHeader(
    int HeaderVersion,
    int SaveVersion,
    int BuildVersion,
    string? SaveName,
    string MapName,
    string SessionName,
    TimeSpan PlayTime,
    DateTime SavedAtUtc,
    SessionVisibility Visibility,
    bool IsModded,
    IReadOnlyList<SaveMod> Mods,
    SaveMapOptions Options,
    string? SaveIdentifier,
    bool? IsPartitionedWorld,
    bool? AdvancedGameSettings);
