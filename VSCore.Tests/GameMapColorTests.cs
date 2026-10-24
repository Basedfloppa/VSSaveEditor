using System;
using System.Collections.Generic;
using System.IO;
using VSCore;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>
    /// The game map colors a block differently from the "average texture color": first the rain surface
    /// (grass and flowers are permeable and invisible on the map), then the climate/season tint.
    /// These tests pin down three things that are easy to break unnoticed: olive (not neon)
    /// grass, dark turquoise water, and the fact that the season really is read from the save.
    /// </summary>
    public class GameMapColorTests
    {
        private static TerrainMap Map(double yearRel = 0.25, int seaLevel = 192) => new()
        {
            MinX = 0, MinZ = 0, Width = 1, Height = 1,
            YearRel = yearRel,
            SeaLevel = seaLevel,
            SurfaceY = new[] { 198 },
            BottomY = new[] { 100 },
            TopBlock = new ushort[] { 0 },
            WaterDepth = new[] { 0 },
            MapY = new ushort[] { 198 },
            MapBlock = new ushort[] { 0 },
            Shadow = new byte[] { 128 },
            MinSurface = 198, MaxSurface = 198
        };

        [Fact(DisplayName = "rain surface: grass/flowers are permeable, soil and water are not")]
        public void RainPermeable_Flags()
        {
            Assert.True(BlockColors.IsRainPermeable("tallgrass-medium-free"));
            Assert.False(BlockColors.IsRainPermeable("tallgrass-medium-snow"));
            Assert.False(BlockColors.IsRainPermeable("soil-medium-normal"));
            Assert.False(BlockColors.IsRainPermeable("water-still-7"));
            Assert.False(BlockColors.IsRainPermeable(null));
            Assert.True(BlockColors.IsRainPermeable("somemod:flower-blue"), "a modded flower is permeable");
            Assert.False(BlockColors.IsRainPermeable("somemod:rock-slab"), "a modded rock is not");
        }

        [Fact(DisplayName = "grass is olive in spring and greener in summer")]
        public void Soil_IsOliveInSpring_GreenInSummer()
        {
            var spring = TerrainColorizer.BlockColor(Map(0.25), "soil-medium-normal", 0, 10, 198, 10, textured: false);
            var summer = TerrainColorizer.BlockColor(Map(0.50), "soil-medium-normal", 0, 10, 198, 10, textured: false);

            Assert.True(spring.R > spring.B + 40, $"spring grass should be olive: {spring}");
            Assert.True(spring.G > spring.B + 30, $"spring grass should be olive: {spring}");
            Assert.True(summer.G > spring.G, $"summer has more green: spring={spring} summer={summer}");
        }

        [Fact(DisplayName = "forest floor: only the grass layer is tinted (BlockForestFloor)")]
        public void ForestFloor_TintsGrassLayerOnly()
        {
            var bare = TerrainColorizer.BlockColor(Map(), "soil-low-none", 0, 10, 198, 10, textured: false);
            var level1 = TerrainColorizer.BlockColor(Map(), "forestfloor-1", 0, 10, 198, 10, textured: false);
            var level7 = TerrainColorizer.BlockColor(Map(), "forestfloor-7", 0, 10, 198, 10, textured: false);

            // The whole color used to be multiplied by the tint: on forestfloor-1 blue collapsed to almost zero.
            // The game tints only the grass layer, so level 1 is olive rather than dark brown.
            Assert.True(level1.B > 12, $"forestfloor-1 must not be almost black: {level1}");
            Assert.True(level1.G > level1.B + 20, $"forestfloor-1 should be olive: {level1}");
            Assert.True(level1.B < bare.B, $"the litter base stays visible: {level1} / {bare}");

            // With the in-game noise (weight 0.6 — a random forestoverlay7 texel) level 1 is even closer
            // to grass: that is how ChunkMapLayer.GenerateChunkImage draws it.
            var noisy1 = TerrainColorizer.BlockColor(Map(), "forestfloor-1", 0, 10, 198, 10);
            var noisy7 = TerrainColorizer.BlockColor(Map(), "forestfloor-7", 0, 10, 198, 10);
            Assert.True(Math.Abs(noisy1.B - noisy7.B) < 25, $"levels 1 and 7 are close in blue: {noisy1} / {noisy7}");

            // forestfloor-0 returns GetColorWithoutTint in the game — the base without a tint.
            var zero = TerrainColorizer.BlockColor(Map(), "forestfloor-0", 0, 10, 198, 10, textured: false);
            BlockColors.TryGet("forestfloor-0", out var zeroRaw);
            Assert.Equal(zeroRaw, zero);
            Assert.NotEqual(bare, zero);   // the litter base is darker than bare soil
        }

        [Fact(DisplayName = "sparse/verysparse: the base is visible (soil/peat/clay) rather than pure grass")]
        public void SparseCoverage_ShowsBaseMaterial()
        {
            var normal = TerrainColorizer.BlockColor(Map(), "soil-low-normal", 0, 10, 198, 10, textured: false);
            var sparse = TerrainColorizer.BlockColor(Map(), "soil-low-sparse", 0, 10, 198, 10, textured: false);
            var very = TerrainColorizer.BlockColor(Map(), "soil-low-verysparse", 0, 10, 198, 10, textured: false);
            var bare = TerrainColorizer.BlockColor(Map(), "soil-low-none", 0, 10, 198, 10, textured: false);

            static int Dist((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
                => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

            Assert.True(Dist(sparse, bare) < Dist(normal, bare), $"sparse is closer to soil: {sparse} / {normal} / {bare}");
            Assert.True(Dist(very, bare) < Dist(sparse, bare), $"verysparse is closer still: {very} / {sparse}");

            // Peat and clay now differ from grass on the map instead of being tinted like grass.
            var peat = TerrainColorizer.BlockColor(Map(), "peat-verysparse", 0, 10, 198, 10, textured: false);
            var redClay = TerrainColorizer.BlockColor(Map(), "rawclay-red-verysparse", 0, 10, 198, 10, textured: false);
            var blueClay = TerrainColorizer.BlockColor(Map(), "rawclay-blue-verysparse", 0, 10, 198, 10, textured: false);
            Assert.True(peat.G < normal.G - 5, $"peat is darker than grass: {peat} / {normal}");
            Assert.True(redClay.R > redClay.B + 20, $"red clay stays red: {redClay}");
            Assert.True(blueClay.B > redClay.B, $"blue clay is bluer than red: {blueClay} / {redClay}");
        }

        [Fact(DisplayName = "the table holds the base and the grass coverage weight")]
        public void BlockColors_ParsesGrassBaseAndWeight()
        {
            Assert.True(BlockColors.TryGetMap("forestfloor-1", out var ff1));
            Assert.NotNull(ff1.GrassBase);
            Assert.InRange(ff1.GrassWeight, 0.13f, 0.15f);

            Assert.True(BlockColors.TryGetMap("soil-medium-sparse", out var sparse));
            Assert.Equal(0.75f, sparse.GrassWeight, 3);
            Assert.NotNull(sparse.GrassBase);

            Assert.True(BlockColors.TryGetMap("soil-medium-none", out var none));
            Assert.Equal(0f, none.GrassWeight);
            Assert.NotNull(none.GrassBase);

            Assert.True(BlockColors.TryGetMap("rock-andesite", out var rock));
            Assert.Null(rock.GrassBase);
            Assert.Equal(0f, rock.GrassWeight);
        }

        [Fact(DisplayName = "water on the map is dark turquoise, not bright blue")]
        public void Water_IsDarkTeal()
        {
            var w = TerrainColorizer.BlockColor(Map(), "water-still-7", 0, 10, 192, 10, textured: false);
            Assert.True(w.G > w.R + 20, $"water is green-blue: {w}");
            Assert.True(w.B > w.R + 15, $"water is green-blue: {w}");
            Assert.True(w.R < 80 && w.G < 120, $"water should be dark: {w}");
        }

        [Fact(DisplayName = "without color maps the tint is one (stone does not change)")]
        public void Tint_NoMaps_IsIdentity()
        {
            Assert.Equal((255, 255, 255), MapTint.For(null, null, 1, 2, 3, 0.25, 192));
            BlockColors.TryGet("rock-andesite", out var raw);
            Assert.Equal(raw, TerrainColorizer.BlockColor(Map(), "rock-andesite", 0, 1, 195, 2, textured: false));
        }

        [Fact(DisplayName = "the color LUTs are loaded from the resource")]
        public void ColorMaps_Loaded()
        {
            Assert.True(ColorMaps.Count >= 10, $"too few LUTs: {ColorMaps.Count}");
            Assert.NotEqual((255, 255, 255), MapTint.For("climatePlantTint", "seasonalGrass", 1, 198, 2, 0.25, 192));
        }

        [Fact(DisplayName = "save calendar: YearRel from TotalGameSeconds and daysPerMonth")]
        public void YearRel_FromGamedata()
        {
            var g = Gamedata(totalSeconds: 70_039_109, hoursPerDay: 24f, daysPerMonth: 30);
            var rel = VSSaveFile.YearRelFromGamedata(g);
            Assert.NotNull(rel);
            Assert.InRange(rel!.Value, 0.24, 0.26);   // 810.6 days in a 360-day year

            var nineMonth = VSSaveFile.YearRelFromGamedata(Gamedata(70_039_109, 24f, 9));
            Assert.NotNull(nineMonth);
            Assert.InRange(nineMonth!.Value, 0.0, 1.0);
        }

        [Fact(DisplayName = "the spawn Y is read from gamedata and used as sea level")]
        public void SpawnY_FromGamedata()
        {
            Assert.Equal(193, VSSaveFile.SpawnYFromGamedata(Gamedata(70_039_109, 24f, 30, spawnY: 193)));
            Assert.Null(VSSaveFile.SpawnYFromGamedata(Array.Empty<byte>()));
        }

        [Fact(DisplayName = "no calendar — YearRel = null (no tint applied)")]
        public void YearRel_Missing()
        {
            Assert.Null(VSSaveFile.YearRelFromGamedata(Array.Empty<byte>()));
        }

        // ---- synthetic SaveGame: field 12 (TotalGameSeconds), 25 (HoursPerDay float),
        //      31 (WorldConfiguration bytes c daysPerMonth) ----
        private static byte[] Gamedata(long totalSeconds, float hoursPerDay, int daysPerMonth, int spawnY = 0)
        {
            using var ms = new MemoryStream();
            Varint(ms, 12, (ulong)totalSeconds);
            VarintTag(ms, 25, 5);
            ms.Write(BitConverter.GetBytes(hoursPerDay));
            var cfg = System.Text.Encoding.ASCII.GetBytes($"daysPerMonth\x02{daysPerMonth}");
            VarintTag(ms, 31, 2);
            VarintRaw(ms, (ulong)cfg.Length);
            ms.Write(cfg);
            if (spawnY > 0)
            {
                using var sp = new MemoryStream();
                Varint(sp, 2, (ulong)spawnY);
                var spb = sp.ToArray();
                VarintTag(ms, 40, 2);
                VarintRaw(ms, (ulong)spb.Length);
                ms.Write(spb);
            }
            return ms.ToArray();
        }

        private static void VarintTag(Stream s, int field, int wire) => VarintRaw(s, (ulong)(field << 3 | wire));
        private static void Varint(Stream s, int field, ulong v) { VarintTag(s, field, 0); VarintRaw(s, v); }
        private static void VarintRaw(Stream s, ulong v)
        {
            while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
            s.WriteByte((byte)v);
        }
    }
}
