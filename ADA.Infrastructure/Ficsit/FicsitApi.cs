namespace ADA.Infrastructure.Ficsit;

/// <summary>Where ficsit.app lives and how ADA identifies itself to it.</summary>
public static class FicsitApi
{
    /// <summary>Identifies ADA and its repository in requests to the mod API.</summary>
    public const string USER_AGENT = "ADA/0.1 (+https://github.com/StillAccursed/ADA)";

    public static readonly Uri BaseAddress = new("https://api.ficsit.app/");

    /// <summary>A download link as other tools write it, e.g. <c>https://api.ficsit.app/v1/version/x/Windows/download</c>.</summary>
    public static string AbsoluteLink(string downloadPath) => new Uri(BaseAddress, downloadPath).ToString();
}
