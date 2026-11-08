using System.Text;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>
    ///     Memory model of the scan, not its parsing rules: a server save repeats the same tokens millions of
    ///     times and has millions of chunk rows with a handful of block ids. These tests pin the two properties
    ///     the memory reduction relies on — one string instance per value, and a packed per-chunk histogram that
    ///     still behaves like the old string dictionary.
    /// </summary>
    public class ScanMemoryModelTests
    {
        [Fact]
        public void StringPool_returns_one_instance_per_value()
        {
            var pool = new StringPool();
            var first = pool.Intern("rock-granite");
            Assert.Same(first, pool.Intern("rock-granite"));
            Assert.Same(first, pool.Intern("rock-granite".AsSpan()));
            var bytes = Encoding.ASCII.GetBytes("xxrock-granite");
            Assert.Same(first, pool.InternAscii(bytes, 2, "rock-granite".Length));
            Assert.NotSame(first, pool.Intern("rock-granites"));
        }

        [Fact]
        public void BlockCodeCounts_manual_path_behaves_like_the_old_dictionary()
        {
            var counts = new BlockCodeCounts();
            counts["rock-granite"] = 900;
            counts["ore-quartz-granite"] = 120;
            counts["rock-granite"] = 950; // overwrite, not add

            Assert.Equal(2, counts.Count);
            Assert.Equal(950, counts["rock-granite"]);
            Assert.True(counts.ContainsKey("ore-quartz-granite"));
            Assert.False(counts.ContainsKey("ore-cassiterite"));
            Assert.Equal(1070, counts.Values.Sum());
            Assert.Throws<KeyNotFoundException>(() => counts["missing"]);
        }

        [Fact]
        public void BlockCodeCounts_packed_path_maps_ids_through_the_registry()
        {
            var counts = new BlockCodeCounts();
            counts.Reset(Registry((7, "ore-cassiterite"), (9, "rock-granite")), 2);
            counts.Add(9, 900);
            counts.Add(7, 120);

            Assert.Equal(2, counts.Count);
            Assert.Equal(900, counts["rock-granite"]);
            Assert.Equal(120, counts["ore-cassiterite"]);
            Assert.Equal(new[] { "rock-granite", "ore-cassiterite" }, counts.Keys.ToArray());
            Assert.Equal(1020, counts.Values.Sum());
            Assert.False(counts.ContainsKey("id:3"));
        }

        [Fact]
        public void BlockCodeCounts_merges_ids_that_share_a_code_only_when_read()
        {
            var counts = new BlockCodeCounts();
            counts.Reset(Registry((1, "rock-granite"), (2, "rock-granite"), (3, "ore-quartz-granite")), 3);
            counts.Add(1, 5);
            counts.Add(2, 7);
            counts.Add(3, 2);

            // packed by id: the duplicate code is summed on enumeration, not stored as a dictionary
            Assert.Equal(2, counts.Count);
            Assert.Equal(12, counts["rock-granite"]);
            Assert.Equal(2, counts["ore-quartz-granite"]);
            Assert.Equal(new[] { "rock-granite", "ore-quartz-granite" }, counts.Keys.ToArray());
            Assert.Equal(14, counts.Values.Sum());
        }

        [Fact]
        public void Chunk_lists_are_created_only_when_a_record_is_added()
        {
            var chunk = new ChunkScanData { ChunkX = 3, ChunkY = 4, ChunkZ = 5 };
            Assert.Empty(chunk.Entities);
            Assert.Empty(chunk.BlockEntities);
            Assert.Equal(96, chunk.WorldX);
            Assert.Equal(128, chunk.WorldY);
            Assert.Equal(160, chunk.WorldZ);

            chunk.AddEntity(new EntityInfo { Species = "game:wolf" });
            chunk.AddBlockEntity(new BlockEntityInfo { Type = "Chest" });
            Assert.Single(chunk.Entities);
            Assert.Single(chunk.BlockEntities);
        }

        [Fact]
        public void BlockCodeCounts_without_a_registry_falls_back_to_id_codes()
        {
            var counts = new BlockCodeCounts();
            counts.Reset(null, 1);
            counts.Add(3, 5);
            Assert.Equal(5, counts["id:3"]);
        }

        [Fact]
        public void Registry_flags_duplicate_codes_for_the_by_code_fallback()
        {
            Assert.False(Registry((1, "rock-granite"), (2, "ore-quartz-granite")).HasDuplicateCodes);
            Assert.True(Registry((1, "rock-granite"), (2, "rock-granite")).HasDuplicateCodes);
        }

        [Fact]
        public void BlockCodeCounts_rejects_a_count_that_does_not_fit_the_packed_word()
        {
            var counts = new BlockCodeCounts();
            counts.Reset(null, 1);
            Assert.Throws<ArgumentOutOfRangeException>(() => counts.Add(1, ushort.MaxValue + 1L));
        }

        // ---- synthetic block registry (gamedata with a BlockIDs section), as in ChiselMaterialsTests ----
        private static byte[] Varint(int v)
        {
            var ms = new MemoryStream();
            while (v >= 0x80) { ms.WriteByte((byte)(v | 0x80)); v >>= 7; }
            ms.WriteByte((byte)v);
            return ms.ToArray();
        }

        private static byte[] BeEntry(int id, string code)
        {
            var ms = new MemoryStream();
            ms.WriteByte(0x08); ms.Write(Varint(id));                       // field1 varint = id
            var cb = Encoding.UTF8.GetBytes(code);
            ms.WriteByte(0x12); ms.Write(Varint(cb.Length)); ms.Write(cb);  // field2 len-delim = code
            return ms.ToArray();
        }

        private static BlockRegistry Registry(params (int Id, string Code)[] items)
        {
            var payload = new MemoryStream();
            foreach (var (id, code) in items)
            {
                var e = BeEntry(id, code);
                payload.WriteByte(0x12); payload.Write(Varint(e.Length)); payload.Write(e);
            }
            var p = payload.ToArray();
            var gd = new MemoryStream();
            gd.Write(Encoding.ASCII.GetBytes("BlockIDs"));
            gd.WriteByte(0x12); gd.Write(Varint(p.Length)); gd.Write(p);
            return BlockRegistry.FromGamedata(gd.ToArray());
        }
    }
}
