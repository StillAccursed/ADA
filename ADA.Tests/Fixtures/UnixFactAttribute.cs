namespace ADA.Tests.Fixtures;

/// <summary>
/// For tests that build Wine prefixes: Wine names drive links <c>c:</c>, <c>y:</c> and so on,
/// which Windows cannot create as file names. The code under test only runs on macOS and Linux.
/// </summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Wine prefix fixtures need 'x:' file names, which Windows does not allow.";
    }
}
