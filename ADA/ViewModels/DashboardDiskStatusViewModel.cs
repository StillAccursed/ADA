using ADA.Infrastructure.IO;

namespace ADA.ViewModels;

/// <summary>Formats a storage snapshot without treating incomplete measurements as zero.</summary>
public sealed class DashboardDiskStatusViewModel(DashboardStorageSnapshot? snapshot = null)
{
    public string ModsSize => Format(snapshot?.Mods);

    public string ArchiveSize => Format(snapshot?.Archives);

    public string TotalSize => snapshot is { Mods.IsComplete: true, Archives.IsComplete: true }
        ? ByteSize.Format(snapshot.Mods.Bytes + snapshot.Archives.Bytes) : "Unknown";

    public string AvailableSpace => Size(snapshot?.GameVolume.AvailableBytes);

    public string TotalCapacity => Size(snapshot?.GameVolume.TotalBytes);

    public bool HasSeparateArchiveVolume => snapshot?.ArchiveVolume.Name is { } archive
        && snapshot.GameVolume.Name is { } game && !string.Equals(archive, game, StringComparison.Ordinal);

    public string ArchiveAvailableSpace => Size(snapshot?.ArchiveVolume.AvailableBytes);

    public string? Problem => snapshot?.Mods.Problem ?? snapshot?.Archives.Problem;

    private static string Size(long? bytes) => bytes is { } size ? ByteSize.Format(size) : "Unknown";

    private static string Format(FolderMeasurement? value) => value switch
    {
        null => "Measuring…",
        { IsComplete: true } => ByteSize.Format(value.Bytes),
        { Bytes: > 0 } => $"{ByteSize.Format(value.Bytes)} (partial)",
        _ => "Unknown",
    };
}
