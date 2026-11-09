using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using VSCore;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>
    ///     The tile cache on disk: a stored tile must come back for the same save and area, must be refused
    ///     after the source file or the content changed, and must stay inside its size limit. A broken or
    ///     missing cache directory only disables the cache — it never throws into the caller.
    /// </summary>
    public class TerrainMapCacheTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _src;

        public TerrainMapCacheTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vscache_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _src = Path.Combine(_dir, "world.vcdbs");
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = _src, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
            c.Open();
            using var cmd = c.CreateCommand();
            // gamedata is what the block registry loads from: a cache hit re-attaches it to the restored map,
            // so a save without it fails exactly where the real build fails.
            cmd.CommandText = "CREATE TABLE chunk(position INTEGER PRIMARY KEY, data BLOB);" +
                              "INSERT INTO chunk(position,data) VALUES(1,x'0102');" +
                              "CREATE TABLE gamedata(savegameid INTEGER PRIMARY KEY, data BLOB);" +
                              "INSERT INTO gamedata(savegameid,data) VALUES(1,x'0909');";
            cmd.ExecuteNonQuery();
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string CacheRoot => Path.Combine(_dir, "cache");

        private static ChunkArea Area(int i = 0) => new()
        {
            Index = i, MinCx = 10 + i, MinCz = 20, MaxCx = 11 + i, MaxCz = 21, Columns = 4
        };

        private static TerrainMap Map(int seed, ChunkArea area)
        {
            var tm = TerrainMap.Allocate(area.MinX, area.MinZ, 4, 3, TerrainNeeds.Blocks);
            for (int i = 0; i < tm.SurfaceY.Length; i++)
            {
                tm.SurfaceY[i] = (ushort)(50 + seed + i);
                tm.MapY![i] = (ushort)(40 + seed + i);
            }

            tm.MaxSurface = 100 + seed;
            tm.MinSurface = 10;
            return tm;
        }

        [Fact(DisplayName = "a stored tile is returned for the same save and area")]
        public void SaveThenLoad_Hits()
        {
            using var save = VSSaveFile.OpenReadOnly(_src);
            var cache = new TerrainMapCache(CacheRoot);
            var area = Area();
            var tm = Map(1, area);
            cache.Save(save, area, TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks, tm);

            Assert.True(cache.TryLoad(save, area, TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks, out var back));
            Assert.NotNull(back);
            Assert.Equal(tm.SurfaceY, back!.SurfaceY);
            Assert.Equal(tm.MapY, back.MapY);
            Assert.Equal(area, back.Area);
            Assert.Equal(1, cache.Count);
        }

        [Fact(DisplayName = "another render mode or another area is a miss")]
        public void OtherModeOrArea_Misses()
        {
            using var save = VSSaveFile.OpenReadOnly(_src);
            var cache = new TerrainMapCache(CacheRoot);
            var area = Area();
            cache.Save(save, area, TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks, Map(1, area));

            Assert.False(cache.TryLoad(save, area, TerrainNeeds.WaterModes, TerrainColorizer.ModeBlocks, out _));
            Assert.False(cache.TryLoad(save, Area(1), TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks, out _));
        }

        [Fact(DisplayName = "after the source file changed (mtime) the tile is not served")]
        public void ChangedSource_Misses()
        {
            using var save = VSSaveFile.OpenReadOnly(_src);
            var cache = new TerrainMapCache(CacheRoot);
            var area = Area();
            cache.Save(save, area, TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks, Map(1, area));
            Assert.True(cache.TryLoad(save, area, TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks, out _));

            File.SetLastWriteTimeUtc(_src, DateTime.UtcNow.AddMinutes(-30));
            Assert.False(cache.TryLoad(save, area, TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks, out _));
        }

        [Fact(DisplayName = "edits rotate the key: a tile built from the previous content is not served")]
        public void ContentVersionChange_Misses()
        {
            using var save = VSSaveFile.OpenForEditing(_src);
            var cache = new TerrainMapCache(CacheRoot);
            var area = Area();
            cache.Save(save, area, TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks, Map(1, area));
            Assert.True(cache.TryLoad(save, area, TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks, out _));

            save.ApplyChanges(new[] { ("chunk", 1L, new byte[] { 9, 9, 9 }) });
            Assert.True(save.ContentVersion > 0);
            Assert.False(cache.TryLoad(save, area, TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks, out _));
        }

        [Fact(DisplayName = "invalidate drops every tile of the save")]
        public void Invalidate_RemovesTiles()
        {
            using var save = VSSaveFile.OpenReadOnly(_src);
            var cache = new TerrainMapCache(CacheRoot);
            cache.Save(save, Area(0), TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks, Map(1, Area(0)));
            cache.Save(save, Area(1), TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks, Map(2, Area(1)));
            Assert.Equal(2, cache.Count);

            cache.Invalidate(save);
            Assert.Equal(0, cache.Count);
            Assert.False(cache.TryLoad(save, Area(0), TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks, out _));
        }

        [Fact(DisplayName = "prune keeps the cache under its limit and drops the oldest tiles first")]
        public void Prune_EnforcesLimit()
        {
            using var save = VSSaveFile.OpenReadOnly(_src);
            // one tile is ~90 B here, so a 220 B limit holds two and pruning must drop the oldest
            var cache = new TerrainMapCache(CacheRoot, maxBytes: 220);
            cache.Save(save, Area(0), TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks, Map(1, Area(0)));
            var first = Directory.GetFiles(CacheRoot, "*.tile", SearchOption.AllDirectories).Single();
            File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddHours(-2));

            cache.Save(save, Area(1), TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks, Map(2, Area(1)));
            cache.Save(save, Area(2), TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks, Map(3, Area(2)));

            Assert.True(cache.BytesOnDisk() <= 220, $"cache holds {cache.BytesOnDisk()} B");
            Assert.False(cache.TryLoad(save, Area(0), TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks, out _));
            Assert.True(cache.TryLoad(save, Area(2), TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks, out _));
        }

        [Fact(DisplayName = "an unusable cache directory disables the cache instead of throwing")]
        public void UnusableRoot_IsDisabled()
        {
            using var save = VSSaveFile.OpenReadOnly(_src);
            // a path under a regular file can never be created
            var blocked = Path.Combine(_src, "cache");
            var cache = new TerrainMapCache(blocked);
            var area = Area();
            cache.Save(save, area, TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks, Map(1, area));
            Assert.False(cache.TryLoad(save, area, TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks, out _));
            Assert.Equal(0, cache.BytesOnDisk());
        }
    }
}
