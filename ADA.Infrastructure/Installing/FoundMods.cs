namespace ADA.Infrastructure.Installing;

/// <summary>A mod folder in the game that ADA didn't install (e.g. Satisfactory Mod Manager did), read from its <c>.uplugin</c>.</summary>
/// <param name="Location">Relative to <c>FactoryGame</c>, e.g. <c>Mods/X</c>.</param>
/// <param name="Dependencies">Mods its <c>.uplugin</c> requires (optional ones left out).</param>
public sealed record FoundMod(string Reference, string Version, string? FriendlyName, string Location,
    IReadOnlyList<string> Dependencies);

/// <param name="Unreadable">Folders that aren't recognisable mods, and why; never adopted.</param>
/// <param name="WrongBuild">
/// Mods whose libraries are all for another platform (a client build copied onto a server), and
/// what to do about it; never adopted, and shown to the user.
/// </param>
public sealed record FoundMods(IReadOnlyList<FoundMod> Mods, IReadOnlyDictionary<string, string> Unreadable,
    IReadOnlyDictionary<string, string>? WrongBuild = null)
{
    internal static readonly FoundMods NONE = new([], new Dictionary<string, string>());

    public IReadOnlyDictionary<string, string> WrongBuild { get; } = WrongBuild ?? new Dictionary<string, string>();
}
