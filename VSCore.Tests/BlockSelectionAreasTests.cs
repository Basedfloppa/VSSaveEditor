using System;
using System.Collections.Generic;
using System.Linq;
using VSCore;
using Xunit;

namespace VSCore.Tests;

/// <summary>
///     The area list of the "Find blocks" scope row must offer only the areas that contain the ticked codes.
///     The answer is computed from the scan histogram (ids per chunk), never by decoding chunks or building an
///     overlay, so these tests drive a hand-built model: two areas far apart, a code that occurs in one, in both,
///     or nowhere. The negative case matters most — an area with no match must never be offered, otherwise the
///     user is sent to a place where the overlay has nothing to show.
/// </summary>
public class BlockSelectionAreasTests
{
    private const string Ore = "ore-cassiterite";
    private const string Rock = "rock-granite";
    private const string Gold = "ore-gold";

    /// <summary>Chunk records at cy=0; a row may carry several codes at once, as a real chunk does.</summary>
    private static SaveScanResult Scan(IEnumerable<(int Cx, int Cz, (string Code, long Cells)[] Blocks)> rows)
    {
        var scan = new SaveScanResult();
        foreach (var (cx, cz, blocks) in rows)
        {
            var cd = new ChunkScanData { ChunkX = cx, ChunkY = 0, ChunkZ = cz };
            foreach (var (code, cells) in blocks) cd.BlockCodeCounts[code] = cells;
            scan.Chunks.Add(cd);
        }

        return scan;
    }

    /// <summary>Two areas: around the origin and around (100,100) — far enough apart to be two connected groups.</summary>
    private static ChunkAreas TwoAreas(SaveScanResult scan)
        => ChunkAreas.Build(scan.Chunks
            .Select(cd => (long)ChunkPosHelper.ToIndex(cd.ChunkX, cd.ChunkY, cd.ChunkZ))
            .ToList(), null);

    /// <summary>Home: (0,0) carries ore AND rock, (1,0) ore, (1,1) rock. Far: two rock-only rows.</summary>
    private static SaveScanResult TwoPatchModel() => Scan(new[]
    {
        (0, 0, new[] { (Ore, 12L), (Rock, 500L) }),
        (1, 0, new[] { (Ore, 4L) }),
        (1, 1, new[] { (Rock, 300L) }),
        (100, 100, new[] { (Rock, 700L) }),
        (101, 100, new[] { (Rock, 800L) })
    });

    [Fact]
    public void Only_areas_that_contain_the_code_are_offered()
    {
        var scan = TwoPatchModel();
        var areas = TwoAreas(scan);
        Assert.Equal(2, areas.Areas.Count); // the fixture really has two areas

        var home = areas.AreaAt(16, 16)!;
        var far = areas.AreaAt(100 * 32 + 16, 100 * 32 + 16)!;

        var matches = BlockSelectionAreas.Find(scan, areas, registry: null, new[] { Ore });
        var only = Assert.Single(matches);
        Assert.Same(home, only.Area);
        Assert.NotSame(far, only.Area);
        Assert.Equal(2, only.MatchChunks);           // two chunk records carry the ore
        Assert.Equal(16, only.MatchCells);           // 12 + 4
    }

    [Fact]
    public void A_code_that_occurs_nowhere_offers_no_area()
    {
        var scan = TwoPatchModel();
        var areas = TwoAreas(scan);
        Assert.Empty(BlockSelectionAreas.Find(scan, areas, registry: null, new[] { Gold }));
        Assert.Empty(BlockSelectionAreas.Find(scan, areas, registry: null, Array.Empty<string>()));
    }

    [Fact]
    public void A_code_in_both_areas_offers_both_in_area_order()
    {
        var scan = TwoPatchModel();
        var areas = TwoAreas(scan);
        var matches = BlockSelectionAreas.Find(scan, areas, registry: null, new[] { Rock });
        Assert.Equal(new[] { 0, 1 }, matches.Select(m => m.Area.Index).ToArray());
        Assert.Equal(2 + 2, matches.Sum(m => m.MatchChunks));   // two rock rows at home, two far
        Assert.Equal(2300, matches.Sum(m => m.MatchCells));     // 500+300+700+800
    }

    [Fact]
    public void A_row_with_two_ticked_codes_is_counted_once()
    {
        // (0,0) carries ore and rock at the same time: the selection is one pass over the rows, so it is one row,
        // and its cells from both codes add up (12 ore cells + 500 rock cells, never the same cell twice)
        var scan = TwoPatchModel();
        var areas = TwoAreas(scan);
        var both = BlockSelectionAreas.Find(scan, areas, null, new[] { Ore, Rock });
        Assert.Equal(2, both.Count);
        Assert.Equal(3, both[0].MatchChunks);            // (0,0), (1,0), (1,1) — not 4
        Assert.Equal(12 + 500 + 4 + 300, both[0].MatchCells);
        Assert.Equal(2, both[1].MatchChunks);
        Assert.Equal(1500, both[1].MatchCells);          // 700 + 800
    }

    [Fact]
    public void The_area_split_adds_up_to_the_whole_save()
    {
        // a caller states the total without an area split; the two numbers must agree, or the widget would
        // advertise more (or less) than the save holds
        var scan = TwoPatchModel();
        var areas = TwoAreas(scan);
        var perArea = BlockSelectionAreas.Find(scan, areas, null, new[] { Rock });
        var all = BlockSelectionAreas.CountAll(scan, null, new[] { Rock });
        Assert.Equal(all.Chunks, perArea.Sum(m => m.MatchChunks));
        Assert.Equal(all.Cells, perArea.Sum(m => m.MatchCells));
        Assert.Equal((4, 2300L), all);
    }

    [Fact]
    public void A_click_selects_only_the_chunks_that_hold_the_code()
    {
        // the same two-patch model: ore in the home area only, rock in both
        var scan = TwoPatchModel();
        var areas = TwoAreas(scan);
        var home = areas.AreaAt(16, 16)!;
        var far = areas.AreaAt(100 * 32 + 16, 100 * 32 + 16)!;

        var ore = BlockSelectionAreas.ChunksWithCodes(scan, registry: null, new[] { Ore });
        Assert.Equal(new[] { (0, 0), (1, 0) }, ore.OrderBy(c => c.Cx).ThenBy(c => c.Cz).ToArray());

        // a code the save does not contain, and an empty selection, select nothing at all
        Assert.Empty(BlockSelectionAreas.ChunksWithCodes(scan, null, new[] { Gold }));
        Assert.Empty(BlockSelectionAreas.ChunksWithCodes(scan, null, Array.Empty<string>()));

        // an area narrows the answer to its own rectangle: the ore is not in the far area, so it selects nothing there
        Assert.Equal(2, BlockSelectionAreas.ChunksWithCodes(scan, null, new[] { Ore }, home).Count);
        Assert.Empty(BlockSelectionAreas.ChunksWithCodes(scan, null, new[] { Ore }, far));

        // two ticked codes in one column are one column, not two passes over the save
        Assert.Equal(3, BlockSelectionAreas.ChunksWithCodes(scan, null, new[] { Ore, Rock }, home).Count);

        // and a column without the code is never in the answer, even though it lies in the same area
        Assert.DoesNotContain((100, 100), BlockSelectionAreas.ChunksWithCodes(scan, null, new[] { Ore }));
    }

    [Fact]
    public void An_empty_model_asks_nothing()
    {
        Assert.Empty(BlockSelectionAreas.Find(null, null, null, new[] { Ore }));
        var scan = TwoPatchModel();
        Assert.Empty(BlockSelectionAreas.Find(scan, null, null, new[] { Ore }));
        var areas = TwoAreas(scan);
        Assert.Empty(BlockSelectionAreas.Find(null, areas, null, new[] { Ore }));
    }
}
