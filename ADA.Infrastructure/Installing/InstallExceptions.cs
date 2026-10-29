using ADA.Core.Games;

namespace ADA.Infrastructure.Installing;

/// <summary>A mod change failed in a way the user should see; the message explains it and is shown as-is.</summary>
public class InstallException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class GameRunningException : InstallException
{
    private const string GAME_MESSAGE = "Satisfactory is running. Close the game before changing mods; it locks mod files.";
    private const string SERVER_MESSAGE = "The server is running. Stop it before changing its mods; it locks mod files.";

    public GameRunningException() : base(GAME_MESSAGE)
    {
    }

    private GameRunningException(string message) : base(message)
    {
    }

    /// <summary>The message that fits <paramref name="game"/>: a server says to stop the server.</summary>
    public static GameRunningException For(GameInstall game) => game.IsRemote ? new(SERVER_MESSAGE) : new();
}

/// <summary>
/// Another check or change already holds this installation. Background checks treat this as
/// "skip"; user actions show it, since the earlier operation will finish on its own.
/// </summary>
public sealed class InstallationBusyException()
    : InstallException("Another operation is already checking or changing this installation.");

/// <summary>
/// The changes worked out under the installation's reservation differ from the ones the user
/// confirmed. Nothing was downloaded or changed; <see cref="Plan"/> is what would run now.
/// </summary>
public sealed class PlanChangedException(ModChangePlan plan)
    : InstallException("Something changed since this preview. Review the changes and choose Switch again.")
{
    public ModChangePlan Plan { get; } = plan;
}

/// <summary>Thrown by test checkpoints to stop the engine dead, as a crash or power cut would.</summary>
internal sealed class SimulatedCrashException(string checkpoint) : Exception("Simulated crash at " + checkpoint);
