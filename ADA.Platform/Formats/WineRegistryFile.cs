using System.Text;

namespace ADA.Platform.Formats;

/// <summary>
/// Reads string values from a Wine registry hive on disk (<c>system.reg</c> is HKLM,
/// <c>user.reg</c> is HKCU). We only need a handful of launcher keys, so this streams the file
/// rather than loading tens of thousands of keys into memory.
/// </summary>
public static class WineRegistryFile
{
    /// <param name="regFilePath">Path to the .reg file.</param>
    /// <param name="keyPath">Key relative to the hive root, single backslashes, e.g. <c>Software\Valve\Steam</c>.</param>
    /// <param name="valueName">Name of a string value under that key.</param>
    public static string? ReadString(string regFilePath, string keyPath, string valueName)
    {
        if (!File.Exists(regFilePath))
            return null;

        try
        {
            var inKey = false;
            foreach (var line in File.ReadLines(regFilePath))
            {
                if (line.StartsWith('['))
                {
                    var end = line.IndexOf(']');
                    inKey = end > 0 && string.Equals(Unescape(line[1..end]), keyPath, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inKey || !line.StartsWith('"'))
                    continue;

                if (TryParseStringValue(line, out var name, out var value)
                    && string.Equals(name, valueName, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static bool TryParseStringValue(string line, out string name, out string value)
    {
        name = value = "";
        var nameEnd = FindClosingQuote(line, 1);
        if (nameEnd < 0 || nameEnd + 2 >= line.Length || line[nameEnd + 1] != '=' || line[nameEnd + 2] != '"')
            return false; // not a plain REG_SZ (dword:, hex:, str(2): ...)

        var valueEnd = FindClosingQuote(line, nameEnd + 3);
        if (valueEnd < 0)
            return false;

        name = Unescape(line[1..nameEnd]);
        value = Unescape(line[(nameEnd + 3)..valueEnd]);
        return true;
    }

    private static int FindClosingQuote(string s, int start)
    {
        for (var i = start; i < s.Length; i++)
        {
            if (s[i] == '\\')
                i++;
            else if (s[i] == '"')
                return i;
        }
        return -1;
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\'))
            return s;

        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
                i++;
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
