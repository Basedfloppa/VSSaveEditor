using System;
using System.Collections.Generic;
using System.Text;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>
    ///     The entity position lives in the nameless field-6 blob (three float64 values), so addressing it
    ///     means searching the chunk AABB, and editing means overwriting the fixed 8-byte components.
    /// </summary>
    public class EntityPositionTests
    {
        private const int ChunkWorldX = 511712; // chunk 15991,0,15983
        private const int ChunkWorldY = 0;
        private const int ChunkWorldZ = 511456;

        private static byte[] Lp(string s)
        {
            var b = new byte[1 + s.Length];
            b[0] = (byte)s.Length;
            Encoding.ASCII.GetBytes(s, 0, s.Length, b, 1);
            return b;
        }

        private static byte[] F32(string key, float v)
            => Concat(new byte[] { 4 }, Lp(key), BitConverter.GetBytes(v));

        private static byte[] I32(string key, int v)
            => Concat(new byte[] { 1 }, Lp(key), BitConverter.GetBytes(v));

        private static byte[] Triple(double x, double y, double z)
            => Concat(BitConverter.GetBytes(x), BitConverter.GetBytes(y), BitConverter.GetBytes(z));

        private static byte[] Concat(params byte[][] parts)
        {
            var ms = new System.IO.MemoryStream();
            foreach (var p in parts) ms.Write(p, 0, p.Length);
            return ms.ToArray();
        }

        /// <summary>A real-looking blob: header, fields, then the float64 triple.</summary>
        private static byte[] Blob(bool duplicateTriple = false, double secondX = 511732.5)
        {
            var parts = new List<byte[]> { Lp("EntityAgent"), Lp("1.22.7"), I32("currenthealth", 5), F32("animalWeight", 1f) };
            parts.Add(Triple(511732.5, 29, 511464.5));
            if (duplicateTriple) parts.Add(Triple(secondX, 29, 511464.5));
            parts.Add(Lp("armorstand-aged"));
            return Concat(parts.ToArray());
        }

        [Fact(DisplayName = "EntityPosition: the triple inside the AABB is found, outside it is not")]
        public void Locate_finds_triple_inside_chunk()
        {
            var blob = Blob();
            var found = EntityPositionCodec.Locate(blob, ChunkWorldX, ChunkWorldY, ChunkWorldZ);
            var one = Assert.Single(found);
            Assert.Equal(511732.5, one.X, 6);
            Assert.Equal(29, one.Y, 6);
            Assert.Equal(511464.5, one.Z, 6);

            // The same blob, but a "foreign" chunk: the triple does not fall inside its AABB.
            Assert.Empty(EntityPositionCodec.Locate(blob, ChunkWorldX + 10_000, ChunkWorldY, ChunkWorldZ));
        }

        [Fact(DisplayName = "EntityPosition: identical copies mean the edit is allowed; a mismatch means it is not")]
        public void AllAgree_requires_identical_copies()
        {
            var same = EntityPositionCodec.Locate(Blob(duplicateTriple: true), ChunkWorldX, ChunkWorldY, ChunkWorldZ);
            Assert.Equal(2, same.Count);
            Assert.True(EntityPositionCodec.AllAgree(same));

            var differ = EntityPositionCodec.Locate(Blob(duplicateTriple: true, secondX: 511740), ChunkWorldX,
                ChunkWorldY, ChunkWorldZ);
            Assert.Equal(2, differ.Count);
            Assert.False(EntityPositionCodec.AllAgree(differ));
        }

        [Fact(DisplayName = "EntityPosition: the edit survives a lossless round-trip through the parser")]
        public void Patch_survives_parse_and_serialize()
        {
            var blob = Blob(duplicateTriple: true);
            var rec = ChunkDataParser.ParseEntity(blob);
            var round = ChunkDataEditor.SerializeEntity(rec);
            Assert.Equal(blob, round); // re-serialization is byte-for-byte identical

            var before = EntityPositionCodec.Locate(round, ChunkWorldX, ChunkWorldY, ChunkWorldZ);
            Assert.Equal(2, before.Count);
            Assert.Equal(2, EntityPositionCodec.Patch(round, before, 511800.25, 100.5, 511500.75));

            var after = EntityPositionCodec.Locate(round, ChunkWorldX, ChunkWorldY, ChunkWorldZ);
            Assert.Equal(2, after.Count);
            Assert.All(after, a =>
            {
                Assert.Equal(511800.25, a.X, 6);
                Assert.Equal(100.5, a.Y, 6);
                Assert.Equal(511500.75, a.Z, 6);
            });
        }

        [Fact(DisplayName = "EntityPosition: a stale offset does not corrupt the blob (the value did not match)")]
        public void Patch_refuses_when_old_value_does_not_match()
        {
            var blob = Blob();
            var found = EntityPositionCodec.Locate(blob, ChunkWorldX, ChunkWorldY, ChunkWorldZ);
            // The edit is already applied to the blob — a write at the stale offset must change nothing.
            EntityPositionCodec.Write(blob, found[0].Offset, 1, 2, 3);
            Assert.Equal(0, EntityPositionCodec.Patch(blob, found, 511700, 30, 511400));
            Assert.Equal((1.0, 2.0, 3.0), EntityPositionCodec.Read(blob, found[0].Offset));
        }
    }
}
