using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using VSCore;
using VSCore.Format;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>P0-8: "a save must never bring the editor down". A deterministic fuzz (fixed seed)
    /// of the parser, the block registry, the chunk walk and the decoder — plus targeted regressions for the
    /// len-delim overflow (P0-1) that this very fuzz caught.</summary>
    public class ParserFuzzTests
    {
        private const int Seed = 20260927;

        // ------------------------- helpers -------------------------

        private static void WriteVarint(Stream ms, long v)
        {
            ulong u = (ulong)v;
            while (u >= 0x80) { ms.WriteByte((byte)((u & 0x7F) | 0x80)); u >>= 7; }
            ms.WriteByte((byte)u);
        }

        private static byte[] TagLenDelim(int field, byte[] payload)
        {
            var ms = new MemoryStream();
            WriteVarint(ms, ((long)field << 3) | 2);
            WriteVarint(ms, payload.Length);
            ms.Write(payload, 0, payload.Length);
            return ms.ToArray();
        }

        private static byte[] Zstd(byte[] src)
        {
            using var c = new ZstdSharp.Compressor(3);
            return c.Wrap(src).ToArray();
        }

        /// <summary>Exactly the bytes that decode to `long.MaxValue`: 9x0xFF + a 0x00 terminator.</summary>
        private static readonly byte[] LongMaxVarint =
            { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

        private static byte[] RandomBlob(Random rnd, int maxLen)
        {
            var b = new byte[rnd.Next(0, maxLen)];
            rnd.NextBytes(b);
            return b;
        }

        /// <summary>Chunk blob with palette [0=air, 7] and one bit plane (a valid baseline for mutations).</summary>
        private static byte[] ValidChunkBlob(int paletteId = 7)
        {
            var plane = new byte[4096];
            for (int i = 0; i < plane.Length; i += 37) plane[i] = 0x5A;
            var ms = new MemoryStream();
            var pal = new byte[8];
            BitConverter.GetBytes(0).CopyTo(pal, 0);
            BitConverter.GetBytes(paletteId).CopyTo(pal, 4);
            WriteVarint(ms, -8);            // marker: a palette of two raw int32 values
            ms.Write(pal, 0, pal.Length);
            var packed = Zstd(plane);
            ms.Write(packed, 0, packed.Length);
            return TagLenDelim(1, ms.ToArray());
        }

        // ------------------------- fuzz -------------------------

        [Fact]
        public void Fuzz_PbReader_RandomBlobs_NeverThrows()
        {
            var rnd = new Random(Seed);
            for (int i = 0; i < 5000; i++)
            {
                var blob = RandomBlob(rnd, 512);
                var ex = Record.Exception(() =>
                {
                    var fields = PbReader.ReadMessage(blob, maxDepth: 3);
                    Assert.True(fields.Count <= 4096);
                    foreach (var f in fields)
                        if (f.WireType == 2 && f.Bytes is { } seg)
                            Assert.True(seg.Length <= blob.Length);
                });
                Assert.Null(ex);
            }
        }

        /// <summary>Tags with plausible field/wiretype values reach branches that pure noise never gets to.</summary>
        [Fact]
        public void Fuzz_PbReader_StructuredTags_NeverThrows()
        {
            var rnd = new Random(Seed + 1);
            for (int i = 0; i < 3000; i++)
            {
                var ms = new MemoryStream();
                int n = rnd.Next(1, 12);
                for (int k = 0; k < n; k++)
                {
                    int field = rnd.Next(1, 20), wt = rnd.Next(0, 6);
                    WriteVarint(ms, ((long)field << 3) | (uint)wt);
                    switch (wt)
                    {
                        case 0: WriteVarint(ms, rnd.NextInt64()); break;
                        case 2:
                            if (rnd.Next(4) == 0) ms.Write(LongMaxVarint, 0, LongMaxVarint.Length);
                            else WriteVarint(ms, rnd.Next(0, 300));
                            break;
                        case 5: WriteVarint(ms, rnd.Next()); break;
                        case 1: WriteVarint(ms, rnd.NextInt64()); break;
                    }
                }
                var blob = ms.ToArray();
                Assert.Null(Record.Exception(() => PbReader.ReadMessage(blob, maxDepth: 3)));
            }
        }

        [Fact]
        public void Fuzz_ChunkDecoder_Random_Mutated_Truncated()
        {
            var rnd = new Random(Seed + 2);
            for (int i = 0; i < 2000; i++) AssertDecodesSafely(RandomBlob(rnd, 256));

            var valid = ValidChunkBlob();
            for (int i = 0; i < 2000; i++)
            {
                var blob = (byte[])valid.Clone();
                int mutations = rnd.Next(1, 8);
                for (int m = 0; m < mutations; m++) blob[rnd.Next(blob.Length)] = (byte)rnd.Next(256);
                AssertDecodesSafely(blob);
            }

            for (int len = 0; len <= valid.Length; len++)
                AssertDecodesSafely(valid.AsSpan(0, len).ToArray());
        }

        [Fact]
        public void Fuzz_ChunkDataEditor_WalkTop_Garbage_NeverThrows()
        {
            var rnd = new Random(Seed + 3);
            for (int i = 0; i < 3000; i++)
            {
                var blob = RandomBlob(rnd, 256);
                var fields = ChunkDataEditor.WalkTop(blob);
                foreach (var f in fields)
                    Assert.True(f.PayloadStart <= f.PayloadEnd && f.PayloadEnd <= blob.Length,
                        $"range {f.PayloadStart}..{f.PayloadEnd} outside 0..{blob.Length}");
            }
        }

        [Fact]
        public void Fuzz_BlockRegistry_GarbageGamedata_NeverThrows()
        {
            var rnd = new Random(Seed + 4);
            for (int i = 0; i < 3000; i++)
                Assert.Null(Record.Exception(() => BlockRegistry.FromGamedata(RandomBlob(rnd, 256))));
        }

        // ------------------------- len-delim overflow regressions (P0-1) -------------------------

        [Fact]
        public void BlockRegistry_HugeLenDelim_ReturnsEmptyWithoutThrowing()
        {
            var ms = new MemoryStream();
            var marker = Encoding.ASCII.GetBytes("BlockIDs");
            ms.Write(marker, 0, marker.Length);
            ms.WriteByte(0x12);                                  // field2, wiretype 2
            ms.Write(LongMaxVarint, 0, LongMaxVarint.Length);
            var gamedata = ms.ToArray();

            BlockRegistry reg = null!;
            var ex = Record.Exception(() => reg = BlockRegistry.FromGamedata(gamedata));
            Assert.Null(ex);
            Assert.Equal(0, reg.Count);
        }

        [Fact]
        public void WalkTop_HugeLenDelim_StopsWithoutBogusRange()
        {
            var blob = new byte[1 + LongMaxVarint.Length];
            blob[0] = 0x0A;                                      // field1, wiretype 2
            Array.Copy(LongMaxVarint, 0, blob, 1, LongMaxVarint.Length);

            var fields = ChunkDataEditor.WalkTop(blob);
            foreach (var f in fields)
                Assert.True(f.PayloadStart <= f.PayloadEnd && f.PayloadEnd <= blob.Length,
                    $"bad range {f.PayloadStart}..{f.PayloadEnd} at length {blob.Length}");
        }

        /// <summary>ScanChisel itself is private, but it runs inside TerrainMap.Build: a save with the code
        /// "chisel" in the registry and the bytes [0x42][long.MaxValue][6]"Chisel" used to throw Overflow.</summary>
        [Fact]
        public void TerrainMap_ChiselNeedleWithHugeVarint_DoesNotThrow()
        {
            var ms = new MemoryStream();
            ms.WriteByte(0x42);                                  // field8, wiretype 2
            ms.Write(LongMaxVarint, 0, LongMaxVarint.Length);
            ms.WriteByte(6);
            ms.Write(Encoding.ASCII.GetBytes("Chisel"), 0, 6);
            var chunkBlob = ms.ToArray();

            var dbPath = MakeSave(chunkBlob, ChunkPosHelper.ToIndex(0, 1, 0), GamedataWithChisel());
            try
            {
                using var save = VSSaveFile.OpenReadOnly(dbPath);
                var tm = TerrainMap.Build(save);
                Assert.NotNull(tm);
            }
            finally
            {
                File.Delete(dbPath);
            }
        }

        // ------------------------- synthetic save -------------------------

        private static void AssertDecodesSafely(byte[] blob)
        {
            var ex = Record.Exception(() =>
            {
                Assert.Equal(ChunkBlockDecoder.CellsPerChunk, ChunkBlockDecoder.DecodeChunkBlocks(blob).Length);
                Assert.Equal(ChunkBlockDecoder.CellsPerChunk, ChunkBlockDecoder.DecodeChunkBlocksMerged(blob).Length);
                Assert.Equal(ChunkBlockDecoder.CellsPerChunk, ChunkBlockDecoder.DecodeChunkBlocksMergedOnePass(blob).Length);
                Assert.True(ChunkBlockDecoder.DecodePalette(blob).Length <= 262_144);
                Assert.True(ChunkBlockDecoder.DecodeLiquidPalette(blob).Length <= 262_144);
                ChunkBlockDecoder.DecodePaletteSize(blob);
                ChunkBlockDecoder.CountPlanesInData(blob);
            });
            Assert.Null(ex);
        }

        private static byte[] GamedataWithChisel()
        {
            var entries = new MemoryStream();
            foreach (var (id, code) in new[] { (0, "air"), (99, "chiseledblock") })
            {
                var e = new MemoryStream();
                WriteVarint(e, (1 << 3) | 0); WriteVarint(e, id);
                var cb = Encoding.UTF8.GetBytes(code);
                WriteVarint(e, (2 << 3) | 2); WriteVarint(e, cb.Length);
                e.Write(cb, 0, cb.Length);
                var eb = e.ToArray();
                WriteVarint(entries, (2 << 3) | 2); WriteVarint(entries, eb.Length);
                entries.Write(eb, 0, eb.Length);
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

        private static string MakeSave(byte[] chunkBlob, ulong position, byte[] gamedata)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "fuzz_" + Guid.NewGuid().ToString("N") + ".vcdbs");
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
                using (var ins = con.CreateCommand())
                {
                    ins.CommandText = "INSERT INTO gamedata(savegameid,data) VALUES(1,$d);";
                    ins.Parameters.AddWithValue("$d", gamedata);
                    ins.ExecuteNonQuery();
                }
            }
            return dbPath;
        }
    }
}
