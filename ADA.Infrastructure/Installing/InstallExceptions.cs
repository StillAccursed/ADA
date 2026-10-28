namespace ADA.Infrastructure.Installing;

/// <summary>A mod change failed in a way the user should see; the message explains it and is shown as-is.</summary>
public class InstallException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class GameRunningException()
    : InstallException("Satisfactory is running. Close the game before changing mods; it locks mod files.");

/// <summary>
/// Another check or change already holds this installation. Background checks treat this as
/// "skip"; user actions show it, since the earlier operation will finish on its own.
/// </summary>
public sealed class InstallationBusyException()
    : InstallException("Another operation is already checking or changing this installation.");

/// <summary>Thrown by test checkpoints to stop the engine dead, as a crash or power cut would.</summary>
internal sealed class SimulatedCrashException(string checkpoint) : Exception("Simulated crash at " + checkpoint);
