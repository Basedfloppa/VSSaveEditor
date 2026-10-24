using System;
using VSCore;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>P0-3: the map area limit — a clear error instead of an OOM when a save holds chunks
    /// at opposite edges of the world (chunk X/Z is 22 bits, that is up to 4 Mi blocks per side).</summary>
    public class TerrainMapLimitsTests
    {
        [Fact]
        public void RealWorldSizes_Pass()
        {
            TerrainMap.EnsureAreaFits(1344, 1312);   // the smallest verified save
            TerrainMap.EnsureAreaFits(8192, 4096);
        }

        [Fact]
        public void LargestKnownRealSave_Passes()
        {
            // The actual size of the largest verified save ("Captured d3e82bd6",
            // 79,265,792 columns): dropping the limit below this would make a real world stop opening.
            TerrainMap.EnsureAreaFits(7552, 10496);
        }

        [Fact]
        public void AreaAtLimit_Passes_AndAboveFails()
        {
            int side = (int)Math.Sqrt(TerrainMap.MaxCells);
            Assert.True((long)side * side <= TerrainMap.MaxCells);
            TerrainMap.EnsureAreaFits(side, side);
            Assert.Throws<InvalidOperationException>(() => TerrainMap.EnsureAreaFits(side + 1, side + 1));
        }

        [Fact]
        public void OppositeWorldCorners_ThrowsClearError()
        {
            // two chunks at the edges: 4,194,304 x 4,194,304 blocks = 1.7e13 columns
            var ex = Assert.Throws<InvalidOperationException>(
                () => TerrainMap.EnsureAreaFits(4_194_304, 4_194_304));
            Assert.Contains("too large", ex.Message);
            Assert.Contains("columns", ex.Message);
            Assert.Contains("opposite ends", ex.Message);
        }
    }
}
