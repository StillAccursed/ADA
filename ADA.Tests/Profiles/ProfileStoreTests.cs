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

    [Fact]
    public void DeletionClearsAllMatchingSelectionsAndKeepsOtherProfilesAndLeftAloneDecisions()
    {
        _store.Save(new("Playthrough", []));
        _store.Save(new("Other", []));
        _store.SetActive("local", "Playthrough");
        _store.SetActive("server", "playthrough");
        _store.SetActive("unrelated", "Other");
        _store.LeaveAlone("local", ["Mods/Unmanaged"]);
        var changed = 0;
        _store.Changed += (_, _) => changed++;

        _store.Delete("PLAYTHROUGH");

        var reloaded = new ProfileStore(_dir.Combine("Profiles"), _dir.Combine("profiles.json"));
        Assert.Equal("Other", Assert.Single(reloaded.List()).Name);
        Assert.Null(reloaded.ActiveName("local"));
        Assert.Null(reloaded.ActiveName("server"));
        Assert.Equal("Other", reloaded.ActiveName("unrelated"));
        Assert.Equal(["Mods/Unmanaged"], reloaded.LeftAlone("local"));
        Assert.Equal(1, changed);
    }

    [Fact]
    public void StateWriteFailureKeepsTheProfileAndActiveSelection()
    {
        _store.Save(new("Keep", []));
        _store.SetActive("game", "Keep");
        Directory.CreateDirectory(_dir.Combine("profiles.json.tmp"));

        var error = Assert.ThrowsAny<Exception>(() => _store.Delete("Keep"));
        Assert.True(error is IOException or UnauthorizedAccessException);

        Assert.NotNull(_store.Get("Keep"));
        Assert.Equal("Keep", _store.ActiveName("game"));
    }
}
