using System.Collections.Generic;
using VSCore;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>Block palette: chiseled/modded codes must not look black,
    /// and unknown codes must get a stable, distinguishable color.</summary>
    public class BlockColorPaletteTests
    {
        private static int Lum((byte r, byte g, byte b) c) => c.r + c.g + c.b;

        [Fact(DisplayName = "chiseledblock: neutral \"carved\" tone, not black")]
        public void ChiseledBlock_NotBlack()
        {
            var c = BlockColorPalette.ColorFor("chiseledblock", 648);
            Assert.Equal((152, 144, 126), c);
            Assert.True(Lum(c) >= 300, $"chiseledblock is too dark: {c}");
        }

        [Fact(DisplayName = "chiseledblock-snow stays snowy (the material matters more than the carving)")]
        public void ChiseledSnow()
        {
            var c = BlockColorPalette.ColorFor("chiseledblock-snow", 647);
            Assert.True(c.r > 190 && c.g > 190 && c.b > 190, $"expected a light tone: {c}");
        }

        [Fact(DisplayName = "modded/unknown codes: bright and varied")]
        public void UnknownCodes_AreBrightAndVaried()
        {
            var seen = new HashSet<(byte, byte, byte)>();
            for (int i = 0; i < 40; i++)
            {
                var c = BlockColorPalette.ColorFor($"somemod:block-{i}", (ushort)(1000 + i));
                Assert.True(Lum(c) >= 300, $"somemod:block-{i} is too dark: {c}");
                seen.Add(c);
            }
            Assert.True(seen.Count >= 6, $"modded block colors are too uniform: {seen.Count}");
        }

        [Fact(DisplayName = "the fallback is stable for one code and distinct across different codes")]
        public void Fallback_StableAndDistinct()
        {
            var a1 = BlockColorPalette.ColorFor("mod:foo", 100);
            var a2 = BlockColorPalette.ColorFor("mod:foo", 100);
            var b = BlockColorPalette.ColorFor("mod:bar", 100);
            Assert.Equal(a1, a2);
            Assert.NotEqual(a1, b);
        }

        [Fact(DisplayName = "semantic materials are still recognized")]
        public void SemanticRules_StillWork()
        {
            Assert.True(BlockColorPalette.IsWater("water-still-1"));
            Assert.True(BlockColorPalette.ColorFor("marble", 1).r > 200);
            Assert.True(BlockColorPalette.ColorFor("copper-ingot", 2).r > BlockColorPalette.ColorFor("copper-ingot", 2).b);
            Assert.True(BlockColorPalette.ColorFor("thatch", 3).r > 150);
            // the specific rock type matters more than the generic "rock/stone"
            var basalt = BlockColorPalette.ColorFor("rock-basalt", 4);
            var granite = BlockColorPalette.ColorFor("rock-granite", 5);
            Assert.NotEqual(basalt, granite);
        }
    }
}
