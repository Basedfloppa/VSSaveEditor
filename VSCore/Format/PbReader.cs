namespace VSCore.Format;

/// <summary>Description of one field of the FastSerializer format (the protobuf variant of VS).</summary>
public sealed class PbField
{
    public int Field { get; init; }
    public int WireType { get; init; }       // 0=varint, 2=len-delim, 5=fixed32
    public long IntValue { get; init; }      // for varint
    public int Length { get; init; }         // for len-delim
    public byte[]? Bytes { get; init; }      // raw len-delim bytes
    public List<PbField>? Children { get; set; } // nested message (when parsing len-delim)
    public float FloatValue { get; init; }

    public override string ToString() => WireType switch
    {
        0 => $"f{Field}:varint={IntValue}",
        2 => $"f{Field}:len={Length}",
        5 => $"f{Field}:f32={FloatValue}",
        _ => $"f{Field}:wt{WireType}"
    };
}

/// <summary>
/// Reader of the VS FastSerializer wire format: LEB128 varint, tag = field<<3 | wireType.
/// Source: the VS API Util/FastSerializer (fields<0x10 -> 1-byte tag), lengths are LEB128.
/// </summary>
public static class PbReader
{
    public static (long value, int next) ReadVarint(byte[] b, int i)
    {
        long result = 0; int shift = 0;
        // More than 10 bytes is already garbage: in C# the shift is masked modulo 64 and would cycle the bits.
        while (i < b.Length && shift < 64)
        {
            byte x = b[i++];
            result |= (long)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) break;
            shift += 7;
        }
        return (result, i);
    }

    /// <summary>Whether a len-delim length from an UNTRUSTED blob fits: non-negative, fits into int
    /// (otherwise `(int)len` yields garbage) and fits into the rest of the buffer. The comparison is against
    /// bufferLength - offset, not offset + len: the latter overflows at len ≈ long.Max and lets the check pass.</summary>
    public static bool LengthFits(long len, int offset, int bufferLength)
        => len >= 0 && len <= int.MaxValue && offset >= 0 && offset <= bufferLength && len <= bufferLength - offset;

    /// <summary>Parses a message; recursion into len-delim is bounded by maxDepth.</summary>
    public static List<PbField> ReadMessage(byte[] b, int maxDepth = 2, int maxFields = 4096)
    {
        var list = new List<PbField>();
        int i = 0;
        while (i < b.Length && list.Count < maxFields)
        {
            var (tag, ni) = ReadVarint(b, i); i = ni;
            int field = (int)(tag >> 3);
            int wt = (int)(tag & 7);
            switch (wt)
            {
                case 0:
                    var (v, vi) = ReadVarint(b, i); i = vi;
                    list.Add(new PbField { Field = field, WireType = 0, IntValue = v });
                    break;
                case 2:
                    var (ln, li) = ReadVarint(b, i); i = li;
                    if (!LengthFits(ln, i, b.Length)) return list;
                    int length = (int)ln;
                    var seg = new byte[length];
                    Array.Copy(b, i, seg, 0, length); i += length;
                    var f = new PbField { Field = field, WireType = 2, Length = length, Bytes = seg };
                    if (maxDepth > 0) { try { f.Children = ReadMessage(seg, maxDepth - 1, Math.Min(64, maxFields)); } catch { f.Children = null; } }
                    list.Add(f);
                    break;
                case 5:
                    if (i + 4 > b.Length) return list;
                    float fv = BitConverter.ToSingle(b, i); i += 4;
                    list.Add(new PbField { Field = field, WireType = 5, FloatValue = fv });
                    break;
                case 1:
                    if (i + 8 > b.Length) return list; i += 8;
                    list.Add(new PbField { Field = field, WireType = 1 });
                    break;
                default:
                    return list; // unknown wiretype -> stop
            }
        }
        return list;
    }
}
