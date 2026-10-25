namespace Overlord.Infrastructure.Ficsit;

/// <summary>Where ficsit.app lives and how Overlord identifies itself to it.</summary>
public static class FicsitApi
{
    public static readonly Uri BaseAddress = new("https://api.ficsit.app/");

    public const string UserAgent = "Overlord/0.1 (+https://github.com/StillAccursed/Overlord)";

    /// <summary>A download link as other tools write it, e.g. <c>https://api.ficsit.app/v1/version/x/Windows/download</c>.</summary>
    public static string AbsoluteLink(string downloadPath) => new Uri(BaseAddress, downloadPath).ToString();
}
