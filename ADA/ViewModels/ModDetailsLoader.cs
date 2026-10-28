using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Core.Resolution;
using ADA.Imaging;
using ADA.Infrastructure.Catalog;
using ADA.Infrastructure.Installing;
using Microsoft.Extensions.Logging;

namespace ADA.ViewModels;

/// <summary>
/// Fills in an open mod's page: versions and changelogs, dependency rows and gallery images.
/// The selected game and installed mods are read as each part is shown, not when loading starts,
/// so a game switched mid-load is reflected. Never throws; failures are shown on the item.
/// </summary>
internal sealed class ModDetailsLoader
{
    private readonly ModCatalogStore _store;
    private readonly IModVersionSource _versions;
    private readonly ModLogos _logos;
    private readonly Func<GameInstall?> _currentGame;
    private readonly Func<IReadOnlyDictionary<string, InstalledMod>> _currentInstalled;
    private readonly ILogger _log;

    internal ModDetailsLoader(ModCatalogStore store, IModVersionSource versions, ModLogos logos,
        Func<GameInstall?> currentGame, Func<IReadOnlyDictionary<string, InstalledMod>> currentInstalled, ILogger log)
    {
        _store = store;
        _versions = versions;
        _logos = logos;
        _currentGame = currentGame;
        _currentInstalled = currentInstalled;
        _log = log;
    }

    /// <summary>Cancelling stops before versions are shown; gallery images already requested still load.</summary>
    internal async Task LoadAsync(ModItemViewModel item, CancellationToken ct)
    {
        var logo = _logos.GetAsync(item.Mod.LogoUrl);
        item.Gallery = ModDescription.ImageUrlsInFormatted(item.FormattedDescription).Select(u => new GalleryImageViewModel(u)).ToList();
        foreach (var image in item.Gallery)
        {
            _ = LoadGalleryImageAsync(image);
        }

        try
        {
            var versions = await _versions.GetVersionsAsync(item.Reference, ct: ct);
            // Lists cached before changelogs were fetched have none; fetch once more.
            if (versions.Count > 0 && versions.All(v => v.Changelog is null))
            {
                versions = await _versions.GetVersionsAsync(item.Reference, refresh: true, ct);
            }

            ShowVersions(item, versions);
            item.Dependencies = await DependencyRowsAsync(item, versions, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Couldn't look up versions of {Mod}", item.Reference);
            item.LatestText = $"Couldn't look up versions: {e.Message}";
        }
        item.Logo ??= await logo;
    }

    private async Task LoadGalleryImageAsync(GalleryImageViewModel image) => image.Image = await _logos.GetAsync(image.Url, 960);

    private void ShowVersions(ModItemViewModel item, IReadOnlyList<CatalogVersion> versions)
    {
        var game = _currentGame();
        var platform = game is null ? TargetPlatform.Windows : ModInstallService.TargetFor(game);
        bool Supports(CatalogVersion v) => game?.Build is { } build && v.TargetFor(platform) is not null
            && GameVersionRange.TryParse(v.GameVersionRange, out var range) && range.Contains(build.Changelist);

        var newestFirst = versions.OrderByDescending(v => v.CreatedAt).ToList();
        item.Versions = newestFirst
            .Select(v => new VersionRowViewModel(v, Supports(v), v.Version == item.InstalledVersion, v.TargetFor(platform)?.Size))
            .ToList();

        var compatible = newestFirst.Where(Supports).ToList();
        var latest = compatible.OrderBy(v => v.Stability).FirstOrDefault();
        item.VersionChoices = [new(null, "Latest compatible"), .. compatible.Select(v => new Option<string?>(v.Version,
            v.Stability == VersionStability.Release ? v.Version : $"{v.Version} ({v.Stability})"))];
        item.SelectedVersionChoice = item.VersionChoices[0];
        item.SizeText = latest?.TargetFor(platform)?.Size is { } size ? ByteSize.Format(size) : null;

        if (game?.Build is not { } gameBuild)
        {
            item.LatestText = null;
            return;
        }
        item.LatestText = latest is null
            ? $"No version supports game build {gameBuild.Changelist} yet."
            : $"Latest for this game: {latest.Version}{(latest.Stability == VersionStability.Release ? "" : $" ({latest.Stability})")}";
    }

    private async Task<IReadOnlyList<DependencyRowViewModel>> DependencyRowsAsync(ModItemViewModel item, IReadOnlyList<CatalogVersion> versions,
        CancellationToken ct)
    {
        // What the version that would be installed needs: the newest compatible, else the newest.
        var shown = item.Versions.FirstOrDefault(v => v.SupportsGame)?.Version ?? item.Versions.FirstOrDefault()?.Version;
        var version = versions.FirstOrDefault(v => v.Version == shown);
        if (version is null)
        {
            return [];
        }

        var installed = _currentInstalled();
        return await Task.Run(() => version.Dependencies
            .Select(d => new DependencyRowViewModel(d.ModReference, _store.Get(d.ModReference)?.Name ?? d.ModReference, d.Condition,
                d.Optional, installed.ContainsKey(d.ModReference)))
            .ToList(), ct);
    }
}
