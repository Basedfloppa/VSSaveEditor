using System.Buffers.Binary;
using System.Text;
using VSCore.Format;

namespace VSCore;

/// <summary>
/// Re-serialisation and editing of chunk data:
///  - setters for ECX/TreeAttribute field values (including nested trees, bool, byte[] and arrays);
///  - serialisation of an Entity/BlockEntity back into a blob (header + root fields, nesting preserved);
///  - splicing the modified payloads (field6/field8 and others) back into the top-level chunk blob
///    (tags/lengths are recomputed, untouched bytes are kept as-is — lossless).
/// Blocks (field1/16) and their placement are NOT edited here — see <see cref="ChunkBlockRewriter" />.
/// </summary>
public static class ChunkDataEditor
{
    // ------------------------- Value setters -------------------------
    // Every setter leaves the field untouched when the value is already there: the cards apply ALL the boxes
    // they show, so marking them dirty regardless would rewrite (and report as changed) blobs nobody edited.

    public static void SetInt(ChunkField f, long v)
    {
        if (f.Type is not (ChunkFieldType.Int32 or ChunkFieldType.Int64) || f.IntValue == v) return;
        f.IntValue = v;
        f.Dirty = true;
        f.Raw = Rebuild(f);
    }

    public static void SetFloat(ChunkField f, float v)
    {
        if (f.Type != ChunkFieldType.Float || f.FloatValue.Equals(v)) return;
        f.FloatValue = v;
        f.Dirty = true;
        f.Raw = Rebuild(f);
    }

    public static void SetDouble(ChunkField f, double v)
    {
        if (f.Type != ChunkFieldType.Double || f.DoubleValue.Equals(v)) return;
        f.DoubleValue = v;
        f.Dirty = true;
        f.Raw = Rebuild(f);
    }

    public static void SetString(ChunkField f, string v)
    {
        if (f.Type != ChunkFieldType.String || string.Equals(f.StringValue, v, StringComparison.Ordinal)) return;
        f.StringValue = v;
        f.Dirty = true;
        f.Raw = Rebuild(f);
    }

    public static void SetBool(ChunkField f, bool v)
    {
        if (f.Type != ChunkFieldType.Bool || f.BoolValue == v) return;
        f.BoolValue = v;
        f.Dirty = true;
        f.Raw = Rebuild(f);
    }

    public static void SetBytes(ChunkField f, byte[] v)
    {
        if (f.Type != ChunkFieldType.ByteArray) return;
        if (v.Length > ushort.MaxValue) throw new ArgumentException("byte array is too long for a save field");
        if (f.BytesValue.AsSpan().SequenceEqual(v)) return;
        f.BytesValue = v;
        f.Dirty = true;
        f.Raw = Rebuild(f);
    }

    public static void SetStringArray(ChunkField f, IEnumerable<string?> values)
    {
        if (f.Type != ChunkFieldType.StringArray) return;
        var list = values.ToList();
        if (f.StringValues.SequenceEqual(list)) return;
        f.StringValues = list;
        f.Dirty = true;
        f.Raw = Rebuild(f);
    }

    public static void SetLongArray(ChunkField f, IEnumerable<long> values)
    {
        if (f.Type is not (ChunkFieldType.IntArray or ChunkFieldType.LongArray)) return;
        var list = values.ToArray();
        if (f.LongValues.SequenceEqual(list)) return;
        f.LongValues = list;
        f.Dirty = true;
        f.Raw = Rebuild(f);
    }

    public static void SetDoubleArray(ChunkField f, IEnumerable<double> values)
    {
        if (f.Type is not (ChunkFieldType.FloatArray or ChunkFieldType.DoubleArray)) return;
        var list = values.ToArray();
        if (f.DoubleValues.SequenceEqual(list)) return;
        f.DoubleValues = list;
        f.Dirty = true;
        f.Raw = Rebuild(f);
    }

    public static void SetBoolArray(ChunkField f, IEnumerable<bool> values)
    {
        if (f.Type != ChunkFieldType.BoolArray) return;
        var list = values.ToList();
        if (f.BoolValues.SequenceEqual(list)) return;
        f.BoolValues = list;
        f.Dirty = true;
        f.Raw = Rebuild(f);
    }

    /// <summary>Rebuilds the exact bytes of a [type][LP-key][value] field from the current values.
    /// Fields marked <see cref="ChunkField.Removed" /> below this one are cut out of the result.</summary>
    public static byte[] Rebuild(ChunkField f)
    {
        if (!f.Dirty && f.Raw.Length > 0 && !AnyChange(f)) return f.Raw;
        var ms = new MemoryStream();
        ms.WriteByte((byte)f.Type);
        WriteString(ms, f.Key);
        switch (f.Type)
        {
            case ChunkFieldType.Int32:
                WriteI32(ms, (int)f.IntValue);
                break;
            case ChunkFieldType.Int64:
                WriteI64(ms, f.IntValue);
                break;
            case ChunkFieldType.Double:
                WriteF64(ms, f.DoubleValue);
                break;
            case ChunkFieldType.Float:
                WriteF32(ms, f.FloatValue);
                break;
            case ChunkFieldType.String:
                WriteString(ms, f.StringValue ?? "");
                break;
            case ChunkFieldType.Tree:
                if (f.IsRawTree || (!f.HasTerminator && f.Children.Count == 0)) return f.Raw;
                foreach (var child in f.Children)
                {
                    if (child.Removed) continue;
                    ms.Write(Rebuild(child));
                }

                ms.WriteByte(0);
                break;
            case ChunkFieldType.ItemStack:
                ms.WriteByte((byte)(f.ItemStackIsNull ? 1 : 0));
                if (!f.ItemStackIsNull)
                {
                    WriteI32(ms, f.ItemClass);
                    WriteI32(ms, f.ItemId);
                    WriteI32(ms, f.StackSize);
                    if (!f.HasTerminator && f.Children.Count == 0) return f.Raw;
                    foreach (var child in f.Children)
                    {
                        if (child.Removed) continue;
                        ms.Write(Rebuild(child));
                    }

                    ms.WriteByte(0);
                }
                break;
            case ChunkFieldType.ByteArray:
                if (f.BytesValue.Length > ushort.MaxValue)
                    throw new InvalidOperationException("byte array is too long for a save field");
                ms.WriteByte((byte)(f.BytesValue.Length & 0xFF));
                ms.WriteByte((byte)((f.BytesValue.Length >> 8) & 0xFF));
                ms.Write(f.BytesValue);
                break;
            case ChunkFieldType.Bool:
                ms.WriteByte((byte)(f.BoolValue ? 1 : 0));
                break;
            case ChunkFieldType.StringArray:
                WriteI32(ms, f.StringValues.Count);
                foreach (var s in f.StringValues) WriteString(ms, s ?? "");
                break;
            case ChunkFieldType.IntArray:
                WriteI32(ms, f.LongValues.Length);
                foreach (var v in f.LongValues) WriteI32(ms, (int)v);
                break;
            case ChunkFieldType.LongArray:
                WriteI32(ms, f.LongValues.Length);
                foreach (var v in f.LongValues) WriteI64(ms, v);
                break;
            case ChunkFieldType.FloatArray:
                WriteI32(ms, f.DoubleValues.Length);
                foreach (var v in f.DoubleValues) WriteF32(ms, (float)v);
                break;
            case ChunkFieldType.DoubleArray:
                WriteI32(ms, f.DoubleValues.Length);
                foreach (var v in f.DoubleValues) WriteF64(ms, v);
                break;
            case ChunkFieldType.BoolArray:
                WriteI32(ms, f.BoolValues.Count);
                foreach (var v in f.BoolValues) ms.WriteByte((byte)(v ? 1 : 0));
                break;
            case ChunkFieldType.TreeArray:
                WriteI32(ms, f.TreeValues.Count);
                foreach (var tree in f.TreeValues)
                {
                    foreach (var child in tree) ms.Write(Rebuild(child));
                    ms.WriteByte(0);
                }
                break;
            default:
                // Opaque — the original field bytes, unchanged.
                return f.Raw;
        }
        return ms.ToArray();
    }

    /// <summary>Whether this field or anything below it was edited or removed — the check that decides
    /// if a player blob (or one inventory body) has to be rewritten at all.</summary>
    public static bool AnyChange(ChunkField f)
    {
        if (f.Dirty || f.Removed) return true;
        foreach (var child in f.Children)
            if (AnyChange(child)) return true;
        return false;
    }

    public static bool AnyChange(IEnumerable<ChunkField> fields)
    {
        foreach (var f in fields)
            if (AnyChange(f)) return true;
        return false;
    }

    private static void WriteI32(Stream ms, int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        ms.Write(b);
    }

    private static void WriteI64(Stream ms, long v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, v);
        ms.Write(b);
    }

    private static void WriteF32(Stream ms, float v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, v);
        ms.Write(b);
    }

    private static void WriteF64(Stream ms, double v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(b, v);
        ms.Write(b);
    }

    /// <summary>BinaryWriter.Write(string): 7-bit length + UTF-8. The earlier single-byte variant
    /// broke on strings longer than 127 bytes.</summary>
    private static void WriteString(Stream ms, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        PbWriter.Varint(ms, bytes.Length);
        ms.Write(bytes);
    }

    // ------------------------- Serialisation of ECX blobs -------------------------

    /// <summary>Builds an entity blob: header + root fields (nesting is preserved).</summary>
    public static byte[] SerializeEntity(EntityRecord e)
        => Serialize(e.HeaderBytes, e.RootFields);

    /// <summary>Builds a block entity blob: header + root fields.</summary>
    public static byte[] SerializeBlockEntity(BlockEntityRecord b)
        => Serialize(b.HeaderBytes, b.RootFields);

    /// <summary>Concatenation of fields (no header) — for headerless ECX bodies (player inventories).</summary>
    public static byte[] SerializeFields(IEnumerable<ChunkField> fields)
        => Serialize(Array.Empty<byte>(), fields);

    private static byte[] Serialize(byte[] header, IEnumerable<ChunkField> fields)
    {
        var list = fields.Where(f => !f.Removed).ToList();
        var ms = new MemoryStream(header.Length + list.Sum(f => Rebuild(f).Length));
        ms.Write(header, 0, header.Length);
        foreach (var f in list) ms.Write(Rebuild(f));
        return ms.ToArray();
    }

    // ------------------------- Top-level walk over the chunk blob -------------------------

    public sealed class ChunkTopField
    {
        public int Field;
        public int WireType;
        public int Start;
        public int PayloadStart;
        public int PayloadEnd;
        public int Occurrence;
    }

    /// <summary>Walks the top-level fields of the chunk blob, recording byte ranges.</summary>
    public static List<ChunkTopField> WalkTop(byte[] blob)
    {
        var res = new List<ChunkTopField>();
        var occ = new Dictionary<int, int>();
        int i = 0;
        while (i < blob.Length)
        {
            int start = i;
            var (tag, ni) = PbReader.ReadVarint(blob, i);
            if (ni <= i) return res;
            i = ni;
            int field = (int)(tag >> 3);
            int wt = (int)(tag & 7);
            occ.TryGetValue(field, out int o);
            occ[field] = o + 1;
            switch (wt)
            {
                case 0:
                    var (_, vi) = PbReader.ReadVarint(blob, i);
                    if (vi <= i) return res;
                    res.Add(new ChunkTopField
                        { Field = field, WireType = wt, Start = start, PayloadStart = i, PayloadEnd = vi, Occurrence = o });
                    i = vi;
                    break;
                case 2:
                    var (ln, li) = PbReader.ReadVarint(blob, i);
                    if (li < i || !PbReader.LengthFits(ln, li, blob.Length)) return res;
                    res.Add(new ChunkTopField
                    {
                        Field = field, WireType = wt, Start = start, PayloadStart = li,
                        PayloadEnd = li + (int)ln, Occurrence = o
                    });
                    i = li + (int)ln;
                    break;
                case 5:
                    if (i + 4 > blob.Length) return res;
                    res.Add(new ChunkTopField
                        { Field = field, WireType = wt, Start = start, PayloadStart = i, PayloadEnd = i + 4, Occurrence = o });
                    i += 4;
                    break;
                case 1:
                    if (i + 8 > blob.Length) return res;
                    res.Add(new ChunkTopField
                        { Field = field, WireType = wt, Start = start, PayloadStart = i, PayloadEnd = i + 8, Occurrence = o });
                    i += 8;
                    break;
                default:
                    return res;
            }
        }
        return res;
    }

    /// <summary>
    /// Replaces the payloads of the given top-level fields (field6/field8 and others) with new ones,
    /// recomputing the lengths; untouched fields are preserved byte for byte.
    /// </summary>
    public static byte[] SplicePayloads(byte[] blob, IReadOnlyList<(int Field, int Occurrence, byte[] Payload)> edits)
    {
        if (edits.Count == 0) return blob;
        var byKey = new Dictionary<(int, int), ChunkTopField>();
        foreach (var t in WalkTop(blob)) byKey[(t.Field, t.Occurrence)] = t;

        var ranges = new List<ByteSplice.Edit>();
        foreach (var (fld, occ, payload) in edits)
        {
            if (!byKey.TryGetValue((fld, occ), out var t)) continue;
            ranges.Add(ByteSplice.Replace(t.Start, t.PayloadEnd, PbWriter.MessageBytes(t.Field, payload)));
        }

        return ByteSplice.Apply(blob, ranges);
    }
}
