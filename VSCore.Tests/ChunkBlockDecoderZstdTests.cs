using System;
using VSCore;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>ZSTD decoder: a context reused across a stream (P2-3), a correct round-trip
    /// on repeated calls, and safe range bounds.</summary>
    public class ChunkBlockDecoderZstdTests
    {
        [Fact]
        public void ZstdDecompress_RoundTrips_And_ReusesContext()
        {
            var rnd = new Random(1234);
            using var comp = new ZstdSharp.Compressor(3);
            for (int i = 0; i < 64; i++)
            {
                var data = new byte[rnd.Next(1, 8192)];
                rnd.NextBytes(data);
                var compressed = comp.Wrap(data).ToArray();

                // the data does not start at offset zero — checks off/len and resilience to garbage around it
                var buf = new byte[compressed.Length + 10];
                Buffer.BlockCopy(compressed, 0, buf, 5, compressed.Length);

                Assert.Equal(data, ChunkBlockDecoder.ZstdDecompress(buf, 5, compressed.Length));
                // a second call on the same stream: the context is reused, but the result does not "stick"
                Assert.Equal(data, ChunkBlockDecoder.ZstdDecompress(buf, 5, compressed.Length));
            }
        }

        [Fact]
        public void ZstdDecompress_BadRange_ReturnsEmpty()
        {
            var b = new byte[8];
            Assert.Empty(ChunkBlockDecoder.ZstdDecompress(b, 4, 0));    // zero length
            Assert.Empty(ChunkBlockDecoder.ZstdDecompress(b, 8, 4));    // out of bounds on the right
            Assert.Empty(ChunkBlockDecoder.ZstdDecompress(b, -1, 4));   // negative offset
        }

        [Fact]
        public void ZstdDecompress_Bomb_ReturnsEmptyWithoutThrowing()
        {
            // 8 MiB of zeros compress to a few kilobytes: without an output cap this is a memory DoS.
            using var comp = new ZstdSharp.Compressor(3);
            var bomb = comp.Wrap(new byte[8 << 20]).ToArray();
            Assert.True(bomb.Length < 1 << 20, $"the bomb should be compact, not {bomb.Length} bytes");

            byte[] output = Array.Empty<byte>();
            var ex = Record.Exception(() => output = ChunkBlockDecoder.ZstdDecompress(bomb, 0, bomb.Length));

            Assert.Null(ex);
            Assert.Empty(output);

            // after a failed frame the cached context is reset: the next chunk on the same stream decodes
            var ok = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
            var okCompressed = comp.Wrap(ok).ToArray();
            Assert.Equal(ok, ChunkBlockDecoder.ZstdDecompress(okCompressed, 0, okCompressed.Length));
        }
    }
}
