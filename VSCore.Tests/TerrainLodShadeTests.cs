using Xunit;

namespace VSCore.Tests;

/// <summary>
///     LOD column averaging: the game map shade goes up to 1.48, so bright snow with it yields
///     more than 255 per channel. A column's contribution must be clamped BEFORE summing — otherwise
///     the average overflows the byte and wraps modulo 256: white snow turns almost black,
///     while yellow flowers (which have less headroom to 255) stay bright. On screen this looked
///     like a yellow-and-black artifact that appeared only at LOD.
/// </summary>
public class TerrainLodShadeTests
{
    [Fact(DisplayName = "LOD: the column contribution is clamped to 255 — white snow stays white")]
    public void ShadedContribution_ClampsBrightSnow()
    {
        var snow = ((byte)230, (byte)229, (byte)251);
        var add = TerrainColorizer.ShadedContribution(snow, 1.48);
        Assert.Equal(255, add.R);
        Assert.Equal(255, add.G);
        Assert.Equal(255, add.B);
    }

    [Fact(DisplayName = "LOD: the 2x2 average of white snow with shade does not wrap modulo 256")]
    public void LodAverage_BrightSnowCell_DoesNotWrap()
    {
        var snow = ((byte)230, (byte)229, (byte)251);
        int r = 0, g = 0, b = 0;
        for (var i = 0; i < 4; i++)
        {
            var add = TerrainColorizer.ShadedContribution(snow, 1.48);
            r += add.R;
            g += add.G;
            b += add.B;
        }

        // Same as in TerrainView: (byte)(rS / seen), where seen = 4.
        Assert.Equal(255, (byte)(r / 4));
        Assert.Equal(255, (byte)(g / 4));
        Assert.Equal(255, (byte)(b / 4));

        // Without the clamp this would be (byte)340 = 84 — almost black.
        Assert.NotEqual((byte)84, (byte)(r / 4));
    }
}
