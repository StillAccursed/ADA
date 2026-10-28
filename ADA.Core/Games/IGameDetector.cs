namespace ADA.Core.Games;

/// <summary>Finds Satisfactory installs on one OS; <c>GameDetection.ForCurrentOS</c> picks the right one.</summary>
public interface IGameDetector
{
    /// <summary>
    /// Finds every install this detector can prove through launcher records. Never throws for a
    /// missing or malformed launcher; that launcher simply contributes nothing.
    /// </summary>
    IReadOnlyList<GameInstall> Detect();
}
