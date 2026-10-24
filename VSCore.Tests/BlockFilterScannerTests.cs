using System;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>Block overlay filter: chunk palette, match lookup by column, Y and id, and the reverse code index.</summary>
    public class BlockFilterScannerTests
    {
        private const int OreId = 42;

        private static void WriteInt32(Stream s, int v)
        {
            s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 24));
        }

        private static void WriteVarint(Stream s, int v)
        {
            uint u = (uint)v;
            while ((u & ~0x7fu) != 0) { s.WriteByte((byte)((u & 0x7f) | 0x80)); u >>= 7; }
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

        /// <summary>Chunk blob: palette [0 = air, paletteId] and one bit plane with the given cells set.</summary>
        private static byte[] MakeChunkBlob(int paletteId, params int[] cellIndexes)
        {
            var plane = new byte[4096];   // 32768 bits = one plane
            foreach (var idx in cellIndexes)
            {
                int word = idx >> 5, bit = idx & 31;
                plane[word * 4 + (bit >> 3)] |= (byte)(1 << (bit & 7));
            }
            var ms = new MemoryStream();
            WriteInt32(ms, -8);       // marker: a palette of two raw int32 values
            WriteInt32(ms, 0);          // index 0 -> air
            WriteInt32(ms, paletteId);  // index 1 -> the requested block
            var packed = Zstd(plane);
            ms.Write(packed, 0, packed.Length);
            return TagLenDelim(1, ms.ToArray());
        }

        /// <summary>gamedata with a BlockIDs section (as in a real save): id <-> code.</summary>
        private static byte[] Gamedata()
        {
            var entries = new MemoryStream();
            foreach (var (id, code) in new[] { (7, "ore-cassiterite"), (9, "rock-granite"), (7 + 1, "ore-cassiterite") })
            {
                var e = new MemoryStream();
                WriteVarint(e, (1 << 3) | 0); WriteVarint(e, id);
                WriteVarint(e, (2 << 3) | 2); WriteVarint(e, code.Length);
                var cb = Encoding.UTF8.GetBytes(code); e.Write(cb, 0, cb.Length);
                var eb = e.ToArray();
                WriteVarint(entries, (2 << 3) | 2); WriteVarint(entries, eb.Length); entries.Write(eb, 0, eb.Length);
            }
            var payload = entries.ToArray();
            var ms = new MemoryStream();
            var marker = Encoding.ASCII.GetBytes("BlockIDs");
            ms.Write(marker, 0, marker.Length);
            WriteVarint(ms, (2 << 3) | 2); WriteVarint(ms, payload.Length);
            ms.Write(payload, 0, payload.Length);
            return ms.ToArray();
        }

        private static void Exec(SqliteConnection con, string sql)
        {
            using var c = con.CreateCommand();
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }

        private static string MakeSave(byte[] chunkBlob, ulong position, bool withGamedata = false)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "flt_" + Guid.NewGuid().ToString("N") + ".vcdbs");
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
                    ins.Parameters.AddWithValue("$p", (long)position);
                    ins.Parameters.AddWithValue("$d", chunkBlob);
                    ins.ExecuteNonQuery();
                }
                if (withGamedata)
                {
                    using var ins = con.CreateCommand();
                    ins.CommandText = "INSERT INTO gamedata(savegameid,data) VALUES(1,$d);";
                    ins.Parameters.AddWithValue("$d", Gamedata());
                    ins.ExecuteNonQuery();
                }
            }
            return dbPath;
        }

        [Fact]
        public void Palette_is_readable_without_unpacking_planes()
        {
            var blob = MakeChunkBlob(OreId, 0);
            Assert.Equal(new[] { 0, OreId }, ChunkBlockDecoder.DecodePalette(blob));
            Assert.Equal(2, ChunkBlockDecoder.DecodePaletteSize(blob));
        }

        [Fact]
        public void Filter_finds_column_top_y_and_id()
        {
            // chunk (cx=0, cy=1, cz=0): column (x=3,z=7); ore at y_local=5 (wy=37) and deeper at y_local=1 (wy=33)
            int upper = 3 + 7 * 32 + 5 * 1024;
            int lower = 3 + 7 * 32 + 1 * 1024;
            var dbPath = MakeSave(MakeChunkBlob(OreId, upper, lower), ChunkPosHelper.ToIndex(0, 1, 0));
            try
            {
                using var save = VSSaveFile.OpenReadOnly(dbPath);
                var tm = TerrainMap.Build(save);
                var map = BlockFilterScanner.Build(save, tm, new[] { OreId });

                Assert.Equal(1, map.HitColumns);
                Assert.Equal(2, map.MatchCells);
                Assert.True(map.Has(3, 7));
                Assert.False(map.Has(3, 8));
                Assert.Equal(2, map.Count[map.Index(3, 7)]);
                Assert.Equal(OreId, map.TopBlock[map.Index(3, 7)]);
                Assert.Equal(37, map.TopY[map.Index(3, 7)]);    // pin down that this is the topmost match
                Assert.Equal(2, map.IdCells[OreId]);
            }
            finally { File.Delete(dbPath); }
        }

        [Fact]
        public void Filter_returns_nothing_for_absent_or_empty_selection()
        {
            // the chunk contains block 5 but no OreId ore -> a filter on OreId must find nothing
            var dbPath = MakeSave(MakeChunkBlob(5, 0), ChunkPosHelper.ToIndex(0, 0, 0));
            try
            {
                using var save = VSSaveFile.OpenReadOnly(dbPath);
                var tm = TerrainMap.Build(save);
                Assert.Equal(0, BlockFilterScanner.Build(save, tm, new[] { OreId }).HitColumns);
                Assert.Equal(0, BlockFilterScanner.Build(save, tm, Array.Empty<int>()).HitColumns);
            }
            finally { File.Delete(dbPath); }
        }

        [Fact]
        public void Registry_maps_code_back_to_ids()
        {
            var dbPath = MakeSave(MakeChunkBlob(5, 0), ChunkPosHelper.ToIndex(0, 0, 0), withGamedata: true);
            try
            {
                using var save = VSSaveFile.OpenReadOnly(dbPath);
                var reg = save.BlockRegistry;
                Assert.Contains(7, reg.IdsOf("ore-cassiterite"));
                Assert.Contains(8, reg.IdsOf("ore-cassiterite"));   // one code can have several ids
                Assert.True(reg.HasCode("rock-granite"));
                Assert.Empty(reg.IdsOf("no-such-block"));
            }
            finally { File.Delete(dbPath); }
        }
    }
}
