using System.Text.RegularExpressions;
using Semver;

namespace Overlord.Core.Catalog;

/// <summary>The shapes ficsit.app allows for mod references and exact versions.</summary>
public static partial class ModIdentifiers
{
    public const int MaxReferenceLength = 100;

    /// <summary>A plain identifier such as <c>WeatherExtended</c>: letters, digits and underscores.</summary>
    public static bool IsValidReference(string? reference) => reference is not null && Reference().IsMatch(reference);

    /// <summary>A strict semver 2.0 version such as <c>1.1.0</c> or <c>2.0.0-beta.1</c>; not a range.</summary>
    public static bool IsExactVersion(string? version) =>
        version is not null && SemVersion.TryParse(version, SemVersionStyles.Strict, out _);

    [GeneratedRegex("^[A-Za-z0-9_]{1,100}$")]
    private static partial Regex Reference();
}
