using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using VSCore;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>
    ///     P1-21: areas (connected groups of chunks) and the windowed map build. A save with chunks at
    ///     opposite edges of the world used to not open at all: the bounding box of the two groups gave
    ///     ~6.6e10 columns, and TerrainMap failed with "map area too large". Now the map is built for a single
    ///     area (the starting one — where the world center is, that is the spawn), and the rest as you scroll.
    /// </summary>
    public class ChunkAreasTests
    {
        private const int BlockId = 5;

        // ---- synthetic save: one chunk with a single block at local y=1 (wy=1 when cy=0) ----

        private static void WriteInt32(Stream s, int v)
        {
            s.WriteByte((byte)v);
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 24));
        }

        private static void WriteVarint(Stream s, int v)
        {
            uint u = (uint)v;
            while ((u & ~0x7fu) != 0)
            {
                s.WriteByte((byte)((u & 0x7f) | 0x80));
                u >>= 7;
            }

            s.WriteByte((byte)u);
        }

        private static byte[] TagLenDelim(int field, byte[] payload)
        {
            var ms = new MemoryStream();
            WriteVarint(ms, (field << 3) | 2);
            WriteVarint(ms, payload.Length);
            ms.Write(payload, 0, payload.Length);
            return ms.ToArray();
        }

        private static byte[] Zstd(byte[] src)
        {
            using var c = new ZstdSharp.Compressor(3);
            return c.Wrap(src).ToArray();
        }

        /// <summary>Chunk blob: palette [0=air, BlockId] and one bit plane with a block in column (0,0).</summary>
        private static byte[] ChunkBlob()
        {
            var plane = new byte[4096];
            const int idx = 1 * 1024; // local y=1: the editor treats y=0 as "no data" (SurfaceY > 0)
            plane[(idx >> 5) * 4 + ((idx & 31) >> 3)] |= (byte)(1 << (idx & 7));
            var ms = new MemoryStream();
            WriteInt32(ms, -8); // marker: a palette of two raw int32 values
            WriteInt32(ms, 0);
            WriteInt32(ms, BlockId);
            var packed = Zstd(plane);
            ms.Write(packed, 0, packed.Length);
            return TagLenDelim(1, ms.ToArray());
        }

        /// <summary>gamedata with world attributes in the same shape VS writes: key + length + string.</summary>
        private static byte[] Gamedata(int worldWidth, int worldLength)
        {
            var ms = new MemoryStream();
            void Attr(string key, int value)
            {
                ms.WriteByte(0x05); // attribute type (a real gamedata has the same byte before the key)
                ms.WriteByte((byte)key.Length);
                var kb = Encoding.ASCII.GetBytes(key);
                ms.Write(kb, 0, kb.Length);
                var vb = Encoding.ASCII.GetBytes(value.ToString(CultureInfo.InvariantCulture));
                ms.WriteByte((byte)vb.Length);
                ms.Write(vb, 0, vb.Length);
            }

            Attr("worldClimate", 1); // a decoy attribute first: the parser must find worldWidth/worldLength specifically
            Attr("worldWidth", worldWidth);
            Attr("worldLength", worldLength);
            return ms.ToArray();
        }

        private static void Exec(SqliteConnection con, string sql)
        {
            using var c = con.CreateCommand();
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }

        private static string MakeSave(IEnumerable<(int Cx, int Cz)> columns, (int Width, int Length)? worldSize = null)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "areas_" + Guid.NewGuid().ToString("N") + ".vcdbs");
            var cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
            using (var con = new SqliteConnection(cs))
            {
                con.Open();
                Exec(con, "CREATE TABLE chunk(position INTEGER PRIMARY KEY, data BLOB);");
                Exec(con, "CREATE TABLE mapchunk(position INTEGER PRIMARY KEY, data BLOB);");
                Exec(con, "CREATE TABLE mapregion(position INTEGER PRIMARY KEY, data BLOB);");
                Exec(con, "CREATE TABLE gamedata(savegameid INTEGER PRIMARY KEY, data BLOB);");
                Exec(con, "CREATE TABLE playerdata(playerid INTEGER PRIMARY KEY AUTOINCREMENT, playeruid TEXT, data BLOB);");

                using (var ins = con.CreateCommand())
                {
                    ins.CommandText = "INSERT INTO chunk(position,data) VALUES($p,$d);";
                    ins.Parameters.AddWithValue("$d", ChunkBlob());
                    var pp = ins.Parameters.Add("$p", SqliteType.Integer);
                    foreach (var (cx, cz) in columns)
                    {
                        pp.Value = (long)ChunkPosHelper.ToIndex(cx, 0, cz);
                        ins.ExecuteNonQuery();
                    }
                }

                if (worldSize is { } ws)
                {
                    using var ins = con.CreateCommand();
                    ins.CommandText = "INSERT INTO gamedata(savegameid,data) VALUES(1,$d);";
                    ins.Parameters.AddWithValue("$d", Gamedata(ws.Width, ws.Length));
                    ins.ExecuteNonQuery();
                }
            }

            return dbPath;
        }

        /// <summary>Two distant groups: a 3x3 "corner" at zero and a 2x2 "center" at chunk 8000 (world center 512000).</summary>
        private static List<(int Cx, int Cz)> TwoAreas()
        {
            var list = new List<(int, int)>();
            for (int cx = 0; cx <= 2; cx++)
            for (int cz = 0; cz <= 2; cz++)
                list.Add((cx, cz));
            for (int cx = 7999; cx <= 8000; cx++)
            for (int cz = 7999; cz <= 8000; cz++)
                list.Add((cx, cz));
            return list;
        }

        [Fact]
        public void TwoFarAreas_AreSplit_AndMainIsAtWorldCentre()
        {
            var db = MakeSave(TwoAreas(), (512000, 512000));
            try
            {
                using var save = VSSaveFile.OpenReadOnly(db);
                var areas = ChunkAreas.Build(save);

                Assert.Equal(2, areas.Areas.Count);
                Assert.Equal(13, areas.TotalColumns);
                Assert.Equal(13, areas.TotalRows);
                Assert.Equal(0, areas.MinWorldY);
                Assert.Equal(31, areas.MaxWorldY);

                // the starting one is the "center" (it holds the world center = spawn), even though the "corner" is larger
                Assert.NotNull(areas.Main);
                Assert.True(areas.Main!.IsMain);
                Assert.Equal(0, areas.Main.Index);
                Assert.Equal(7999, areas.Main.MinCx);
                Assert.Equal(4, areas.Main.Columns);
                Assert.Equal(0, areas.Areas[1].MinCx);
                Assert.Equal(9, areas.Areas[1].Columns);

                // the panning domain covers both groups
                Assert.True(areas.WorldBounds.MinX <= 0);
                Assert.True(areas.WorldBounds.MaxX >= 8000 * 32 + 31);
            }
            finally { File.Delete(db); }
        }

        [Fact]
        public void WithoutWorldSize_MainIsTheLargestArea()
        {
            var db = MakeSave(TwoAreas()); // no gamedata: the world size is unknown
            try
            {
                using var save = VSSaveFile.OpenReadOnly(db);
                var areas = ChunkAreas.Build(save);
                Assert.Null(areas.WorldWidth);
                Assert.Equal(2, areas.Areas.Count);
                Assert.Equal(9, areas.Main!.Columns); // 3x3 is larger than 2x2
                Assert.Equal(0, areas.Main.MinCx);
            }
            finally { File.Delete(db); }
        }

        [Fact]
        public void WholeSaveWouldNotFit_SoWindowsAreBuiltPerArea()
        {
            var db = MakeSave(TwoAreas(), (512000, 512000));
            try
            {
                using var save = VSSaveFile.OpenReadOnly(db);
                var areas = ChunkAreas.Build(save);
                int w = areas.Areas.Max(a => a.MaxX) - areas.Areas.Min(a => a.MinX) + 1;
                int h = areas.Areas.Max(a => a.MaxZ) - areas.Areas.Min(a => a.MinZ) + 1;

                // this is exactly what caused "the save does not open": the shared rectangle of the two groups
                var ex = Assert.Throws<InvalidOperationException>(() => TerrainMap.EnsureAreaFits(w, h));
                Assert.Contains("too large", ex.Message);

                // windowed, each area is built on its own and contains only its columns
                var corner = areas.Areas.Single(a => a.MinCx == 0);
                using (var save2 = VSSaveFile.OpenReadOnly(db))
                {
                    var tmCorner = TerrainMap.BuildInArea(save2, corner);
                    // Map bounds follow columns WITH DATA (the block sits in the corner of each chunk),
                    // so we check the essential part: they stay within their own area and do not
                    // stretch to the second group (otherwise the window does not work).
                    Assert.InRange(tmCorner.MinX, corner.MinX, corner.MaxX);
                    Assert.InRange(tmCorner.MinX + tmCorner.Width - 1, corner.MinX, corner.MaxX);
                    Assert.InRange(tmCorner.MinZ, corner.MinZ, corner.MaxZ);
                    Assert.InRange(tmCorner.MinZ + tmCorner.Height - 1, corner.MinZ, corner.MaxZ);
                    Assert.Equal(9, tmCorner.PresentChunks.Count);
                    Assert.All(tmCorner.PresentChunks, c => Assert.True(corner.ContainsChunk(c.cx, c.cz)));
                    Assert.Equal(1, tmCorner.MaxSurface); // the chunk block was actually decoded
                    Assert.Same(corner, tmCorner.Area);
                }

                // Build(save) = the map of the starting area (this used to throw)
                using (var save3 = VSSaveFile.OpenReadOnly(db))
                {
                    // Build(save) picks the starting area itself — on another connection that is a different
                    // ChunkArea object, so we compare the bounds rather than the reference
                    var tm = TerrainMap.Build(save3);
                    Assert.NotNull(tm.Area);
                    Assert.Equal(areas.Main!.MinCx, tm.Area!.MinCx);
                    Assert.Equal(areas.Main.MinCz, tm.Area.MinCz);
                    Assert.Equal(4, tm.PresentChunks.Count);
                    Assert.All(tm.PresentChunks, c => Assert.True(tm.Area.ContainsChunk(c.cx, c.cz)));
                }
            }
            finally { File.Delete(db); }
        }

        [Fact]
        public void ChunkRowsInWindow_ReturnsOnlyItsColumns()
        {
            var db = MakeSave(TwoAreas(), (512000, 512000));
            try
            {
                using var save = VSSaveFile.OpenReadOnly(db);
                var areas = ChunkAreas.Build(save);
                var corner = areas.Areas.Single(a => a.MinCx == 0);

                Assert.Equal(13, save.ChunkRowsInWindow(null).Count);        // without a window — the whole save
                Assert.Equal(9, save.ChunkRowsInWindow(corner).Count);       // windowed — the "corner" only
                Assert.Equal(4, save.ChunkRowsInWindow(areas.Main).Count);
                Assert.Equal(13, save.ChunkPositions.Count);
            }
            finally { File.Delete(db); }
        }

        [Fact]
        public void DetectWorldSize_ReadsAttributes_AndIgnoresMissing()
        {
            var withSize = MakeSave(TwoAreas(), (512000, 1024000));
            var without = MakeSave(TwoAreas());
            try
            {
                using (var save = VSSaveFile.OpenReadOnly(withSize))
                {
                    var size = save.DetectWorldSize();
                    Assert.NotNull(size);
                    Assert.Equal(512000, size!.Value.Width);
                    Assert.Equal(1024000, size.Value.Length);
                }

                using (var save = VSSaveFile.OpenReadOnly(without))
                    Assert.Null(save.DetectWorldSize());
            }
            finally
            {
                File.Delete(withSize);
                File.Delete(without);
            }
        }

        [Fact]
        public void HugeCluster_IsSplitIntoWindowsThatFitTheLimit()
        {
            // a diagonal "snake": one connected group (8 neighbours), but a bbox of 640032x640032 blocks —
            // a single map does not fit it, so Build has to split it along the median / a hole
            var positions = new List<long>();
            for (int i = 0; i <= 20000; i++) positions.Add((long)ChunkPosHelper.ToIndex(i, 0, i));

            var areas = ChunkAreas.Build(positions, null);
            Assert.True(areas.Areas.Count > 1, $"expected a split, got {areas.Areas.Count} windows");
            Assert.Equal(20001, areas.Areas.Sum(a => a.Columns));
            Assert.Equal(20001, areas.Areas.Sum(a => a.Rows));
            Assert.All(areas.Areas, a => Assert.True(a.BlockCells <= TerrainMap.MaxCells,
                $"window {a.MinCx}..{a.MaxCx} does not fit: {a.BlockCells} columns"));
        }

        [Fact]
        public void EmptySave_HasNoAreas_AndBuildsSafeEmptyMap()
        {
            var db = MakeSave(Array.Empty<(int, int)>());
            try
            {
                using var save = VSSaveFile.OpenReadOnly(db);
                var areas = ChunkAreas.Build(save);
                Assert.Empty(areas.Areas);
                Assert.Null(areas.Main);
                Assert.Equal(0, areas.TotalColumns);

                var tm = TerrainMap.BuildInArea(save, null);
                Assert.Equal(1, tm.Width);
                Assert.Equal(1, tm.Height);
                Assert.Equal(0, tm.MaxSurface);
                Assert.Empty(tm.PresentChunks);
                Assert.Null(tm.Area);
            }
            finally { File.Delete(db); }
        }

        [Fact]
        public void AreaForViewport_PicksTheAreaUnderTheViewport()
        {
            var db = MakeSave(TwoAreas(), (512000, 512000));
            try
            {
                using var save = VSSaveFile.OpenReadOnly(db);
                var areas = ChunkAreas.Build(save);
                var main = areas.Main!;
                var corner = areas.Areas.Single(a => a.MinCx == 0);

                ChunkArea? Pick(ChunkArea a, ChunkArea? current = null)
                    => areas.AreaForViewport(a.MinX, a.MinZ, a.MaxX, a.MaxZ, current);

                Assert.Same(corner, Pick(corner, main));   // we reached the far group
                Assert.Same(main, Pick(main, corner));
                Assert.Same(main, Pick(main, main));       // the current one stays itself

                // the viewport sits in the void between the groups: no intersections, nothing to switch to
                Assert.Null(areas.AreaForViewport(1_200_000, 1_200_000, 1_200_100, 1_200_100, main));

                // the viewport covers both groups: the one closer to the viewport center wins
                // (the center of the giant rectangle lies closer to the "corner")
                Assert.Same(corner, areas.AreaForViewport(corner.MinX, corner.MinZ, main.MaxX, main.MaxZ, null));
            }
            finally { File.Delete(db); }
        }
    }
}
