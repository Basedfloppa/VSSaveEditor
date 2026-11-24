using System.Text;

namespace VSCore.Format;

/// <summary>
///     The only writer of the protobuf/FastSerializer primitives the editors emit. There used to be five copies
///     of the varint loop (PbTree, ModData, PlayerDataParser, ChunkDataEditor and its string writer) while every
///     reader is a single one (<see cref="PbReader" />). A copy is the shortest path to a blob that still parses
///     but no longer means the same thing: fix one writer and the others keep emitting the old framing.
/// </summary>
public static class PbWriter
{
    /// <summary>Base-128 varint, the encoding <see cref="PbReader.ReadVarint" /> reads back.</summary>
    public static void Varint(Stream s, long value)
    {
        ulong u = (ulong)value;
        while (u >= 0x80)
        {
            s.WriteByte((byte)((u & 0x7F) | 0x80));
            u >>= 7;
        }

        s.WriteByte((byte)u);
    }

    /// <summary>A length-delimited field (wire type 2): tag + length + payload.</summary>
    public static void Message(Stream s, int field, ReadOnlySpan<byte> payload)
    {
        Varint(s, ((long)field << 3) | 2);
        Varint(s, payload.Length);
        s.Write(payload);
    }

    /// <summary>A varint field (wire type 0): tag + value.</summary>
    public static void Int(Stream s, int field, long value)
    {
        Varint(s, (long)field << 3);
        Varint(s, value);
    }

    /// <summary>The bytes of one length-delimited field — the unit every splice replaces.</summary>
    public static byte[] MessageBytes(int field, ReadOnlySpan<byte> payload)
    {
        var ms = new MemoryStream(payload.Length + 8);
        Message(ms, field, payload);
        return ms.ToArray();
    }

    /// <summary>The bytes of one length-delimited field holding UTF-8 text.</summary>
    public static byte[] TextBytes(int field, string text) => MessageBytes(field, Encoding.UTF8.GetBytes(text));
}
