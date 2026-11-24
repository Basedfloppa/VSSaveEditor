using System.Text;
using VSCore.Format;

namespace VSCore.Tests;

/// <summary>
///     Byte builders shared by the tests that construct ECX/TreeAttribute payloads and protobuf blobs by hand.
///     Each test file used to carry its own private copy of <c>Varint</c>/<c>Cat</c>/<c>LenField</c>; a second
///     copy of the length encoding is the shortest path to a fixture that no longer matches what the parser reads.
/// </summary>
internal static class TestBytes
{
    public static byte[] Varint(long value)
    {
        var ms = new MemoryStream();
        PbWriter.Varint(ms, value);
        return ms.ToArray();
    }

    public static byte[] Cat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p, 0, p.Length);
        return ms.ToArray();
    }

    /// <summary>7-bit length + UTF-8 — how ECX and BinaryWriter store strings.</summary>
    public static byte[] Text(string s) => Cat(Varint(Encoding.UTF8.GetByteCount(s)), Encoding.UTF8.GetBytes(s));

    public static byte[] I32(int v)
    {
        var b = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b, v);
        return b;
    }

    public static byte[] F32(float v)
    {
        var b = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(b, v);
        return b;
    }

    public static byte[] LenField(int field, byte[] payload)
        => Cat(Varint(((long)field << 3) | 2), Varint(payload.Length), payload);

    public static byte[] VarintField(int field, long value) => Cat(Varint((long)field << 3), Varint(value));

    // ------------------------- ECX fields -------------------------

    /// <summary>gamedata with registry sections: the marker, then field 2/wire 2 holding a message per entry
    /// (field 1 = id, field 2 = code) — the frame <see cref="ItemRegistry.FromGamedata" /> reads.</summary>
    public static byte[] Gamedata(params (string Marker, (int Id, string Code)[] Entries)[] sections)
    {
        var parts = new List<byte[]>();
        foreach (var (marker, entries) in sections)
        {
            var body = entries
                .Select(e => LenField(2, Cat(VarintField(1, e.Id), LenField(2, Encoding.UTF8.GetBytes(e.Code)))))
                .SelectMany(b => b).ToArray();
            parts.Add(Cat(Text(marker), new byte[] { 0x12 }, Varint(body.Length), body));
        }

        return Cat(parts.ToArray());
    }

    public static byte[] EcxInt(string key, int value) => Cat(new[] { (byte)1 }, Text(key), I32(value));

    public static byte[] EcxFloat(string key, float value) => Cat(new[] { (byte)4 }, Text(key), F32(value));

    public static byte[] EcxString(string key, string value) => Cat(new[] { (byte)5 }, Text(key), Text(value));

    /// <summary>A nested tree: type 6, the key, the children, then the 0 terminator.</summary>
    public static byte[] EcxTree(string key, params byte[][] children) => Cat(new[] { (byte)6 }, Text(key), Cat(children),
        new byte[] { 0 });

    /// <summary>An ItemStack field: type 7, the "not null" byte, class/id/size, the stack attributes, terminator.</summary>
    public static byte[] EcxStack(string key, int itemClass, int itemId, int size, params byte[][] attributes)
        => Cat(new[] { (byte)7 }, Text(key), new byte[] { 0 }, I32(itemClass), I32(itemId), I32(size), Cat(attributes),
            new byte[] { 0 });
}
