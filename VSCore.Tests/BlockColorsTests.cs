using VSCore;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>
    /// Block colors from the game assets (the generated <c>BlockColors.txt</c> table): the map must
    /// take the ACTUAL block color instead of guessing from the name (which is why different rocks
    /// and their "shapes" — rock/cobblestone/stonebricks — used to collapse into one gray).
    /// </summary>
    public class BlockColorsTests
    {
        private static int Lum((byte r, byte g, byte b) c) => c.r + c.g + c.b;

        [Fact(DisplayName = "the color table from the assets is loaded and not empty")]
        public void Table_Loaded()
        {
            Assert.True(BlockColors.Loaded, "BlockColors.txt was not embedded into the build");
            Assert.True(BlockColors.Count > 5000, $"too few codes in the table: {BlockColors.Count}");
        }

        [Fact(DisplayName = "different rocks and their shapes get different actual colors (not a single gray)")]
        public void DifferentMaterials_HaveDifferentColors()
        {
            var claystone = BlockColorPalette.ColorFor("cobblestone-claystone", 100);
            var chert = BlockColorPalette.ColorFor("cobblestone-chert", 101);
            var granite = BlockColorPalette.ColorFor("cobblestone-granite", 102);
            Assert.NotEqual(claystone, chert);
            Assert.NotEqual(chert, granite);
            Assert.NotEqual(claystone, granite);

            // claystone/bauxite are brownish/reddish (R noticeably above B), slate is neutral
            Assert.True(claystone.r > claystone.b + 20, $"claystone should be brown: {claystone}");
            var bauxite = BlockColorPalette.ColorFor("rock-bauxite", 103);
            Assert.True(bauxite.r > bauxite.b + 60, $"bauxite should be red-brown: {bauxite}");
            var slate = BlockColorPalette.ColorFor("cobblestone-slate", 104);
            Assert.True(System.Math.Abs(slate.r - slate.b) < 25, $"slate should be neutral: {slate}");
        }

        [Fact(DisplayName = "the block shape matters: rock/cobblestone/stonebricks of the same rock differ")]
        public void Shapes_DifferForSameRock()
        {
            var rock = BlockColorPalette.ColorFor("rock-granite", 200);
            var cobble = BlockColorPalette.ColorFor("cobblestone-granite", 201);
            var bricks = BlockColorPalette.ColorFor("stonebricks-granite", 202);
            Assert.NotEqual(rock, cobble);
            Assert.NotEqual(rock, bricks);
            Assert.NotEqual(cobble, bricks);
            // and all three are stone-like (neither black nor bright)
            foreach (var c in new[] { rock, cobble, bricks })
                Assert.True(Lum(c) is > 120 and < 640, $"implausible stone tone: {c}");
        }

        [Fact(DisplayName = "a missing code is derived from the rock (shape x rock color)")]
        public void ShapeFallback_DerivesFromRock()
        {
            // stonebricks-obsidian is not in the table (obsidian is excluded from the recipes), but rock-obsidian is
            Assert.True(BlockColors.TryGet("rock-obsidian", out var obsidian));
            Assert.True(BlockColors.TryGet("stonebricks-obsidian", out var derived), "the color transfer by shape did not work");
            Assert.True(Lum(derived) < 200, $"obsidian should stay dark: {derived}");
            // the color is derived, not a copy of the rock color
            Assert.NotEqual(obsidian, derived);
            foreach (var (d, s) in new[] { (derived.r, obsidian.r), (derived.g, obsidian.g), (derived.b, obsidian.b) })
                Assert.True(d <= s || d - s < 40, "the shape must not lighten the material too much");
        }

        [Fact(DisplayName = "trailing states and the domain in the code do not get in the way of lookups")]
        public void CodeVariants_Resolve()
        {
            Assert.True(BlockColors.TryGet("game:cobblestone-claystone", out var withDomain));
            Assert.True(BlockColors.TryGet("cobblestone-claystone", out var plain));
            Assert.Equal(plain, withDomain);

            Assert.True(BlockColors.TryGet("stonebricks-andesite-free", out var withState));
            Assert.True(BlockColors.TryGet("stonebricks-andesite", out var baseCode));
            Assert.Equal(baseCode, withState);
        }

        [Fact(DisplayName = "shader-tinted blocks: the base comes from the table, the default color from semantics")]
        public void ShaderTinted_UsesSemantics()
        {
            Assert.True(BlockColorPalette.IsShaderTinted("water-still-7"));
            Assert.True(BlockColorPalette.IsShaderTinted("leaves-oak"));
            Assert.False(BlockColorPalette.IsShaderTinted("planks-oak"));
            Assert.False(BlockColorPalette.IsShaderTinted("rock-andesite"));

            // Water is present in the table — but as the GRAY base texture color: the in-game map multiplies it
            // by the climate tint (climateWaterTint), and only then does it become blue-green.
            Assert.True(BlockColors.TryGet("water-still-7", out var waterBase), "the base water color is needed for the tint");
            Assert.True(waterBase.r == waterBase.g && waterBase.g == waterBase.b, $"the water base should be gray: {waterBase}");

            var water = BlockColorPalette.ColorFor("water-still-7", 300);
            Assert.True(water.b > water.r, $"water should be blue: {water}");
            var leaves = BlockColorPalette.ColorFor("leaves-oak", 301);
            Assert.True(leaves.g > leaves.r, $"foliage should be green: {leaves}");
        }

        [Fact(DisplayName = "a chiseled block takes its color from its material: every material yields its own tone")]
        public void ChiselMaterial_ColorsDiffer()
        {
            var planks = BlockColorPalette.ColorFor("planks-veryaged-we", 400);
            var rock = BlockColorPalette.ColorFor("rock-andesite", 401);
            var brick = BlockColorPalette.ColorFor("stonebricks-claystone", 402);
            Assert.True(planks.r > planks.b + 10, $"planks should be warm: {planks}");
            Assert.NotEqual(planks, rock);
            Assert.NotEqual(rock, brick);
            Assert.NotEqual(planks, brick);
        }
    }
}
