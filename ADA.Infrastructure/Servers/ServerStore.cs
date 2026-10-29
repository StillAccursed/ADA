using System.Text.Json;
using System.Text.Json.Serialization;
using ADA.Core.Servers;
using ADA.Infrastructure.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Infrastructure.Servers;

/// <summary>A server can't be saved as it is; the message says why and is shown as-is.</summary>
public sealed class InvalidServerException(string message) : Exception(message);

/// <summary>
/// The saved servers, in <c>Config/servers.json</c>. Holds connection details only; secrets are
/// in <see cref="ServerSecrets"/>. At most one server is marked connected.
/// </summary>
public sealed class ServerStore(string path, ILogger<ServerStore>? log = null)
{
    public const int MAX_NAME_LENGTH = 64;

    private readonly ILogger _log = log ?? NullLogger<ServerStore>.Instance;
    private readonly Lock _gate = new();
    private List<ServerConnection>? _servers;

    /// <summary>Raised on the saving thread after a write, outside the store's lock.</summary>
    public event EventHandler? Changed;

    /// <summary>Every saved server, sorted by name.</summary>
    public IReadOnlyList<ServerConnection> All
    {
        get
        {
            lock (_gate)
            {
                return Load().OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
        }
    }

    /// <summary>The server the user left connected, if any.</summary>
    public ServerConnection? Connected
    {
        get
        {
            lock (_gate)
            {
                return Load().FirstOrDefault(s => s.Connected);
            }
        }
    }

    public ServerConnection? Find(Guid id)
    {
        lock (_gate)
        {
            return Load().FirstOrDefault(s => s.Id == id);
        }
    }

    /// <returns>Why <paramref name="server"/> can't be saved, or null if it can.</returns>
    public string? ProblemWith(ServerConnection server)
    {
        lock (_gate)
        {
            return Problem(server, Load());
        }
    }

    /// <summary>Adds the server, or replaces the saved one with the same <see cref="ServerConnection.Id"/>.</summary>
    /// <exception cref="InvalidServerException">See <see cref="ProblemWith"/>.</exception>
    public void Save(ServerConnection server)
    {
        lock (_gate)
        {
            var servers = Load();
            if (Problem(server, servers) is { } problem)
            {
                throw new InvalidServerException(problem);
            }
            server = server with { Name = server.Name.Trim(), Address = server.Address.Trim(), User = server.User.Trim() };
            var index = servers.FindIndex(s => s.Id == server.Id);
            if (index >= 0)
            {
                servers[index] = server;
            }
            else
            {
                servers.Add(server);
            }
            if (server.Connected)
            {
                // Only one server is connected at a time; this one takes over.
                servers = servers.Select(s => s.Id == server.Id ? s : s with { Connected = false }).ToList();
            }
            Write(servers);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Marks <paramref name="id"/> as the connected server and every other as not, in one write. Null disconnects all.</summary>
    public void SetConnected(Guid? id)
    {
        lock (_gate)
        {
            var servers = Load();
            if (servers.All(s => s.Connected == (s.Id == id)))
            {
                return;
            }
            Write(servers.Select(s => s with { Connected = s.Id == id }).ToList());
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <returns>False if no server had that id.</returns>
    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            var servers = Load();
            if (servers.RemoveAll(s => s.Id == id) == 0)
            {
                return false;
            }
            Write(servers);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static string? Problem(ServerConnection server, List<ServerConnection> saved)
    {
        var name = server.Name.Trim();
        if (name.Length == 0)
        {
            return "Give the server a name.";
        }
        if (name.Length > MAX_NAME_LENGTH)
        {
            return $"The name can be at most {MAX_NAME_LENGTH} characters.";
        }
        if (saved.Any(s => s.Id != server.Id && s.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
        {
            return $"A server called {name} already exists.";
        }
        if (server.Transport == ServerTransport.Folder)
        {
            // No login: only the folder is needed, and the address is optional (for the running check).
            if (server.ApiPort is < 1 or > 65535)
            {
                return "Ports must be between 1 and 65535.";
            }
            return string.IsNullOrWhiteSpace(server.GameRoot) ? "Choose the server folder." : null;
        }
        if (string.IsNullOrWhiteSpace(server.Address))
        {
            return "Enter the server's address.";
        }
        if (server.Port is < 1 or > 65535 || server.ApiPort is < 1 or > 65535)
        {
            return "Ports must be between 1 and 65535.";
        }
        if (string.IsNullOrWhiteSpace(server.User))
        {
            return "Enter the user name to log in with.";
        }
        if (string.IsNullOrWhiteSpace(server.GameRoot))
        {
            return "Enter the server folder.";
        }
        if (server.Transport == ServerTransport.Ftp && server.Auth != ServerAuth.Password)
        {
            return "FTP servers log in with a password.";
        }
        if (server.Auth == ServerAuth.PrivateKey && string.IsNullOrWhiteSpace(server.PrivateKeyPath))
        {
            return "Choose the private key file.";
        }
        return null;
    }

    /// <summary>
    /// The saved list, read once. A damaged file is moved aside rather than overwritten, so the
    /// user's servers can still be recovered from it; ADA then starts with none.
    /// </summary>
    private List<ServerConnection> Load()
    {
        if (_servers is not null)
        {
            return _servers;
        }

        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                return _servers = JsonSerializer.Deserialize(stream, ServerJsonContext.Default.ListServerConnection) ?? [];
            }
        }
        catch (JsonException e)
        {
            var aside = $"{path}.damaged-{DateTime.UtcNow:yyyyMMddHHmmss}";
            _log.LogError(e, "Saved servers are unreadable; moved to {Aside}", aside);
            File.Move(path, aside);
        }
        return _servers = [];
    }

    private void Write(List<ServerConnection> servers)
    {
        AtomicFile.Write(path, stream => JsonSerializer.Serialize(stream, servers, ServerJsonContext.Default.ListServerConnection));
        _servers = servers;
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<ServerConnection>))]
internal sealed partial class ServerJsonContext : JsonSerializerContext;
