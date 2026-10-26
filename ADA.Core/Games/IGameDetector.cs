namespace ADA.Core.Games;

public interface IGameDetector
{
    /// <summary>
    /// Finds every install this detector can prove through launcher records. Never throws for a
    /// missing or malformed launcher; that launcher simply contributes nothing.
    /// </summary>
    IReadOnlyList<GameInstall> Detect();
}
