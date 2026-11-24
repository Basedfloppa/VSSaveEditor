using System.Text;
using VSCore.Format;
using Xunit;

namespace VSCore.Tests;

/// <summary>
///     Generic protobuf tree of a mod's saved value: the editor shows it by field numbers (there is no schema),
///     so reading, editing and cutting must be lossless — a value nobody touched has to go back byte for byte.
/// </summary>
public class PbTreeTests
{
    private const string UidA = "zCoQ0h2n1QqKA6d5zrbsbnVk";
    private const string UidB = "p+BcZDwmc9EddZ3BJBMhKCot";

    private static byte[] Varint(long v)
    {
        var ms = new MemoryStream();
        var u = (ulong)v;
        while (u >= 0x80)
        {
            ms.WriteByte((byte)((u & 0x7F) | 0x80));
            u >>= 7;
        }

        ms.WriteByte((byte)u);
        return ms.ToArray();
    }

    private static byte[] Cat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p, 0, p.Length);
        return ms.ToArray();
    }

    private static byte[] LenField(int field, byte[] payload)
        => Cat(Varint(((long)field << 3) | 2), Varint(payload.Length), payload);

    private static byte[] VarintField(int field, long value) => Cat(Varint((long)field << 3), Varint(value));

    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>The shape of `divineascension_playerprogressiondata_&lt;uid&gt;`: a message holding the uid and a few
    /// nested messages (field 100 = uid + counters, field 101 = pairs).</summary>
    private static byte[] Progression(string uid)
        => Cat(
            LenField(100, Cat(LenField(1, Text(uid)), VarintField(2, 5), VarintField(3, 29))),
            LenField(101, Cat(VarintField(1, 7), VarintField(2, 130))));

    [Fact(DisplayName = "pb tree: an untouched value serialises back byte for byte")]
    public void RoundTrip_IsLossless()
    {
        var value = Progression(UidA);
        var nodes = PbTree.Parse(value);
        Assert.Equal(value, PbTree.Serialize(nodes));
        Assert.False(PbTree.AnyChanged(nodes));
    }

    [Fact(DisplayName = "pb tree: a nested message is a subtree, a uid inside it stays readable text")]
    public void Parse_SeparatesMessagesAndText()
    {
        var nodes = PbTree.Parse(Progression(UidA));
        Assert.Equal(2, nodes.Count);
        Assert.All(nodes, n => Assert.True(n.IsMessage));
        var uidNode = nodes[0].Children![0];
        Assert.False(uidNode.IsMessage);
        Assert.Equal(UidA, uidNode.Text);
        Assert.Equal(UidA, nodes[0].Children![0].ValueText);
    }

    [Fact(DisplayName = "pb tree: editing a number and a text keeps every other byte identical")]
    public void Edit_ChangesOnlyTheTouchedValue()
    {
        var nodes = PbTree.Parse(Progression(UidA));
        var counter = nodes[0].Children![1];
        PbTree.SetInt(counter, 29);
        var bytes = PbTree.Serialize(nodes);
        var again = PbTree.Parse(bytes);
        Assert.Equal(29, again[0].Children![1].IntValue);
        // the second top-level entry was not touched: its bytes are the tail of the original value
        Assert.Equal(nodes[1].Payload, again[1].Payload);
        Assert.True(PbTree.AnyChanged(nodes));
    }

    [Fact(DisplayName = "pb tree: deleting a node cuts the whole entry, siblings survive")]
    public void Remove_CutsWholeEntry()
    {
        var nodes = PbTree.Parse(Progression(UidA));
        nodes[1].Removed = true;
        var rebuilt = PbTree.Serialize(nodes);
        var again = PbTree.Parse(rebuilt);
        Assert.Single(again);
        Assert.Equal(100, again[0].Field);
    }

    [Fact(DisplayName = "pb tree: a dictionary entry naming the player is found and cut whole")]
    public void RemoveEntriesMentioning_FindsDictionaryEntries()
    {
        // `loreDiscoveriesByPlayerUid`: one message per player, the uid is the first field of the entry
        var value = Cat(
            LenField(1, Cat(LenField(1, Text(UidA)), LenField(2, new byte[] { 1, 2, 3 }))),
            LenField(1, Cat(LenField(1, Text(UidB)), LenField(2, new byte[] { 4, 5, 6 }))));
        var nodes = PbTree.Parse(value);
        Assert.Equal(1, PbTree.CountEntriesMentioning(nodes, UidA));
        Assert.Equal(1, PbTree.RemoveEntriesMentioning(nodes, UidA));
        var rebuilt = PbTree.Serialize(nodes);
        Assert.DoesNotContain(UidA, Encoding.UTF8.GetString(rebuilt));
        Assert.Contains(UidB, Encoding.UTF8.GetString(rebuilt));
        Assert.Equal(1, PbTree.Parse(rebuilt).Count);
    }

    [Fact(DisplayName = "pb tree: a payload that is plain text is not mistaken for a message")]
    public void Parse_DoesNotTreatTextAsMessage()
    {
        var nodes = PbTree.Parse(LenField(1, Text(UidA)));
        Assert.Single(nodes);
        Assert.False(nodes[0].IsMessage);
        Assert.Equal(UidA, nodes[0].Text);
        Assert.Equal(UidA, PbTree.Serialize(nodes) is var b ? PbNode.AsText(PbTree.Parse(b)[0].Payload) : null);
    }

    [Fact(DisplayName = "pb tree: a broken value is kept as one opaque node instead of throwing")]
    public void Parse_BrokenValueSurvives()
    {
        var broken = new byte[] { 0x0A, 0xFF, 0xFF, 0x01, 0x02 };
        var nodes = PbTree.Parse(broken);
        Assert.Single(nodes);
        Assert.Equal(broken, nodes[0].Payload);
        Assert.Equal(broken, PbTree.Serialize(nodes));
    }

    [Fact(DisplayName = "proto: what the writer emits is what the reader reads (varint, text, long payload)")]
    public void Writer_And_Reader_Agree()
    {
        var ms = new MemoryStream();
        PbWriter.Int(ms, 3, 300);
        PbWriter.Message(ms, 2, Encoding.UTF8.GetBytes("uid"));
        PbWriter.Message(ms, 200, new byte[300]); // length over 127: the length varint has to be multi-byte
        var blob = ms.ToArray();

        var fields = PbReader.ReadMessage(blob, 1);
        Assert.Equal(new[] { 3, 2, 200 }, fields.Select(f => f.Field));
        Assert.Equal(300, fields[0].IntValue);
        Assert.Equal("uid", Encoding.UTF8.GetString(fields[1].Bytes!));
        Assert.Equal(300, fields[2].Bytes!.Length);

        // the tree parser is a second reader: it has to see the same fields in the same order
        Assert.Equal(new[] { 3, 2, 200 }, PbTree.Parse(blob).Select(n => n.Field));
        Assert.Equal(blob, PbTree.Serialize(PbTree.Parse(blob)));
    }
}
