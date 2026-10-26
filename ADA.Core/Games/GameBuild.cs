namespace ADA.Core.Games;

/// <summary>
/// The game build an install is running, read from the game's own files rather than the launcher.
/// </summary>
/// <param name="Changelist">
/// The build's changelist (CL) number, e.g. 513885. Mods and SML declare compatible games as a
/// range over this number (<c>"GameVersion": "&gt;=513885"</c> in a <c>.uplugin</c>).
/// </param>
/// <param name="GameVersion">Human-readable version, e.g. <c>1.2.4.1</c>.</param>
public sealed record GameBuild(int Changelist, string? GameVersion);
