using System;
using System.Collections.Generic;
using VSCore;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>P0-7/P0-8: garbage and truncated chunk blobs must not bring the decoder down.</summary>
    public class ChunkBlockDecoderNegativeTests
    {
        private static ushort[] Air() => new ushort[ChunkBlockDecoder.CellsPerChunk];

        private static void WriteVarint(List<byte> dst, long v)
        {
            while (v >= 0x80) { dst.Add((byte)(v | 0x80)); v >>= 7; }
            dst.Add((byte)v);
        }

        private static byte[] LenDelim(int field, params byte[] payload)
        {
            var blob = new List<byte>();
            WriteVarint(blob, ((long)field << 3) | 2);
            WriteVarint(blob, payload.Length);
            blob.AddRange(payload);
            return blob.ToArray();
        }

        [Fact]
        public void DecodeBlocksCompressed_ShorterThanMarker_ReturnsAir()
        {
            Assert.Equal(Air(), ChunkBlockDecoder.DecodeBlocksCompressed(Array.Empty<byte>()));
            Assert.Equal(Air(), ChunkBlockDecoder.DecodeBlocksCompressed(new byte[] { 0x01, 0x02, 0x03 }));
        }

        [Fact]
        public void DecodeChunkBlocks_TruncatedField_ReturnsAir()
        {
            // the declared blocksCompressed field is 2 bytes: the int32 marker does not fit
            var blob = LenDelim(1, 0x01, 0x02);
            Assert.Equal(Air(), ChunkBlockDecoder.DecodeChunkBlocks(blob));
            Assert.Equal(Air(), ChunkBlockDecoder.DecodeChunkBlocksMerged(blob));
            Assert.Equal(Air(), ChunkBlockDecoder.DecodeChunkBlocksMergedOnePass(blob));
        }

        [Fact]
        public void DecodeChunkBlocks_FieldLengthBeyondBlob_ReturnsAir()
        {
            // field1 LEN declares 200 bytes, the blob holds 3
            var blob = new byte[] { 0x0A, 0xC8, 0x01, 0xAA, 0xBB, 0xCC };
            Assert.Equal(Air(), ChunkBlockDecoder.DecodeChunkBlocks(blob));
        }

        [Fact]
        public void PaletteAndPlaneCount_TruncatedField_AreEmpty()
        {
            Assert.Empty(ChunkBlockDecoder.DecodePaletteOfField(LenDelim(1, 0x01, 0x02), 1));
            Assert.Empty(ChunkBlockDecoder.DecodeLiquidPalette(LenDelim(16, 0x00)));
            Assert.Equal(0, ChunkBlockDecoder.DecodePaletteSize(LenDelim(1, 0x00)));
            Assert.Equal(0, ChunkBlockDecoder.CountPlanesInData(LenDelim(1, 0x01, 0x02)));
            Assert.Equal(0, ChunkBlockDecoder.CountPlanesInData(Array.Empty<byte>()));
        }

        [Fact]
        public void GarbageMarkersInBothFields_DoNotThrow()
        {
            // int.MaxValue marker: dataOffset = 4 + num overflows and garbage data follows
            var garbage = new byte[] { 0xFF, 0xFF, 0xFF, 0x7F, 0x01, 0x02, 0x03 };
            var blob = new List<byte>();
            blob.AddRange(LenDelim(1, garbage));
            blob.AddRange(LenDelim(16, garbage));
            var b = blob.ToArray();

            var ex = Record.Exception(() =>
            {
                ChunkBlockDecoder.DecodeChunkBlocks(b);
                ChunkBlockDecoder.DecodeChunkBlocksMerged(b);
                ChunkBlockDecoder.DecodeChunkBlocksMergedOnePass(b);
                ChunkBlockDecoder.DecodePalette(b);
                ChunkBlockDecoder.DecodeLiquidPalette(b);
                ChunkBlockDecoder.CountPlanesInData(b);
            });
            Assert.Null(ex);
        }
    }
}
