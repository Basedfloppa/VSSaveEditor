using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VSCore;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>Chiseled blocks (Chisel/MicroBlock): the material is read from the block entity and used
    /// for the column color instead of the generic neutral tone.</summary>
    public class ChiselMaterialsTests
    {
        // ---- building a synthetic ECX block entity blob ----
        private static byte[] Lp(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            var ms = new MemoryStream();
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
            return ms.ToArray();
        }
        private static byte[] I32(int v) => BitConverter.GetBytes(v);
        private static byte[] IntArray(params int[] ids)
        {
            var ms = new MemoryStream();
            ms.Write(BitConverter.GetBytes(ids.Length));
            foreach (var id in ids) ms.Write(BitConverter.GetBytes(id));
            return ms.ToArray();
        }
        private static byte[] StringArray(params string[] items)
        {
            var ms = new MemoryStream();
            ms.Write(BitConverter.GetBytes(items.Length));
            foreach (var s in items) ms.Write(Lp(s));
            return ms.ToArray();
        }
        private static byte[] Be(string type, params (byte T, string Key, byte[] Val)[] fields)
        {
            var ms = new MemoryStream();
            ms.Write(Lp(type));
            foreach (var (t, key, val) in fields)
            {
                ms.WriteByte(t);
                ms.Write(Lp(key));
                ms.Write(val);
            }
            return ms.ToArray();
        }

        [Fact(DisplayName = "ChiselMaterials: reads posx/posy/posz and materials (int32[])")]
        public void TryRead_Chisel()
        {
            var blob = Be("MicroBlock",
                (1, "posx", I32(512073)),
                (1, "posy", I32(64)),
                (1, "posz", I32(511448)),
                (5, "blockCode", Lp("microblock")),
                (ChiselMaterials.IntArrayType, "materials", IntArray(11219, 42)),
                (1, "decorRot", I32(0)),
                (8, "emitSideAo", new byte[] { 1 }));   // an unknown type at the tail does not get in the way

            Assert.True(ChiselMaterials.TryRead(blob, null, out int x, out int y, out int z, out var mats));
            Assert.Equal(512073, x);
            Assert.Equal(64, y);
            Assert.Equal(511448, z);
            Assert.Equal(new[] { 11219, 42 }, mats);
        }

        [Fact(DisplayName = "ChiselMaterials: materials as an array of STRING codes (type 0x0A)")]
        public void TryRead_StringMaterials()
        {
            var reg = Registry((5, "rock-peridotite"), (6, "planks-aged-we"));
            var blob = Be("MicroBlock",
                (1, "posx", I32(10)), (1, "posy", I32(20)), (1, "posz", I32(30)),
                (5, "blockCode", Lp("microblock")),
                (ChiselMaterials.StringArrayType, "materials", StringArray("rock-peridotite", "game:planks-aged-we")));

            Assert.True(ChiselMaterials.TryRead(blob, reg, out int x, out int y, out int z, out var mats));
            Assert.Equal((10, 20, 30), (x, y, z));
            Assert.Equal(new[] { 5, 6 }, mats);   // a code with the "game:" domain resolves as well
        }

        [Fact(DisplayName = "ChiselMaterials: a non-chiseled block and missing materials — false")]
        public void TryRead_Rejects()
        {
            var chest = Be("Chest",
                (1, "posx", I32(1)), (1, "posy", I32(2)), (1, "posz", I32(3)),
                (ChiselMaterials.IntArrayType, "materials", IntArray(5)));
            Assert.False(ChiselMaterials.TryRead(chest, null, out _, out _, out _, out _));

            var noMats = Be("Chisel",
                (1, "posx", I32(1)), (1, "posy", I32(2)), (1, "posz", I32(3)),
                (5, "blockCode", Lp("chiseledblock")));
            Assert.False(ChiselMaterials.TryRead(noMats, null, out _, out _, out _, out _));

            Assert.False(ChiselMaterials.TryRead(null, null, out _, out _, out _, out _));
            Assert.False(ChiselMaterials.TryRead(new byte[] { 3, 1, 2, 3 }, null, out _, out _, out _, out _));
        }

        // ---- synthetic block registry (gamedata with a BlockIDs section) ----
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

        private static TerrainMap Flat(BlockRegistry reg, ushort topId)
        {
            int n = 16;
            var tm = new TerrainMap
            {
                MinX = 0, MinZ = 0, Width = 4, Height = 4,
                SurfaceY = new int[n], BottomY = new int[n], TopBlock = new ushort[n], WaterDepth = new int[n],
                MinSurface = 1, MaxSurface = 1, Registry = reg
            };
            for (int i = 0; i < n; i++) { tm.SurfaceY[i] = 1; tm.TopBlock[i] = topId; }
            return tm;
        }

        [Fact(DisplayName = "a chiseled block's color equals the color of its material from the block entity")]
        public void RenderedColor_UsesMaterial()
        {
            var reg = Registry((1, "chiseledblock"), (2, "rock-basalt"));
            Assert.Equal("chiseledblock", reg.CodeOf(1));
            Assert.Equal("rock-basalt", reg.CodeOf(2));

            var chiseled = Flat(reg, topId: 1);
            chiseled.ChiselMaterial[TerrainMap.PackColumn(2, 1)] = 2;   // column (2,1) is made of basalt
            var basalt = Flat(reg, topId: 2);

            int col = 1 * 4 + 2;   // (x=2, z=1)
            var withoutMaterial = TerrainColorizer.ColumnColor(chiseled, 0, 1, null, TerrainColorizer.ModeBlocks, 1);
            var withMaterial = TerrainColorizer.ColumnColor(chiseled, col, 1, null, TerrainColorizer.ModeBlocks, 1);
            var plainBasalt = TerrainColorizer.ColumnColor(basalt, 0, 1, null, TerrainColorizer.ModeBlocks, 1);

            // Block color on the map is noisy: the game samples a random texture texel, so two
            // different columns of the same material differ by a few units. We check the tone,
            // not bitwise equality.
            Assert.True(Close(plainBasalt, withMaterial, 12), $"{plainBasalt} vs {withMaterial}: the material was not substituted");
            Assert.False(Close(plainBasalt, withoutMaterial, 12));   // without a material the tone is the neutral "carved" one
        }

        [Fact(DisplayName = "registry: AnyCodeContains finds chisel/microblock")]
        public void Registry_AnyCodeContains()
        {
            var reg = Registry((1, "chiseledblock"), (2, "rock-basalt"), (3, "microblock"));
            Assert.True(reg.AnyCodeContains("chisel"));
            Assert.True(reg.AnyCodeContains("microblock"));
            Assert.False(reg.AnyCodeContains("copper"));
        }

        private static bool Close((byte R, byte G, byte B) a, (byte R, byte G, byte B) b, int tol)
            => Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;
    }
}
