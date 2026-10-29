using ADA.Core.Games;

namespace ADA.Infrastructure.Servers;

/// <summary>Answers "is this install running?" for local installs and the connected server.</summary>
/// <param name="isLocalRunning">This machine's process check; Platform's, passed in because Infrastructure doesn't reference it.</param>
public sealed class GameRunningChecks(ServerConnections servers, Func<CancellationToken, Task<bool>> isLocalRunning)
{
    public Task<bool> IsRunningAsync(GameInstall game, CancellationToken ct) =>
        game.IsRemote ? servers.IsRunningAsync(game, ct) : isLocalRunning(ct);
}
