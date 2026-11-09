using System;
using System.IO;
using System.Linq;
using VSCore;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>
    ///     The on-disk tile format: a cached tile must come back identical, and anything unexpected
    ///     (foreign file, other format version, truncation) must be a miss rather than an exception —
    ///     the cache is an optimisation and can never be the reason a save fails to open.
    /// </summary>
    public class TerrainMapStoreTests
    {
        private static TerrainMap Sample(int w = 8, int h = 6)
        {
            var tm = TerrainMap.Allocate(320, 640, w, h, TerrainNeeds.Blocks);
            int cells = w * h;
            for (int i = 0; i < cells; i++)
            {
                tm.SurfaceY[i] = (ushort)(100 + i);
                tm.BottomY[i] = (ushort)(10 + i % 7);
                tm.TopBlock[i] = (ushort)(i % 5);
                tm.MapY![i] = (ushort)(99 + i);
                tm.MapBlock![i] = (ushort)(i % 3);
            }

            tm.Shadow = new byte[cells];
            for (int i = 0; i < cells; i++) tm.Shadow[i] = (byte)(128 + i % 9);
            tm.MaxSurface = 200;
            tm.MinSurface = 90;
            tm.MinWorldY = 0;
            tm.MaxWorldY = 255;
            tm.YearRel = 0.75;
            tm.SeaLevel = 110;
            tm.PresentChunks.Add((10, 20));
            tm.PresentChunks.Add((11, 20));
            tm.ChiselMaterial[TerrainMap.PackColumn(321, 641)] = 42;
            var pixels = new byte[4 * 3 * 4];   // pw*ph*4: the store refuses a pyramid whose pixel count does not match
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)i;
            tm.SetPyramid(TerrainPyramid.Restore(320, 640, 4, 3, 2, TerrainColorizer.ModeBlocks, pixels));
            return tm;
        }

        [Fact(DisplayName = "tile round-trips: every array, the relief, the overview, chunks and chisel material")]
        public void RoundTrip_IsLossless()
        {
            var tm = Sample();
            using var ms = new MemoryStream();
            TerrainMapStore.Write(ms, tm);
            ms.Position = 0;
            var back = TerrainMapStore.Read(ms, null, null);
            Assert.NotNull(back);
            Assert.Equal(tm.MinX, back!.MinX);
            Assert.Equal(tm.MinZ, back.MinZ);
            Assert.Equal(tm.Width, back.Width);
            Assert.Equal(tm.Height, back.Height);
            Assert.Equal(tm.Needs, back.Needs);
            Assert.Equal(tm.MaxSurface, back.MaxSurface);
            Assert.Equal(tm.MinSurface, back.MinSurface);
            Assert.Equal(tm.MinWorldY, back.MinWorldY);
            Assert.Equal(tm.MaxWorldY, back.MaxWorldY);
            Assert.Equal(tm.YearRel, back.YearRel);
            Assert.Equal(tm.SeaLevel, back.SeaLevel);
            Assert.Equal(tm.SurfaceY, back.SurfaceY);
            Assert.Equal(tm.BottomY, back.BottomY);
            Assert.Equal(tm.TopBlock, back.TopBlock);
            Assert.Equal(tm.MapY, back.MapY);
            Assert.Equal(tm.MapBlock, back.MapBlock);
            Assert.Equal(tm.Shadow, back.Shadow);
            Assert.Null(back.WaterDepth); // blocks mode has no water column, and none must appear from nowhere
            Assert.Equal(tm.PresentChunks.OrderBy(c => c.cx).ThenBy(c => c.cz),
                back.PresentChunks.OrderBy(c => c.cx).ThenBy(c => c.cz));
            Assert.Equal(42, back.ChiselMaterial[TerrainMap.PackColumn(321, 641)]);
            Assert.NotNull(back.Pyramid);
            Assert.Equal(tm.Pyramid!.Width, back.Pyramid!.Width);
            Assert.Equal(tm.Pyramid.Height, back.Pyramid.Height);
            Assert.Equal(tm.Pyramid.CellSize, back.Pyramid.CellSize);
            Assert.Equal(tm.Pyramid.Mode, back.Pyramid.Mode);
            Assert.Equal(tm.Pyramid.Pixels, back.Pyramid.Pixels);
        }

        [Fact(DisplayName = "water mode round-trips its water column and the rain arrays stay absent")]
        public void RoundTrip_WaterMode()
        {
            var tm = TerrainMap.Allocate(0, 0, 4, 3, TerrainNeeds.WaterModes);
            for (int i = 0; i < 12; i++) tm.WaterDepth![i] = i * 3;
            tm.SurfaceY[0] = 120;
            using var ms = new MemoryStream();
            TerrainMapStore.Write(ms, tm);
            ms.Position = 0;
            var back = TerrainMapStore.Read(ms, null, null);
            Assert.NotNull(back);
            Assert.Equal(tm.WaterDepth, back!.WaterDepth);
            Assert.Null(back.MapY);
            Assert.Null(back.Pyramid);
        }

        [Fact(DisplayName = "a foreign file, an unknown version or a truncated tile is a miss, not an exception")]
        public void Garbage_IsAMiss()
        {
            using (var junk = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }))
                Assert.Null(TerrainMapStore.Read(junk, null, null));

            using var ms = new MemoryStream();
            TerrainMapStore.Write(ms, Sample());
            var bytes = ms.ToArray();

            // same magic, other format version
            var otherVersion = (byte[])bytes.Clone();
            otherVersion[TerrainMapStore.Magic.Length] = (byte)(TerrainMapStore.FormatVersion + 1);
            using (var s = new MemoryStream(otherVersion)) Assert.Null(TerrainMapStore.Read(s, null, null));

            // cut in the middle of the compressed block
            using (var s = new MemoryStream(bytes, 0, bytes.Length / 2)) Assert.Null(TerrainMapStore.Read(s, null, null));

            // the payload itself corrupted: the frame no longer decompresses
            var corrupt = (byte[])bytes.Clone();
            corrupt[^1] ^= 0xFF;
            corrupt[^2] ^= 0xFF;
            using (var s = new MemoryStream(corrupt)) Assert.Null(TerrainMapStore.Read(s, null, null));
        }

        [Fact(DisplayName = "a real tile of a real save round-trips with identical arrays")]
        public void RoundTrip_RealSave()
        {
            const string save = "/home/user/.config/VintagestoryData/Saves/Captured 8652c64e.vcdbs";
            if (!File.Exists(save)) return;   // no save in this environment — skip

            using var file = VSSaveFile.OpenReadOnly(save);
            var areas = ChunkAreas.Build(file);
            var tm = TerrainMap.BuildInArea(file, areas.Main);
            Assert.True(tm.Width > 1 && tm.Height > 1, "the sample save must produce a real map");

            using var ms = new MemoryStream();
            TerrainMapStore.Write(ms, tm);
            Assert.True(ms.Length > 0);
            ms.Position = 0;
            var back = TerrainMapStore.Read(ms, file, areas.Main);
            Assert.NotNull(back);
            Assert.Equal(tm.SurfaceY, back!.SurfaceY);
            Assert.Equal(tm.BottomY, back.BottomY);
            Assert.Equal(tm.TopBlock, back.TopBlock);
            Assert.Equal(tm.MapY, back.MapY);
            Assert.Equal(tm.MapBlock, back.MapBlock);
            Assert.Equal(tm.Shadow, back.Shadow);
            Assert.Equal(tm.PresentChunks.Count, back.PresentChunks.Count);
            Assert.Equal(areas.Main, back.Area);
            Assert.NotNull(back.Voxels);   // re-attached, not stored
            Assert.NotNull(back.Registry);
        }
    }
}
