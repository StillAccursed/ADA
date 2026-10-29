namespace ADA.Tests.Fixtures;

/// <summary>
/// For tests that use this machine's real secret store (Keychain, Credential Manager, Secret
/// Service). They can make the OS ask for permission, so they only run when
/// <c>ADA_TEST_SECRET_STORE=1</c> is set, and are reported as skipped otherwise.
/// </summary>
public sealed class SecretStoreFactAttribute : FactAttribute
{
    public SecretStoreFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ADA_TEST_SECRET_STORE") != "1")
        {
            Skip = "Uses the real OS secret store; set ADA_TEST_SECRET_STORE=1 to run it.";
        }
    }
}
