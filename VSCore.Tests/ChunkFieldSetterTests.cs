using Xunit;

namespace VSCore.Tests;

/// <summary>
///     "The value that is already there is not an edit". The chunk cards and the player-data window apply every
///     box they show, so a setter that marks the field dirty regardless turns an untouched card into a rewrite
///     of the whole blob — and into a pending edit that reports changes nobody made.
/// </summary>
public class ChunkFieldSetterTests
{
    [Fact(DisplayName = "setters: a value that is already there leaves the field clean")]
    public void Setters_SameValue_NotDirty()
    {
        var i = new ChunkField { Type = ChunkFieldType.Int32, Key = "n", IntValue = 5 };
        ChunkDataEditor.SetInt(i, 5);
        Assert.False(i.Dirty);
        ChunkDataEditor.SetInt(i, 6);
        Assert.True(i.Dirty);

        var f = new ChunkField { Type = ChunkFieldType.Float, Key = "f", FloatValue = 1.5f };
        ChunkDataEditor.SetFloat(f, 1.5f);
        Assert.False(f.Dirty);

        var d = new ChunkField { Type = ChunkFieldType.Double, Key = "d", DoubleValue = 2.25 };
        ChunkDataEditor.SetDouble(d, 2.25);
        Assert.False(d.Dirty);

        var s = new ChunkField { Type = ChunkFieldType.String, Key = "s", StringValue = "axe" };
        ChunkDataEditor.SetString(s, "axe");
        Assert.False(s.Dirty);

        var b = new ChunkField { Type = ChunkFieldType.Bool, Key = "b", BoolValue = true };
        ChunkDataEditor.SetBool(b, true);
        Assert.False(b.Dirty);

        var by = new ChunkField { Type = ChunkFieldType.ByteArray, Key = "by", BytesValue = new byte[] { 1, 2 } };
        ChunkDataEditor.SetBytes(by, new byte[] { 1, 2 });
        Assert.False(by.Dirty);

        // `i` was changed above on purpose; the rest were only re-typed, and the apply path must agree
        Assert.False(ChunkDataEditor.AnyChange(new[] { f, d, s, b, by }));
    }

    [Fact(DisplayName = "setters: arrays compare element-wise before marking an edit")]
    public void Setters_Arrays_CompareContents()
    {
        var la = new ChunkField { Type = ChunkFieldType.IntArray, Key = "la", LongValues = new long[] { 1, 2, 3 } };
        ChunkDataEditor.SetLongArray(la, new long[] { 1, 2, 3 });
        Assert.False(la.Dirty);
        ChunkDataEditor.SetLongArray(la, new long[] { 1, 2 });
        Assert.True(la.Dirty);

        var da = new ChunkField { Type = ChunkFieldType.DoubleArray, Key = "da", DoubleValues = new[] { 0.5 } };
        ChunkDataEditor.SetDoubleArray(da, new[] { 0.5 });
        Assert.False(da.Dirty);

        var sa = new ChunkField { Type = ChunkFieldType.StringArray, Key = "sa", StringValues = new() { "a", null } };
        ChunkDataEditor.SetStringArray(sa, new[] { "a", null });
        Assert.False(sa.Dirty);

        var ba = new ChunkField { Type = ChunkFieldType.BoolArray, Key = "ba", BoolValues = new() { true, false } };
        ChunkDataEditor.SetBoolArray(ba, new[] { true, false });
        Assert.False(ba.Dirty);
    }

    [Fact(DisplayName = "setters: re-typing the value a field was edited to keeps it an edit")]
    public void Setters_SameValueAfterEdit_StaysDirty()
    {
        var s = new ChunkField { Type = ChunkFieldType.String, Key = "s", StringValue = "a" };
        ChunkDataEditor.SetString(s, "b");
        Assert.True(s.Dirty);
        ChunkDataEditor.SetString(s, "b"); // the second write changes nothing, but the field IS edited
        Assert.True(s.Dirty);
        Assert.True(ChunkDataEditor.AnyChange(s));
    }
}
