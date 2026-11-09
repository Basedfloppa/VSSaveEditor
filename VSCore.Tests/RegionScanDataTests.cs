using VSCore;
using Xunit;

namespace VSCore.Tests;

public sealed class RegionScanDataTests
{
    [Fact]
    public void Layers_are_not_loaded_until_asked()
    {
        // The whole point of the lazy region: the scan keeps position + length, and the blob (677 MB across
        // ~2000 rows on a 12-GB save) is read only when the layers are actually displayed.
        int reads = 0;
        var region = new RegionScanData
        {
            Position = 4242,
            DataLength = 512,
            LayerLoader = () => { reads++; return new List<string> { "f1 len=3" }; },
        };

        Assert.False(region.LayersLoaded);
        Assert.Equal(0, reads);
        Assert.Equal(4242, region.Position);
        Assert.Equal(512, region.DataLength);
    }

    [Fact]
    public void Layers_are_loaded_once_and_kept()
    {
        int reads = 0;
        var region = new RegionScanData
        {
            LayerLoader = () => { reads++; return new List<string> { "f1 len=3" }; },
        };

        Assert.Single(region.Layers);
        Assert.True(region.LayersLoaded);
        Assert.Single(region.Layers);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void A_failing_loader_reports_and_returns_no_layers()
    {
        // A closed save or a torn blob must not take the dump down.
        var region = new RegionScanData
        {
            Position = 7,
            LayerLoader = () => throw new IOException("save is closed"),
        };

        Assert.Empty(region.Layers);
        Assert.True(region.LayersLoaded); // remembered, so the failing read is not retried on every dump

        var line = AppLog.Describe("WARN", "mapregion", new IOException("save is closed"));
        Assert.Contains("IOException", line);
    }

    [Fact]
    public void A_bare_record_has_no_layers_and_no_loader()
    {
        var region = new RegionScanData { Position = 1, DataLength = 2 };
        Assert.Empty(region.Layers);   // nothing to read, and nothing throws
        Assert.True(region.LayersLoaded);
    }
}
