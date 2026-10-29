namespace ADA.Infrastructure.IO;

/// <summary>
/// Paths inside a game install, relative to <c>FactoryGame</c> with <c>/</c> separators, so they
/// mean the same on any OS and over SFTP. The same form as the locations in install records.
/// </summary>
internal static class GamePaths
{
    /// <summary>
    /// Whether <paramref name="path"/> stays inside the install: not rooted, no <c>\</c> or <c>:</c>,
    /// and no <c>.</c> or <c>..</c> segments. The same rule <c>ModArchive</c> applies to archive
    /// entries. The empty string is the install root itself.
    /// </summary>
    public static bool IsSafe(string path) =>
        path.Length == 0 || (!path.StartsWith('/') && !Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains(':')
                             && path.Split('/').All(segment => segment is not ("." or "..")));

    public static string Combine(string parent, string child) => parent.Length == 0 ? child : $"{parent}/{child}";

    /// <returns>The containing folder; the empty string for a top-level entry.</returns>
    public static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }
}
