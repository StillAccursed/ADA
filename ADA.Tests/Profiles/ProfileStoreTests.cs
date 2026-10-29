using ADA.Infrastructure.Profiles;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Profiles;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ProfileStore _store;

    public ProfileStoreTests() => _store = new ProfileStore(_dir.Combine("Profiles"), _dir.Combine("profiles.json"));

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void ForgetsTheActiveProfileAndLeftAloneFoldersButKeepsOtherGames()
    {
        _store.SetActive("sftp://a", "Playthrough");
        _store.LeaveAlone("sftp://a", ["Mods/X"]);
        _store.SetActive("/games/Satisfactory", "Local");

        _store.ClearActive("sftp://a");

        var reloaded = new ProfileStore(_dir.Combine("Profiles"), _dir.Combine("profiles.json"));
        Assert.Null(reloaded.ActiveName("sftp://a"));
        Assert.Empty(reloaded.LeftAlone("sftp://a"));
        Assert.Equal("Local", reloaded.ActiveName("/games/Satisfactory"));
    }

    [Fact]
    public void RaisesChangedOnlyWhenSomethingWasForgotten()
    {
        _store.SetActive("sftp://a", "Playthrough");
        var raised = 0;
        _store.Changed += (_, _) => raised++;

        _store.ClearActive("sftp://a");
        _store.ClearActive("sftp://a");

        Assert.Equal(1, raised);
    }
}
