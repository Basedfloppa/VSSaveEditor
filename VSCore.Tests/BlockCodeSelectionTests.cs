using System;
using System.Collections.Generic;
using VSCore;
using Xunit;

namespace VSCore.Tests;

/// <summary>
///     "Select all / Deselect all" in the block list works on the searched subset only. The case that matters is the
///     negative one: a code ticked before the search was typed is not in the list, and the bulk action must not
///     reach it — otherwise the overlay the user already built would change under an action that looked local.
/// </summary>
public class BlockCodeSelectionTests
{
    private static HashSet<string> Ticked(params string[] codes) => new(codes, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Select_all_ticks_the_shown_codes_and_leaves_the_others_alone()
    {
        var ticked = Ticked("rock-granite");

        var changed = BlockCodeSelection.Apply(new[] { "rock-granite", "rock-peridotite", "soil-normal" }, ticked, select: true);

        Assert.Equal(2, changed);                       // granite was already ticked
        Assert.Equal(3, ticked.Count);
        Assert.Contains("rock-peridotite", ticked);
        Assert.Contains("soil-normal", ticked);
    }

    [Fact]
    public void Select_all_never_reaches_a_code_the_search_does_not_show()
    {
        // "torch" is typed: the list shows the torch codes, and the granite ticked before that must survive
        var ticked = Ticked("rock-granite");
        var shown = new[] { "torchholder-wood", "torchholder-metal" };

        BlockCodeSelection.Apply(shown, ticked, select: true);

        Assert.Contains("rock-granite", ticked);
        Assert.Equal(3, ticked.Count);
    }

    [Fact]
    public void Deselect_all_untick_the_shown_codes_only()
    {
        var ticked = Ticked("rock-granite", "torchholder-wood", "torchholder-metal");

        var changed = BlockCodeSelection.Apply(new[] { "torchholder-wood", "torchholder-metal" }, ticked, select: false);

        Assert.Equal(2, changed);
        Assert.Equal(new[] { "rock-granite" }, ticked);  // the code outside the search is still ticked
    }

    [Fact]
    public void Deselect_all_does_not_touch_a_code_that_is_not_shown_even_if_it_looks_similar()
    {
        var ticked = Ticked("torchholder-wood", "torchholdersmithingplus-wood");

        BlockCodeSelection.Apply(new[] { "torchholder-wood" }, ticked, select: false);

        Assert.Equal(new[] { "torchholdersmithingplus-wood" }, ticked);
    }

    [Fact]
    public void A_second_click_changes_nothing_and_says_so()
    {
        var ticked = Ticked();
        var shown = new[] { "rock-granite", "rock-peridotite" };

        Assert.Equal(2, BlockCodeSelection.Apply(shown, ticked, select: true));
        Assert.Equal(0, BlockCodeSelection.Apply(shown, ticked, select: true));
    }

    [Fact]
    public void The_set_decides_about_case_not_the_helper()
    {
        // the app's set compares case-insensitively, so the same code in another case is the same code
        var ticked = Ticked("Rock-Granite");

        Assert.Equal(0, BlockCodeSelection.Apply(new[] { "rock-granite" }, ticked, select: true));
        Assert.Single(ticked);
    }

    [Fact]
    public void Counting_ticked_codes_covers_the_shown_ones_only()
    {
        var ticked = Ticked("rock-granite", "gold");

        Assert.Equal(1, BlockCodeSelection.CountTicked(new[] { "rock-granite", "rock-peridotite" }, ticked));
        Assert.Equal(0, BlockCodeSelection.CountTicked(Array.Empty<string>(), ticked));
        Assert.Equal(0, BlockCodeSelection.CountTicked(null, ticked));
        Assert.Equal(0, BlockCodeSelection.CountTicked(new[] { "rock-granite" }, null));
    }
}
