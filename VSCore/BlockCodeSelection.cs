using System;
using System.Collections.Generic;

namespace VSCore;

/// <summary>
///     Bulk selection of block codes in the "Find blocks" list.
///     The list shows a search-filtered subset, so "select all" and "deselect all" must touch exactly the codes the
///     filter offers. A code ticked before the search was typed is no longer in the list, and a bulk action that
///     reached it would silently change the overlay the user built — the very thing the list exists to control.
/// </summary>
public static class BlockCodeSelection
{
    /// <summary>
    ///     Ticks (or unticks) every code of <paramref name="shown"/> and returns how many codes actually changed.
    ///     Codes outside <paramref name="shown"/> are left untouched in both directions.
    /// </summary>
    public static int Apply(IEnumerable<string>? shown, ISet<string>? ticked, bool select)
    {
        if (shown is null || ticked is null) return 0;
        var changed = 0;
        foreach (var code in shown)
        {
            if (string.IsNullOrEmpty(code)) continue;
            if (select)
            {
                if (ticked.Add(code)) changed++;
            }
            else if (ticked.Remove(code))
            {
                changed++;
            }
        }

        return changed;
    }

    /// <summary>How many of the shown codes are ticked — what the two buttons are enabled by.</summary>
    public static int CountTicked(IEnumerable<string>? shown, ISet<string>? ticked)
    {
        if (shown is null || ticked is null) return 0;
        var count = 0;
        foreach (var code in shown)
            if (!string.IsNullOrEmpty(code) && ticked.Contains(code))
                count++;
        return count;
    }
}
