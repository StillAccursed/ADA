namespace ADA.Infrastructure.Installing;

public enum InstallStage
{
    Downloading,
    Extracting,
    Committing,
}

internal sealed record InstallProgress(string Reference, InstallStage Stage, int FilesDone, int FilesTotal);

/// <param name="PreviousVersions">
/// Each changed mod's version before the transaction (reference → version), for describing updates and removals;
/// null where it wasn't read.
/// </param>
public sealed record TransactionResult(
    IReadOnlyList<InstalledMod> Installed,
    IReadOnlyList<string> Uninstalled,
    IReadOnlyDictionary<string, string>? PreviousVersions = null);

/// <param name="RolledForward">Verified changes that were completed.</param>
/// <param name="Discarded">Unverified changes that were dropped; those mods are as they were.</param>
public sealed record RecoveryResult(
    bool HadInterruptedTransaction,
    IReadOnlyList<string> RolledForward,
    IReadOnlyList<string> Discarded,
    IReadOnlyList<string> RolledBack)
{
    internal static readonly RecoveryResult CLEAN = new(false, [], [], []);
}

public enum ModHealth
{
    Ok,
    Damaged,
    Missing,
}

public sealed record ModCheck(string Reference, ModHealth Health, IReadOnlyList<string> Problems);

/// <param name="Unmanaged">Mod folders ADA did not install; left alone.</param>
public sealed record CheckResult(IReadOnlyList<ModCheck> Mods, IReadOnlyList<string> Unmanaged)
{
    public IEnumerable<ModCheck> NeedingRepair => Mods.Where(m => m.Health != ModHealth.Ok);
}
