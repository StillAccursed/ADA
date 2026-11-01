using ADA.Core.Activity;
using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Core.Profiles;
using ADA.Infrastructure.Activity;
using ADA.Infrastructure.Profiles;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Profiles;

public sealed class ProfileTransferTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ProfileStore _store;
    private readonly ProfileTransfer _transfer;

    public ProfileTransferTests()
    {
        _store = new(_dir.Combine("Profiles"), _dir.Combine("profiles.json"));
        _transfer = new(_store, new FakeVersionSource((reference, _) =>
            Task.FromResult<IReadOnlyList<CatalogVersion>>([CatalogFixtures.Version(reference, "1.0.0")])));
    }

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("olp")]
    [InlineData("smmprofile")]
    [InlineData("OLP")]
    [InlineData("SMMPROFILE")]
    public async Task ExportsAndImportsExactVersionsWithoutActivating(string extension)
    {
        var profile = new ModProfile("Playthrough", [new("SML", "1.0.0"), new("Weather", "1.0.0")]);
        var game = Game();
        var path = _dir.Combine($"Export.{extension}");

        await _transfer.ExportAsync(profile, game, path);
        var imported = await _transfer.ImportAsync(path);

        Assert.Equal(profile.Name, imported.Name);
        Assert.Equal(profile.Mods.OrderBy(m => m.Name), imported.Mods.OrderBy(m => m.Name));
        Assert.Null(_store.ActiveName(game.GamePath));
        var saved = Assert.Single(_store.List());
        Assert.Equal(imported.Name, saved.Name);
        Assert.Equal(imported.Mods, saved.Mods);
        if (extension.Equals("smmprofile", StringComparison.OrdinalIgnoreCase))
        {
            var document = SmmProfileFile.Read(path);
            Assert.Equal(513885, document.Metadata!.GameVersion);
            Assert.Equal(["Windows"], document.Profile!.RequiredTargets);
        }
    }

    [Fact]
    public async Task DuplicateImportPreservesOriginalAndItsActiveSelection()
    {
        var profile = new ModProfile(new string('A', OlpFile.MAX_NAME_LENGTH), [new("Original", "1.0.0")]);
        _store.Save(profile);
        _store.SetActive("game", profile.Name);
        var path = _dir.Combine("incoming.olp");
        OlpFile.Write(path, profile with { Name = profile.Name.ToLowerInvariant(), Mods = [new("Imported", "2.0.0")] });

        var imported = await _transfer.ImportAsync(path);
        var second = await _transfer.ImportAsync(path);

        Assert.EndsWith(" (2)", imported.Name);
        Assert.EndsWith(" (3)", second.Name);
        Assert.Equal(OlpFile.MAX_NAME_LENGTH, imported.Name.Length);
        Assert.Equal(profile.Name, _store.ActiveName("game"));
        Assert.Equal(profile.Mods, _store.Get(profile.Name)!.Mods);
        Assert.Equal("Imported", Assert.Single(imported.Mods).Name);
    }

    [Fact]
    public async Task SmmImportsLockedDependenciesButOmitsDisabledMods()
    {
        var path = Smm("""
            { "Enabled": { "enabled": true, "version": ">=0.0.0" },
              "Disabled": { "enabled": false, "version": ">=0.0.0" } }
            """, """
            { "Enabled": { "version": "1.0.0" }, "Disabled": { "version": "2.0.0" },
              "Dependency": { "version": "3.0.0" } }
            """);

        var imported = await _transfer.ImportAsync(path);

        Assert.Equal([new ProfileMod("Dependency", "3.0.0"), new ProfileMod("Enabled", "1.0.0")],
            imported.Mods.OrderBy(m => m.Name));
    }

    [Fact]
    public async Task MissingEnabledLockRefusesImportWithoutSavingPartialProfile()
    {
        var path = Smm("""{ "Missing": { "version": ">=0.0.0" } }""", "{}");

        var error = await Assert.ThrowsAsync<InvalidProfileException>(() => _transfer.ImportAsync(path));

        Assert.Contains("Missing", error.Message);
        Assert.Empty(_store.List());
    }

    [Theory]
    [InlineData("broken.olp", "{invalid")]
    [InlineData("broken.olp", """{ "name": "P", "mods": [{ "name": "A", "version": ">=1.0.0" }] }""")]
    [InlineData("broken.smmprofile", """{ "profile": { "name": "P" } }""")]
    [InlineData("broken.txt", "{}")]
    public async Task InvalidImportsLeaveTheLibraryIntact(string name, string json)
    {
        _store.Save(new("Existing", []));

        await Assert.ThrowsAsync<InvalidProfileException>(() => _transfer.ImportAsync(_dir.WriteFile(name, json)));

        Assert.Equal("Existing", Assert.Single(_store.List()).Name);
    }

    [Fact]
    public async Task FailedSmmValidationPreservesAnExistingDestination()
    {
        var path = _dir.WriteFile("existing.smmprofile", "original content");

        await Assert.ThrowsAsync<InvalidProfileException>(() =>
            _transfer.ExportAsync(new("Unavailable", [new("Gone", "9.0.0")]), Game(), path));

        Assert.Equal("original content", File.ReadAllText(path));
    }

    [Fact]
    public async Task NativeExportWorksOfflineWithoutAKnownGameBuild()
    {
        var offline = new ProfileTransfer(_store, new FakeVersionSource((_, _) => throw new IOException("Offline")));
        var game = Game() with { Build = null };
        var path = _dir.Combine("offline.olp");

        await offline.ExportAsync(new("Offline", []), game, path);

        Assert.Equal("Offline", OlpFile.Read(path).Name);
        await Assert.ThrowsAsync<InvalidProfileException>(() => offline.ExportAsync(new("Offline", []), game, path + ".smmprofile"));
        Assert.False(File.Exists(path + ".smmprofile"));
    }

    [Fact]
    public async Task CancelledImportDoesNotAddAProfile()
    {
        var path = _dir.Combine("cancelled.olp");
        OlpFile.Write(path, new("Cancelled", []));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _transfer.ImportAsync(path, cancellation.Token));

        Assert.Empty(_store.List());
    }

    private GameInstall Game() => new(_dir.Combine("Game"), GameLauncher.Steam, GameHost.Windows,
        GameKind.Client, null, "Test", new(513885, "1.2"));

    private string Smm(string requested, string locked) => _dir.WriteFile("incoming.smmprofile", $$"""
        { "profile": { "name": "Imported", "mods": {{requested}} },
          "lockfile": { "version": 1, "mods": {{locked}} } }
        """);

    [Fact]
    public async Task RecordsImportsAndExports()
    {
        var activity = new ActivityLog(_dir.Combine("activity.jsonl"));
        var transfer = new ProfileTransfer(_store, new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([])), activity);
        var path = _dir.Combine("Atria.olp");

        await transfer.ExportAsync(new ModProfile("Atria", [new("SML", "1.0.0")]), Game(), path);
        await transfer.ImportAsync(path);
        await Assert.ThrowsAsync<InvalidProfileException>(() => transfer.ImportAsync(_dir.Combine("notes.txt")));

        var entries = await activity.ReadAsync();
        Assert.Equal(["Importing notes.txt failed", "Imported profile Atria from Atria.olp", "Exported Atria to Atria.olp"],
            entries.Select(e => e.Summary));
        Assert.Equal([ActivityOutcome.Failed, ActivityOutcome.Succeeded, ActivityOutcome.Succeeded], entries.Select(e => e.Outcome));
        Assert.Equal("Choose an .olp or .smmprofile file.", entries[0].Error);
    }
}
