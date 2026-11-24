using VSCore.Format;
using Xunit;

namespace VSCore.Tests;

/// <summary>
///     The one splice every editor of a shared blob goes through: it pads, cuts and replaces at the byte level.
///     The rules it has to keep are the ones that used to be written out four times separately — a single pass,
///     an overlapping range skipped instead of applied twice, everything outside the ranges copied as it was.
/// </summary>
public class ByteSpliceTests
{
    private static byte[] Bytes(params int[] values) => values.Select(v => (byte)v).ToArray();

    [Fact(DisplayName = "splice: no edits returns the same array, not a copy")]
    public void Apply_NoEdits_SameArray()
    {
        var blob = Bytes(1, 2, 3);
        Assert.Same(blob, ByteSplice.Apply(blob, Array.Empty<ByteSplice.Edit>()));
    }

    [Fact(DisplayName = "splice: a cut, a replacement and an insertion land in one pass")]
    public void Apply_Mixed_OnePass()
    {
        var blob = Bytes(1, 2, 3, 4, 5, 6);
        var result = ByteSplice.Apply(blob, new[]
        {
            ByteSplice.Cut(1, 3), // drop 2,3
            ByteSplice.Replace(4, 5, Bytes(9, 9)), // 5 -> 9,9
            new ByteSplice.Edit(6, 6, Bytes(7)) // append
        });
        Assert.Equal(Bytes(1, 4, 9, 9, 6, 7), result);
    }

    [Fact(DisplayName = "splice: edits given out of order are applied in offset order")]
    public void Apply_Unsorted_IsOrdered()
    {
        var blob = Bytes(1, 2, 3, 4);
        var result = ByteSplice.Apply(blob, new[]
        {
            ByteSplice.Cut(2, 4),
            ByteSplice.Cut(0, 1)
        });
        Assert.Equal(Bytes(2), result);
    }

    [Fact(DisplayName = "splice: a range starting inside the previous one is skipped, not applied twice")]
    public void Apply_Overlapping_SkipsSecond()
    {
        var blob = Bytes(1, 2, 3, 4, 5);
        var result = ByteSplice.Apply(blob, new[]
        {
            ByteSplice.Cut(0, 3),
            ByteSplice.Cut(1, 2) // inside the first one: those bytes are already gone
        });
        Assert.Equal(Bytes(4, 5), result);
    }

    [Fact(DisplayName = "splice: a range outside the blob is ignored instead of throwing")]
    public void Apply_OutOfRange_Ignored()
    {
        var blob = Bytes(1, 2, 3);
        Assert.Same(blob, ByteSplice.Apply(blob, new[] { ByteSplice.Cut(1, 99) }));
        Assert.Same(blob, ByteSplice.Apply(blob, new[] { ByteSplice.Cut(-1, 2) }));
    }
}
