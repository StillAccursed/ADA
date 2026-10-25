namespace Overlord.Core.Profiles;

/// <param name="Name">The mod reference, e.g. <c>WeatherExtended</c> (not its display name).</param>
/// <param name="Version">An exact version, e.g. <c>1.1.0</c>.</param>
public sealed record ProfileMod(string Name, string Version);

/// <summary>A named set of mods at exact versions, as stored in a <c>.olp</c> file.</summary>
public sealed record ModProfile(string Name, IReadOnlyList<ProfileMod> Mods);
