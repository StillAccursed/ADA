using ADA.Core.Games;
using ADA.Infrastructure.IO;
using ADA.Infrastructure.Servers;

namespace ADA.Infrastructure.Installing;

/// <summary>The files of an install: on this machine, or on the connected server.</summary>
/// <param name="servers">Null where only local installs exist (tests, and before servers are wired up).</param>
public sealed class GameFilesFactory(ServerConnections? servers = null)
{
    /// <exception cref="ServerUnavailableException">The install is a server that isn't connected right now.</exception>
    internal IGameFiles For(GameInstall game)
    {
        if (!game.IsRemote)
        {
            return new LocalGameFiles(Path.Combine(game.GamePath, "FactoryGame"));
        }
        return servers?.FilesFor(game) ?? throw new ServerUnavailableException("Servers aren't available here.");
    }
}
