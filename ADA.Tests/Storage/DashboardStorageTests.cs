using ADA.Infrastructure.IO;
using ADA.Tests.Fixtures;
using ADA.ViewModels;

namespace ADA.Tests.Storage;

public sealed class DashboardStorageTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Measures_mods_and_nested_game_features_once_and_counts_only_kept_archives()
    {
        var mods = _dir.Combine("Mods");
        var archives = _dir.Combine("Archives");
        Directory.CreateDirectory(Path.Combine(mods, "GameFeatures", "A"));
        Directory.CreateDirectory(archives);
        File.WriteAllBytes(Path.Combine(mods, "one.bin"), new byte[10]);
        File.WriteAllBytes(Path.Combine(mods, "GameFeatures", "A", "two.bin"), new byte[20]);
        File.WriteAllBytes(Path.Combine(archives, "one.smod"), new byte[50]);
        File.WriteAllBytes(Path.Combine(archives, "one.part"), new byte[99]);

        var snapshot = await new DashboardStorageReader(archives).ReadAsync(mods);

        Assert.Equal(30, snapshot.Mods.Bytes);
        Assert.Equal(50, snapshot.Archives.Bytes);
        Assert.True(snapshot.Mods.IsComplete);
        Assert.Equal("80 bytes", new DashboardDiskStatusViewModel(snapshot).TotalSize);
    }

    [Fact]
    public void Missing_folder_is_zero_but_a_file_in_place_of_folder_is_unknown()
    {
        Assert.Equal(new FolderMeasurement(0, true), DashboardStorageReader.MeasureFolder(_dir.Combine("Missing"), false));
        var path = _dir.Combine("NotDirectory");
        File.WriteAllText(path, "file");
        Assert.False(DashboardStorageReader.MeasureFolder(path, false).IsComplete);
    }

    [Fact]
    public void Incomplete_measurements_do_not_contribute_a_false_zero_to_the_total()
    {
        var vm = new DashboardDiskStatusViewModel(new(new(10, false, "Denied"), new(20, true),
            new("Game", 100, 1000), new("Archives", 200, 2000)));
        Assert.Equal("10 bytes (partial)", vm.ModsSize);
        Assert.Equal("Unknown", vm.TotalSize);
        Assert.True(vm.HasSeparateArchiveVolume);
        Assert.Equal("200 bytes", vm.ArchiveAvailableSpace);
    }

    [Fact]
    public void Cancelled_traversal_is_discarded()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => DashboardStorageReader.MeasureFolder(_dir.Path, false, cts.Token));
    }

    [UnixFact]
    public void Directory_links_cannot_cycle_or_double_count_files()
    {
        var mods = _dir.Combine("Mods");
        Directory.CreateDirectory(mods);
        File.WriteAllBytes(Path.Combine(mods, "one.bin"), new byte[10]);
        Directory.CreateSymbolicLink(Path.Combine(mods, "Cycle"), mods);
        Assert.Equal(new FolderMeasurement(10, true), DashboardStorageReader.MeasureFolder(mods, false));
    }
}
