using System.Buffers.Binary;
using System.Text;
using VSCore.Format;

namespace VSCore;

// ============================= STRUCTURED CHUNK DATA MODEL =============================

/// <summary>
/// Type of a field value in an ECX/TreeAttribute blob. The numbers match
/// <c>TreeAttribute.AttributeIdMapping</c> from VintagestoryAPI: 1..6 — scalars and tree,
/// 7 — ItemStack, 8 — byte[], 9 — bool, 10..16 — arrays.
/// </summary>
public enum ChunkFieldType : byte
{
    Opaque = 0,
    Int32 = 1,
    Int64 = 2,
    Double = 3,
    Float = 4,
    String = 5,
    Tree = 6,
    ItemStack = 7,
    ByteArray = 8,
    Bool = 9,
    StringArray = 10,
    IntArray = 11,
    FloatArray = 12,
    DoubleArray = 13,
    TreeArray = 14,
    LongArray = 15,
    BoolArray = 16
}

/// <summary>
/// One field of an ECX blob: [type-byte][LP key][value]. For a tree the value is nested fields,
/// for arrays typed values, for an ItemStack the class/id/size and the stack attributes.
/// <see cref="Raw" /> — the exact field bytes (type+key+value), so that unchanged fields can be
/// saved byte for byte instead of rebuilt.
/// </summary>
public sealed class ChunkField
{
    public ChunkFieldType Type;
    public string Key = "";

    /// <summary>Full path from the root ("health.currenthealth"); filled in by <see cref="ChunkDataParser.Flatten" />.
    /// Empty for a top-level field — then <see cref="Key" /> serves as the label.</summary>
    public string Path = "";

    public long IntValue;
    public float FloatValue;
    public double DoubleValue;
    public string? StringValue;
    public bool BoolValue;
    public byte[] BytesValue = Array.Empty<byte>();
    public long[] LongValues = Array.Empty<long>();
    public double[] DoubleValues = Array.Empty<double>();
    public List<string?> StringValues = new();
    public List<bool> BoolValues = new();
    public List<ChunkField> Children = new();
    public List<List<ChunkField>> TreeValues = new();

    /// <summary>
    /// For Tree/ItemStack: whether the 0 terminator was found. If the structure could not be parsed (a mod writes
    /// a "streamed tree" as raw bytes), the field stays a raw value without a terminator and without children —
    /// its bytes are kept as-is, and parsing continues from the next byte.
    /// </summary>
    public bool HasTerminator = true;

    /// <summary>The tree was parsed as raw bytes (a mod's streamed tree) rather than as fields.
    /// Such a field is not edited row by row, but its bytes are preserved byte for byte.</summary>
    public bool IsRawTree;

    // ItemStack (type 7): the null flag + class/id/stacksize + the tree of stack attributes.
    public bool ItemStackIsNull;
    public int ItemClass;
    public int ItemId;
    public int StackSize;

    /// <summary>Exact field bytes — for Opaque and for unchanged tree fields.</summary>
    public byte[] Raw = Array.Empty<byte>();

    /// <summary>The field was changed through a setter. While false, the serialiser emits <see cref="Raw" /> byte for byte:
    /// that way even values the parser only understood approximately are preserved (mod raw trees).</summary>
    public bool Dirty;

    /// <summary>Label in lists and cards: the full path if the field is nested.</summary>
    public string DisplayPath => Path.Length > 0 ? Path : Key;

    /// <summary>Whether the value can be edited as a string. Tree and ItemStack are edited through their leaves.</summary>
    public bool IsEditable => Type is ChunkFieldType.Int32 or ChunkFieldType.Int64 or ChunkFieldType.Float
        or ChunkFieldType.Double or ChunkFieldType.String or ChunkFieldType.Bool
        or ChunkFieldType.ByteArray or ChunkFieldType.StringArray or ChunkFieldType.IntArray
        or ChunkFieldType.FloatArray or ChunkFieldType.DoubleArray or ChunkFieldType.LongArray
        or ChunkFieldType.BoolArray;

    public string ValueText => Type switch
    {
        ChunkFieldType.Int32 or ChunkFieldType.Int64 => IntValue.ToString(),
        ChunkFieldType.Float => FloatValue.ToString("0.###"),
        ChunkFieldType.Double => DoubleValue.ToString("0.###"),
        ChunkFieldType.String => StringValue ?? "",
        ChunkFieldType.Bool => BoolValue ? "true" : "false",
        ChunkFieldType.ByteArray => BytesValue.Length <= 32
            ? Convert.ToHexString(BytesValue).ToLowerInvariant()
            : $"{BytesValue.Length} bytes",
        ChunkFieldType.StringArray => string.Join(", ", StringValues.Select(v => v ?? "(null)")),
        ChunkFieldType.IntArray => string.Join(", ", LongValues),
        ChunkFieldType.LongArray => string.Join(", ", LongValues),
        ChunkFieldType.FloatArray or ChunkFieldType.DoubleArray => string.Join(", ", DoubleValues),
        ChunkFieldType.BoolArray => string.Join(", ", BoolValues.Select(v => v ? "true" : "false")),
        ChunkFieldType.Tree => IsRawTree || !HasTerminator
            ? $"<raw tree {Raw.Length} bytes>"
            : $"{Children.Count} field(s)",
        ChunkFieldType.ItemStack => ItemStackIsNull
            ? "(empty item stack)"
            : $"itemstack class {ItemClass} id {ItemId} ×{StackSize}",
        ChunkFieldType.TreeArray => $"{TreeValues.Count} tree(s)",
        _ => $"<{Raw.Length} raw bytes>"
    };

    /// <summary>Text for the editor. Empty for Opaque/tree — such rows never reach the editor.</summary>
    public string EditText => Type switch
    {
        ChunkFieldType.Int32 or ChunkFieldType.Int64 => IntValue.ToString(),
        ChunkFieldType.Float => FloatValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        ChunkFieldType.Double => DoubleValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        ChunkFieldType.String => StringValue ?? "",
        ChunkFieldType.Bool => BoolValue ? "true" : "false",
        ChunkFieldType.ByteArray => Convert.ToHexString(BytesValue).ToLowerInvariant(),
        ChunkFieldType.StringArray => string.Join(", ", StringValues.Select(v => v ?? "")),
        ChunkFieldType.IntArray or ChunkFieldType.LongArray => string.Join(", ", LongValues),
        ChunkFieldType.FloatArray or ChunkFieldType.DoubleArray => string.Join(", ",
            DoubleValues.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))),
        ChunkFieldType.BoolArray => string.Join(", ", BoolValues.Select(v => v ? "true" : "false")),
        _ => ""
    };

    public override string ToString() => $"{DisplayPath} = {ValueText}";
}

/// <summary>Entity (field 6): header + root ECX/TreeAttribute fields.</summary>
public sealed class EntityRecord
{
    public string? TypeName;
    public string? Version;

    /// <summary>Root fields — serialisation goes through them (nesting is preserved).</summary>
    public List<ChunkField> RootFields = new();

    /// <summary>Flat list of leaf fields: convenient for search, cards and editing.
    /// For a nested field <see cref="ChunkField.Path" /> holds the full path.</summary>
    public List<ChunkField> Fields = new();

    public byte[] HeaderBytes = Array.Empty<byte>();
    public byte[] RawBytes = Array.Empty<byte>();
    public List<string> GameCodes = new();

    public void SetRoots(List<ChunkField> roots)
    {
        RootFields = roots;
        Fields = ChunkDataParser.Flatten(roots);
    }

    public string? Species => GameCodes.Count > 0 ? GameCodes[0] : TypeName;
    public string? Kind { get; set; }
    public string? SexAge { get; set; }

    public bool HasParsedFields => Fields.Count > 0;
    public string FieldsSummary => string.Join("\n", Fields);
}

/// <summary>Block entity (field 8): header + root ECX/TreeAttribute fields.</summary>
public sealed class BlockEntityRecord
{
    public string? TypeName;
    public List<ChunkField> RootFields = new();
    public List<ChunkField> Fields = new();
    public byte[] HeaderBytes = Array.Empty<byte>();
    public byte[] RawBytes = Array.Empty<byte>();

    public void SetRoots(List<ChunkField> roots)
    {
        RootFields = roots;
        Fields = ChunkDataParser.Flatten(roots);
    }

    public bool HasParsedFields => Fields.Count > 0;
    public string FieldsSummary => string.Join("\n", Fields);
}

/// <summary>Chunk with structured data (blocks are read-only statistics only).</summary>
public sealed class ParsedChunk
{
    public long Position;
    public int ChunkX, ChunkY, ChunkZ;
    public int WorldX, WorldY, WorldZ;
    public string? Version;
    public Dictionary<string, long> BlockCounts = new();
    public long SolidCells, FluidCells, AirCells;
    public List<EntityRecord> Entities = new();
    public List<BlockEntityRecord> BlockEntities = new();
    public byte[] RawBytes = Array.Empty<byte>();
}

// ============================= PARSER =============================

/// <summary>
/// Structural parser of chunk data (.vcdbs chunk.data):
/// top-level fields through FastSerializer (PbReader), field 6 — entities, field 8 — block entities,
/// field 1/16 — blocks (read-only statistics only, through the block registry).
///
/// The ECX/TreeAttribute format (decoded from real 1.22.6/1.22.7 saves and checked against
/// <c>TreeAttribute.ToBytes</c>):
///   [type-byte][LP key][value], where 1=int32, 2=int64, 3=double, 4=float, 5=string,
///   6=nested tree (fields up to the 0 byte), 7=ItemStack, 8=byte[], 9=bool,
///   10..16 — string[]/int[]/float[]/double[]/tree[]/long[]/bool[].
///   Everything unrecognised is preserved as Opaque bytes (lossless).
/// </summary>
public static class ChunkDataParser
{
    private const int MaxDepth = 32;

    /// <summary>Parses the whole chunk blob into the structured model.</summary>
    public static ParsedChunk Parse(byte[] blob, BlockRegistry reg, long position)
    {
        var pc = new ParsedChunk { Position = position, RawBytes = blob };
        var (cx, cy, cz) = ChunkPosHelper.DecodeIndex((ulong)position);
        pc.ChunkX = cx; pc.ChunkY = cy; pc.ChunkZ = cz;
        pc.WorldX = cx * 32; pc.WorldY = cy * 32; pc.WorldZ = cz * 32;

        byte[]? solids = null, fluids = null;
        foreach (var f in PbReader.ReadMessage(blob, maxDepth: 1, maxFields: 5000))
        {
            if (f.WireType != 2 || f.Bytes is null) continue;
            switch (f.Field)
            {
                case 1: solids = f.Bytes; break;
                case 16: fluids = f.Bytes; break;
                case 6: pc.Entities.Add(ParseEntity(f.Bytes)); break;
                case 8: pc.BlockEntities.Add(ParseBlockEntity(f.Bytes)); break;
                case 12: pc.Version = CleanAscii(f.Bytes); break;
            }
        }

        ushort[] s = solids is { Length: >= 4 }
            ? ChunkBlockDecoder.DecodeBlocksCompressed(solids)
            : new ushort[ChunkBlockDecoder.CellsPerChunk];
        ushort[]? fl = fluids is { Length: > 0 } ? SafeLiquids(fluids) : null;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != 0) { pc.SolidCells++; Bump(reg, pc.BlockCounts, s[i]); }
            else if (fl is not null && i < fl.Length && fl[i] != 0) { pc.FluidCells++; Bump(reg, pc.BlockCounts, fl[i]); }
            else pc.AirCells++;
        }
        return pc;
    }

    private static ushort[]? SafeLiquids(byte[] fluids)
    {
        try { return ChunkBlockDecoder.DecodeBlocksCompressed(fluids); }
        catch { return null; }
    }

    private static void Bump(BlockRegistry reg, Dictionary<string, long> map, ushort id)
    {
        var code = reg.CodeOf(id) ?? "id:" + id;
        map.TryGetValue(code, out var v);
        map[code] = v + 1;
    }

    // ------------------------- ECX blob: field 6 (entity) -------------------------

    public static EntityRecord ParseEntity(byte[] b)
    {
        var rec = new EntityRecord { RawBytes = b };
        rec.GameCodes = ExtractGameCodes(b);
        var sp = rec.GameCodes.Count > 0 ? rec.GameCodes[0] : null;
        if (sp is string code)
        {
            var core = code.StartsWith("game:") ? code.Substring(5) : code;
            var parts = core.Split('-');
            rec.Kind = parts[0];
            rec.SexAge = parts.Length <= 2
                ? (parts.Length == 2 ? parts[1] : null)
                : string.Join("-", parts.Skip(parts.Length - 2));
        }

        int i = 0, n = b.Length;
        if (TryReadString(b, ref i, n, out var typeName)) rec.TypeName = typeName;
        if (TryReadString(b, ref i, n, out var ver)) rec.Version = ver;
        rec.HeaderBytes = Slice(b, 0, i);
        rec.SetRoots(ParseFieldList(b, ref i, n, 0, stopAtZero: false));
        return rec;
    }

    // ------------------------- ECX blob: field 8 (block entity) -------------------------

    public static BlockEntityRecord ParseBlockEntity(byte[] b)
    {
        var rec = new BlockEntityRecord { RawBytes = b };
        int i = 0, n = b.Length;
        if (TryReadString(b, ref i, n, out var typeName)) rec.TypeName = typeName;
        rec.HeaderBytes = Slice(b, 0, i);
        rec.SetRoots(ParseFieldList(b, ref i, n, 0, stopAtZero: false));
        return rec;
    }

    // ------------------------- Core: field parsing -------------------------

    /// <summary>
    /// Parses a sequence of fields up to <paramref name="end" />. A nested tree
    /// (<paramref name="stopAtZero" />) stops at the 0 byte — that is its terminator.
    /// Everything unrecognised is preserved as Opaque (one byte at a time), so the sum of Raw
    /// covers the original bytes and the round-trip is lossless.
    /// </summary>
    public static List<ChunkField> ParseFields(byte[] b, int start, int end)
    {
        int i = start;
        return ParseFieldList(b, ref i, end, 0, stopAtZero: false);
    }

    private static List<ChunkField> ParseFieldList(byte[] b, ref int i, int end, int depth, bool stopAtZero)
    {
        var fields = new List<ChunkField>();
        if (end > b.Length) end = b.Length;
        while (i < end)
        {
            if (b[i] == 0 && stopAtZero)
            {
                i++;
                return fields;
            }

            int start = i;
            if (TryParseField(b, ref i, end, depth, out var field) && i > start)
            {
                fields.Add(field);
                continue;
            }

            i = start + 1;
            fields.Add(new ChunkField { Type = ChunkFieldType.Opaque, Raw = new[] { b[start] } });
        }
        return fields;
    }

    private static bool TryParseField(byte[] b, ref int i, int end, int depth, out ChunkField field)
    {
        field = null!;
        if (i >= end || depth > MaxDepth) return false;

        int start = i;
        byte t = b[i];
        if (t is < 1 or > 16) return false;

        int p = i + 1;
        if (!TryReadString(b, ref p, end, out var key)) return false;
        if (p > end || key.Length == 0) return false;   // an empty key is no longer a field but garbage from a shift

        var f = new ChunkField { Type = (ChunkFieldType)t, Key = key };

        switch (f.Type)
        {
            case ChunkFieldType.Int32:
                if (p + 4 > end) return false;
                f.IntValue = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
                p += 4;
                break;
            case ChunkFieldType.Int64:
                if (p + 8 > end) return false;
                f.IntValue = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(p));
                p += 8;
                break;
            case ChunkFieldType.Double:
                if (p + 8 > end) return false;
                f.DoubleValue = BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(p));
                p += 8;
                break;
            case ChunkFieldType.Float:
                if (p + 4 > end) return false;
                f.FloatValue = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(p));
                p += 4;
                break;
            case ChunkFieldType.String:
                if (!TryReadString(b, ref p, end, out var sv)) return false;
                f.StringValue = sv;
                break;
            case ChunkFieldType.Tree:
            {
                int childStart = p;
                var children = ParseFieldList(b, ref p, end, depth + 1, stopAtZero: true);
                var closed = p > childStart && b[p - 1] == 0;
                // A mod may write type 6 through StreamedTreeAttribute: then it is raw bytes, not
                // fields. We tell them apart by the fact that a normal tree parses without Opaque bytes
                // and is closed by a terminator; otherwise we leave the field raw and do not eat foreign bytes.
                if (closed && children.All(c => c.Type != ChunkFieldType.Opaque)
                    && TreeBytes(children).AsSpan().SequenceEqual(b.AsSpan(childStart, p - childStart)))
                {
                    f.Children = children;
                    f.HasTerminator = true;
                }
                else
                {
                    // StreamedTreeAttribute: type 6, but the value is a mod's raw bytes. We keep
                    // only type+key and parse the following bytes as ordinary fields/opaque:
                    // that keeps the round-trip lossless while the fields after such a tree stay reachable.
                    p = childStart;
                    f.Children = new List<ChunkField>();
                    f.HasTerminator = false;
                    f.IsRawTree = true;
                }
                break;
            }
            case ChunkFieldType.ItemStack:
            {
                if (p >= end) return false;
                f.ItemStackIsNull = b[p++] != 0;
                if (!f.ItemStackIsNull)
                {
                    if (p + 12 > end) return false;
                    f.ItemClass = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)); p += 4;
                    f.ItemId = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)); p += 4;
                    f.StackSize = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)); p += 4;
                    int attrStart = p;
                    var attrs = ParseFieldList(b, ref p, end, depth + 1, stopAtZero: true);
                    var closed = p > attrStart && b[p - 1] == 0;
                    if (closed && attrs.All(c => c.Type != ChunkFieldType.Opaque)
                        && TreeBytes(attrs).AsSpan().SequenceEqual(b.AsSpan(attrStart, p - attrStart)))
                    {
                        f.Children = attrs;
                        f.HasTerminator = true;
                    }
                    else
                    {
                        p = attrStart;
                        f.Children = new List<ChunkField>();
                        f.HasTerminator = false;
                        f.IsRawTree = true;
                    }
                }
                break;
            }
            case ChunkFieldType.ByteArray:
            {
                if (p + 2 > end) return false;
                int len = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p));
                p += 2;
                if (p + len > end) return false;
                f.BytesValue = Slice(b, p, p + len);
                p += len;
                break;
            }
            case ChunkFieldType.Bool:
                if (p >= end) return false;
                f.BoolValue = b[p++] != 0;
                break;
            case ChunkFieldType.StringArray:
            {
                if (!TryReadCount(b, ref p, end, 1, out int count)) return false;
                for (int k = 0; k < count; k++)
                {
                    if (!TryReadString(b, ref p, end, out var item)) return false;
                    f.StringValues.Add(item);
                }
                break;
            }
            case ChunkFieldType.IntArray:
            {
                if (!TryReadCount(b, ref p, end, 4, out int count)) return false;
                var arr = new long[count];
                for (int k = 0; k < count; k++)
                {
                    arr[k] = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
                    p += 4;
                }
                f.LongValues = arr;
                break;
            }
            case ChunkFieldType.LongArray:
            {
                if (!TryReadCount(b, ref p, end, 8, out int count)) return false;
                var arr = new long[count];
                for (int k = 0; k < count; k++)
                {
                    arr[k] = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(p));
                    p += 8;
                }
                f.LongValues = arr;
                break;
            }
            case ChunkFieldType.FloatArray:
            {
                if (!TryReadCount(b, ref p, end, 4, out int count)) return false;
                var arr = new double[count];
                for (int k = 0; k < count; k++)
                {
                    arr[k] = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(p));
                    p += 4;
                }
                f.DoubleValues = arr;
                break;
            }
            case ChunkFieldType.DoubleArray:
            {
                if (!TryReadCount(b, ref p, end, 8, out int count)) return false;
                var arr = new double[count];
                for (int k = 0; k < count; k++)
                {
                    arr[k] = BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(p));
                    p += 8;
                }
                f.DoubleValues = arr;
                break;
            }
            case ChunkFieldType.BoolArray:
            {
                if (!TryReadCount(b, ref p, end, 1, out int count)) return false;
                for (int k = 0; k < count; k++) f.BoolValues.Add(b[p++] != 0);
                break;
            }
            case ChunkFieldType.TreeArray:
            {
                if (!TryReadCount(b, ref p, end, 1, out int count)) return false;
                for (int k = 0; k < count; k++)
                {
                    int treeStart = p;
                    var tree = ParseFieldList(b, ref p, end, depth + 1, stopAtZero: true);
                    if (p <= treeStart) return false;
                    f.TreeValues.Add(tree);
                }
                break;
            }
            default:
                return false;
        }

        f.Raw = Slice(b, start, p);
        i = p;
        field = f;
        return true;
    }

    /// <summary>Rebuilds the tree bytes from the already parsed children (to verify that parsing is exact).</summary>
    private static byte[] TreeBytes(IReadOnlyList<ChunkField> children)
    {
        int len = children.Sum(c => c.Raw.Length) + 1;
        var bytes = new byte[len];
        int p = 0;
        foreach (var child in children)
        {
            Buffer.BlockCopy(child.Raw, 0, bytes, p, child.Raw.Length);
            p += child.Raw.Length;
        }
        bytes[p] = 0;
        return bytes;
    }

    /// <summary>Array count: int32, checked so that the declared number fits into the rest of the blob.</summary>
    private static bool TryReadCount(byte[] b, ref int p, int end, int minElementSize, out int count)
    {
        count = 0;
        if (p + 4 > end) return false;
        count = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
        p += 4;
        if (count < 0 || (long)count * minElementSize > end - p) return false;
        return true;
    }

    /// <summary>A slice without extra allocations.</summary>
    private static byte[] Slice(byte[] b, int s, int e)
    {
        var r = new byte[e - s];
        Array.Copy(b, s, r, 0, e - s);
        return r;
    }

    /// <summary>BinaryWriter LP string: 7-bit length + UTF-8. An empty string is allowed.</summary>
    private static bool TryReadString(byte[] b, ref int i, int end, out string s)
    {
        s = "";
        if (i >= end) return false;
        int p = i;
        long len = 0;
        int shift = 0;
        while (p < end && shift <= 28)
        {
            byte c = b[p++];
            len |= (long)(c & 0x7F) << shift;
            if ((c & 0x80) == 0) break;
            shift += 7;
        }
        if (p > end || shift > 28 || len < 0 || len > end - p) return false;
        s = Encoding.UTF8.GetString(b, p, (int)len);
        i = p + (int)len;
        return true;
    }

    private static string CleanAscii(byte[] b)
    {
        var sb = new StringBuilder();
        foreach (byte c in b) if (c >= 32 && c < 127) sb.Append((char)c);
        return sb.ToString();
    }

    /// <summary>
    /// Flat list of leaf fields. For a nested field <see cref="ChunkField.Path" /> is set
    /// (for example `health.currenthealth`), so the UI shows where the value came from and two identical
    /// local keys from different trees are not confused.
    /// </summary>
    public static List<ChunkField> Flatten(IEnumerable<ChunkField> roots)
    {
        var result = new List<ChunkField>();
        foreach (var f in roots) FlattenInto(f, "", result);
        return result;
    }

    private static void FlattenInto(ChunkField f, string prefix, List<ChunkField> result)
    {
        f.Path = prefix.Length == 0 ? f.Key : prefix + "." + f.Key;
        if (f.Type == ChunkFieldType.Tree)
        {
            if (f.IsRawTree && f.Children.Count == 0) result.Add(f);
            foreach (var child in f.Children) FlattenInto(child, f.Path, result);
            return;
        }

        // ItemStack is a container value: its own attributes live in Children. We show
        // the stack itself on one line (id/size), and the nested attributes as separate paths.
        if (f.Type == ChunkFieldType.ItemStack)
        {
            // The contents of an item are not an entity field: the Items tab shows them, while row-by-row
            // itemstack editing is not supported yet. We do not bloat the card with hundreds of rows.
            result.Add(f);
            return;
        }

        result.Add(f);
    }

    /// <summary>All `game:...` codes in the blob (the entity kind and other references).</summary>
    private static List<string> ExtractGameCodes(byte[] b)
    {
        var res = new List<string>();
        for (int i = 0; i + 5 <= b.Length; i++)
        {
            if (b[i] == 'g' && b[i + 1] == 'a' && b[i + 2] == 'm' && b[i + 3] == 'e' && b[i + 4] == ':')
            {
                int j = i + 5;
                var sb = new StringBuilder("game:");
                while (j < b.Length && b[j] >= 32 && b[j] < 127 &&
                       (char.IsLetterOrDigit((char)b[j]) || b[j] == '-' || b[j] == '_' || b[j] == '.' || b[j] == '/'))
                    sb.Append((char)b[j++]);
                if (sb.Length > 6) res.Add(sb.ToString());
                i = j + 1;
            }
        }
        return res.Distinct().ToList();
    }
}
