using ADA.Core.Saves;

namespace ADA.ViewModels;

/// <summary>One save in the Saves page's list. Immutable: a re-read builds new items.</summary>
public sealed class SaveItemViewModel(SaveGame save)
{
    public SaveGame Save { get; } = save;

    public string Name => Save.Name;

    public string SessionName => Save.Header.SessionName;

    public string PlayTimeText => SaveText.PlayTime(Save.Header.PlayTime);

    public string SavedText => SaveText.When(Save.Header.SavedAtUtc);

    public string SizeText => ByteSize.Format(Save.Size);

    public bool IsModded => Save.Header.IsModded;

    public bool HasAdvancedGameSettings => Save.Header.AdvancedGameSettings == true;

    public string ModsText => Save.Header.Mods.Count switch
    {
        0 => IsModded ? "Modded" : "No mods",
        1 => "1 mod",
        var count => $"{count} mods",
    };

    /// <summary>Whether the save or session name contains <paramref name="search"/>, ignoring case.</summary>
    internal bool Matches(string search) =>
        Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || SessionName.Contains(search, StringComparison.CurrentCultureIgnoreCase);
}
