using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Infrastructure.Downloads;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Settings;
using ADA.Tests.Fixtures;
using ADA.Tests.Installing;
using ADA.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.ViewModels;

/// <summary>The Dashboard's prompt for mods another manager installed.</summary>
public sealed class FoundModsPromptTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly GameFolder _game;
    private readonly ProfileStore _store;

    public FoundModsPromptTests()
    {
        _game = new GameFolder(_dir.Combine("Satisfactory"));
        _store = new ProfileStore(_dir.Combine("Profiles"), _dir.Combine("profiles.json"));
        var folder = _game.ModFolder("Mods/SML");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SML.uplugin"), """{ "SemVersion": "3.12.0", "FriendlyName": "Satisfactory Mod Loader" }""");
    }

    public void Dispose() => _dir.Dispose();

    /// <param name="withProfile">Give the game an active profile first, as after the first-run prompt.</param>
    private async Task<GameInstallViewModel> CardAsync(bool withProfile = true)
    {
        if (withProfile)
        {
            _store.Save(new("Playthrough", []));
            _store.SetActive(_game.Root, "Playthrough");
        }
        var service = new ModInstallService(
            new SettingsStore(_dir.Combine("settings.json")),
            new ArchiveDownloader(new HttpClient(new FakeHttpHandler()), _dir.Combine("Archives")),
            new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([])),
            _ => Task.FromResult(false));
        var install = new GameInstall(_game.Root, GameLauncher.Steam, GameHost.Windows, GameKind.Client, null, "test");
        var card = new GameInstallViewModel(install, service, new ModResolutionService(new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([])), service), new GameProfiles(_store, service), NullLogger<GameInstallViewModel>.Instance);
        await card.CheckAsync();
        return card;
    }

    [Fact]
    public async Task Asks_about_found_mods_after_the_launch_check()
    {
        var card = await CardAsync();

        Assert.True(card.ShowFoundChoice);
        Assert.Equal("Found 1 mod installed by another mod manager", card.FoundTitle);
        Assert.Equal("Satisfactory Mod Loader", card.FoundNames);
        Assert.Equal("Add to \"Playthrough\"", card.ImportToCurrentText);
    }

    [Fact]
    public async Task Without_a_profile_found_mods_can_only_go_into_a_new_one()
    {
        var card = await CardAsync(withProfile: false);

        Assert.True(card.ShowFoundChoice);
        Assert.Equal("Add to current profile", card.ImportToCurrentText);
        Assert.False(card.ImportToCurrentCommand.CanExecute(null));
    }

    [Fact]
    public async Task Adding_to_the_current_profile_closes_the_prompt()
    {
        var card = await CardAsync();

        await card.ImportToCurrentCommand.ExecuteAsync(null);

        Assert.False(card.HasFoundMods);
        Assert.StartsWith("Added 1 mod to \"Playthrough\"", card.Notice);
        Assert.Contains("1 mod installed", card.ModsSummary);
    }

    [Fact]
    public async Task A_new_profile_needs_a_free_name_then_can_be_switched_to()
    {
        _store.Save(new("Atria", []));
        var card = await CardAsync();
        card.ChooseNewProfileCommand.Execute(null);

        card.NewProfileName = "atria";
        await card.CreateNewProfileCommand.ExecuteAsync(null);
        Assert.Equal("There's already a profile called \"atria\".", card.NewProfileProblem);
        Assert.True(card.ShowNewProfile);

        card.NewProfileName = "From SMM";
        await card.CreateNewProfileCommand.ExecuteAsync(null);

        Assert.False(card.HasFoundMods);
        Assert.Equal("From SMM", card.ProfileName);
    }
}
