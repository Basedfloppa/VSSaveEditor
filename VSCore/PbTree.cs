using System.Text;
using VSCore.Format;

namespace VSCore;

/// <summary>
///     One field of a FastSerializer/protobuf message, kept editable: mods store their per-player state in
///     `gamedata` as opaque values with no schema and often without names, so the only handle on them is the
///     field number. Everything the editor shows and changes goes through this node.
///     Two invariants keep the write lossless: <see cref="Raw" /> means the bytes were not parsed at all — such a
///     node is written back as they are and never emitted as a field; <see cref="Children" /> != null means the
///     payload is a message (wire type 2 only) and it is re-serialised only when something below it changed.
/// </summary>
public sealed class PbNode
{
    public int Field { get; init; }
    public int WireType { get; init; }                    // 0=varint, 2=len-delim, 5=fixed32, 1=fixed64

    public long IntValue { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    /// <summary>Nested message, when the payload parses as one. null — the value is opaque (text, bytes, …).</summary>
    public List<PbNode>? Children { get; set; }

    /// <summary>The payload was changed through a setter; without it the original bytes are emitted unchanged,
    /// so a value nobody touched is written back byte for byte (no re-encoding of exotic varints).</summary>
    public bool Dirty { get; set; }

    /// <summary>The node is cut out on serialisation. A whole subtree is removed together with the node.</summary>
    public bool Removed { get; set; }

    /// <summary>The value could not be parsed at all: the node holds the bytes of the WHOLE value and is
    /// written back as they are — a field 0 would otherwise be emitted as a valid-looking tag and corrupt it.</summary>
    public bool Raw { get; init; }

    public bool IsMessage => Children is not null;

    /// <summary>Value as text, when the payload is readable UTF-8 (player uids in a dictionary, names, …).</summary>
    public string? Text => WireType == 2 && !IsMessage ? AsText(Payload) : null;

    public string Label => Raw ? "raw value" : WireType switch
    {
        0 => $"field {Field}",
        2 => $"field {Field}",
        _ => $"field {Field} (fixed)"
    };

    public string ValueText => WireType switch
    {
        0 => IntValue.ToString(),
        2 => IsMessage ? $"{Children!.Count} field(s)" : Text is { } t ? t : $"{Payload.Length} byte(s)",
        5 or 1 => $"0x{Convert.ToHexString(Payload).ToLowerInvariant()}",
        _ => $"{Payload.Length} byte(s)"
    };

    /// <summary>Printable UTF-8 or null. Control characters disqualify a payload: a binary blob that happens to
    /// be valid UTF-8 would otherwise be offered for text editing and silently broken by a round-trip.</summary>
    public static string? AsText(byte[] b)
    {
        if (b.Length == 0) return "";
        try
        {
            var s = new UTF8Encoding(false, true).GetString(b);
            foreach (var ch in s)
                if (char.IsControl(ch)) return null;
            return s;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    public override string ToString() => $"{Label} = {ValueText}";
}

/// <summary>
///     Lossless read/edit/write of an arbitrary protobuf message (mod data values in `gamedata`).
///     The format has no schema here, so "structured editing" means field numbers, wire types and payloads:
///     a value can be edited (varint, text, hex) or cut out, and untouched bytes are written back as they were.
/// </summary>
public static class PbTree
{
    private const int MaxDepth = 8;
    private const int MaxFieldsPerMessage = 4096;

    /// <summary>
    ///     Parses a message. Malformed tails are preserved as one opaque node rather than thrown on: the value
    ///     of an unknown mod must stay editable, and a half-parsed tree that serialises back to the same bytes
    ///     is still a correct representation.
    /// </summary>
    public static List<PbNode> Parse(byte[]? blob)
    {
        var list = new List<PbNode>();
        if (blob is not { Length: > 0 }) return list;
        var parsed = TryParseMessage(blob, MaxDepth);
        if (parsed is not null) return parsed;
        // unparseable: keep the whole value as one opaque node so it can still be seen and cut
        list.Add(new PbNode { Field = 0, WireType = 2, Payload = blob, Raw = true });
        return list;
    }

    private static List<PbNode>? TryParseMessage(byte[] blob, int depth)
    {
        var list = new List<PbNode>();
        int i = 0;
        while (i < blob.Length)
        {
            if (list.Count >= MaxFieldsPerMessage) return null;
            int start = i;
            var (tag, ni) = PbReader.ReadVarint(blob, i);
            if (ni <= i || ni > blob.Length) return null;
            i = ni;
            int field = (int)(tag >> 3);
            int wt = (int)(tag & 7);
            if (field <= 0) return null;
            switch (wt)
            {
                case 0:
                {
                    var (v, vi) = PbReader.ReadVarint(blob, i);
                    if (vi <= i || vi > blob.Length) return null;
                    list.Add(new PbNode { Field = field, WireType = 0, IntValue = v, Payload = blob[i..vi] });
                    i = vi;
                    break;
                }
                case 2:
                {
                    var (ln, li) = PbReader.ReadVarint(blob, i);
                    if (li < i || !PbReader.LengthFits(ln, li, blob.Length)) return null;
                    int end = li + (int)ln;
                    var node = new PbNode { Field = field, WireType = 2, Payload = blob[li..end] };
                    node.Children = asMessage(node.Payload) ? TryParseMessage(node.Payload, depth - 1) : null;
                    list.Add(node);
                    i = end;
                    break;
                }
                case 5:
                    if (i + 4 > blob.Length) return null;
                    list.Add(new PbNode { Field = field, WireType = 5, Payload = blob[i..(i + 4)] });
                    i += 4;
                    break;
                case 1:
                    if (i + 8 > blob.Length) return null;
                    list.Add(new PbNode { Field = field, WireType = 1, Payload = blob[i..(i + 8)] });
                    i += 8;
                    break;
                default:
                    return null; // groups and unknown wire types: not this format
            }

            if (i <= start) return null;
        }

        return list;

        // A message is only assumed when it can be parsed as one AND the bytes are not plain text: a uid or a
        // name is also "parseable" now and then, and offering it as a subtree would hide its value.
        static bool asMessage(byte[] payload)
            => payload.Length > 1 && PbNode.AsText(payload) is null;
    }

    /// <summary>Serialises the nodes; removed ones are cut. Untouched payloads are emitted byte for byte.</summary>
    public static byte[] Serialize(IEnumerable<PbNode> nodes)
    {
        var ms = new MemoryStream();
        foreach (var n in nodes)
        {
            if (n.Removed) continue;
            if (n.Raw)
            {
                ms.Write(n.Payload, 0, n.Payload.Length);
                continue;
            }

            switch (n.WireType)
            {
                case 0:
                    PbWriter.Int(ms, n.Field, n.IntValue);
                    break;
                case 2:
                    PbWriter.Message(ms, n.Field,
                        n.Children is not null && Changed(n) ? Serialize(n.Children) : n.Payload);
                    break;
                default:
                    PbWriter.Varint(ms, ((long)n.Field << 3) | (uint)n.WireType);
                    ms.Write(n.Payload, 0, n.Payload.Length);
                    break;
            }
        }

        return ms.ToArray();
    }

    /// <summary>Whether this node or anything below it was edited or removed.</summary>
    public static bool Changed(PbNode n)
    {
        if (n.Dirty || n.Removed) return true;
        if (n.Children is null) return false;
        foreach (var c in n.Children)
            if (Changed(c)) return true;
        return false;
    }

    public static bool AnyChanged(IEnumerable<PbNode> nodes)
    {
        foreach (var n in nodes)
            if (Changed(n)) return true;
        return false;
    }

    public static void SetInt(PbNode n, long value)
    {
        if (n.WireType != 0) return;
        n.IntValue = value;
        n.Dirty = true;
    }

    /// <summary>Replaces the payload of a len-delimited leaf with UTF-8 text.</summary>
    public static void SetText(PbNode n, string text)
    {
        if (n.WireType != 2 || n.IsMessage) return;
        n.Payload = Encoding.UTF8.GetBytes(text);
        n.Dirty = true;
    }

    public static void SetBytes(PbNode n, byte[] payload)
    {
        if (n.WireType != 2 || n.IsMessage) return;
        n.Payload = payload;
        n.Dirty = true;
    }

    /// <summary>
    ///     Removes the top-level nodes whose subtree contains the given text (a player uid inside a
    ///     `&lt;key&gt;ByPlayerUid` dictionary). Whole entries are cut, never a sub-field of an entry: a dictionary
    ///     entry that lost only its key would keep the value and stop being addressable.
    /// </summary>
    public static int RemoveEntriesMentioning(List<PbNode> roots, string text)
    {
        if (text.Length == 0) return 0;
        int n = 0;
        foreach (var node in roots)
        {
            if (node.Removed || !Mentions(node, text)) continue;
            n++;
            node.Removed = true;
        }

        return n;
    }

    /// <summary>Whether the node itself or any descendant holds the text (as a string payload).</summary>
    public static bool Mentions(PbNode node, string text)
    {
        if (node.WireType == 2 && !node.IsMessage)
        {
            var s = node.Text;
            if (s is not null && s.Contains(text, StringComparison.Ordinal)) return true;
        }

        if (node.Children is null) return false;
        foreach (var c in node.Children)
            if (Mentions(c, text)) return true;
        return false;
    }

    /// <summary>How many top-level nodes name the text — shown before anything is marked.</summary>
    public static int CountEntriesMentioning(IEnumerable<PbNode> roots, string text)
        => roots.Count(n => !n.Removed && Mentions(n, text));
}
