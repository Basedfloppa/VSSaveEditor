using System.Globalization;
using System.Text;
using VSCore.Format;

namespace VSCore;

/// <summary>Kind of a value in the world tree — the byte that starts every entry.</summary>
public enum WorldSettingKind
{
    String,
    Integer,
    Float,
    Bool
}

/// <summary>
///     One entry of the world tree in the save (`gamedata` field 31): a name and a typed value, stored as
///     <c>&lt;type byte&gt;&lt;len&gt;&lt;name&gt;&lt;value&gt;</c>. The vanilla world settings (gameMode, daysPerMonth,
///     landformScale, …) and the mod entries that live beside them (`stonequarry:…`, whole `…Config.json` blobs)
///     share one flat list — nothing here tries to tell a "setting" from a "mod datum": the save does not mark them,
///     and a guess would show as an invented boundary.
/// </summary>
public sealed class WorldSetting
{
    /// <summary>Longer values are cut in the list; the card shows them in full.</summary>
    public const int PreviewChars = 160;

    public required string Name { get; init; }
    public required WorldSettingKind Kind { get; init; }

    /// <summary>The value in display form: `true`, `512`, `1.5`, or the string exactly as stored.</summary>
    public required string Value { get; init; }

    /// <summary>Payload bytes the value takes in the blob (a string's length prefix is not counted).</summary>
    public int ValueBytes { get; init; }

    /// <summary>Ordinal of the entry in the tree. Mods may store the same name twice, so a name alone is not a key.</summary>
    public int Index { get; init; }

    /// <summary>Offset of the entry's type byte inside the tree blob.</summary>
    public int EntryOffset { get; init; }

    /// <summary>
    ///     Offset where an edit starts writing. For a string that is the length prefix (the new value may have
    ///     another length, so the prefix is part of the replacement); for int/float/bool it is the value itself.
    /// </summary>
    public int ValuePrefixStart { get; init; }

    /// <summary>Offset just past the value — the end of the replaced range.</summary>
    public int ValueEnd { get; init; }

    /// <summary>
    ///     True when a pending edit changes this entry: the row then says so, and the list shows the value the
    ///     save will get, not the one it still has. A freshly parsed entry is unmarked; the window sets the flag
    ///     when it knows the stored value differs.
    /// </summary>
    public bool IsEdited { get; set; }

    public bool IsLong => Value.Length > PreviewChars;

    /// <summary>The value cut for the list. ASCII letters keep the "B" honest: `… (2852 chars)`.</summary>
    public string Preview => Value.Length <= PreviewChars ? Value : Value[..PreviewChars] + $"… ({Value.Length} chars)";

    /// <summary>The list row: `name = value` (the list itself draws two aligned columns).</summary>
    public string Display => Name + " = " + Preview;

    public string KindName => Kind switch
    {
        WorldSettingKind.String => "string",
        WorldSettingKind.Integer => "int",
        WorldSettingKind.Float => "float",
        WorldSettingKind.Bool => "bool",
        _ => "?"
    };
}

/// <summary>
///     Parsed world tree: the settings, how much of the blob was understood, and why it stopped early
///     (a value type this build does not know — mods may store their own). Also holds what an edit needs:
///     the tree bytes and where they sit in `gamedata`.
/// </summary>
public sealed class WorldSettings
{
    public IReadOnlyList<WorldSetting> Settings { get; }
    public int ParsedBytes { get; }
    public int TotalBytes { get; }
    public string? Warning { get; }

    /// <summary>The tree bytes (the payload of `gamedata` field 31); null when the save carries no tree.</summary>
    public byte[]? Tree { get; }

    /// <summary>Where those bytes start inside `gamedata` — an edit splices the rebuilt tree back at this offset.</summary>
    public int TreeOffset { get; }

    /// <summary>
    ///     Offset of the length prefix that introduces the tree. An edit that changes the tree's SIZE has to
    ///     re-emit that prefix as well, so the replaced range starts here rather than at the payload.
    /// </summary>
    public int TreeHeaderStart { get; }

    /// <summary>Whether an entry of this tree can be rewritten (the framing is known and located).</summary>
    public bool CanEdit => Tree is { Length: > 0 } && TreeOffset > 0 && TreeHeaderStart >= 0;

    private WorldSettings(List<WorldSetting> settings, int parsed, int total, string? warning, byte[]? tree,
        int treeOffset, int treeHeaderStart)
    {
        TreeHeaderStart = treeHeaderStart;
        Settings = settings;
        ParsedBytes = parsed;
        TotalBytes = total;
        Warning = warning;
        Tree = tree;
        TreeOffset = treeOffset;
    }

    /// <summary>
    ///     Parses the world tree out of the `gamedata` blob. Never throws: a truncated or unknown entry ends the
    ///     walk, keeps what was read and says why — a half-understood tree must read as half-understood, not as
    ///     "no settings".
    /// </summary>
    public static WorldSettings Parse(byte[]? gamedata)
    {
        var none = new List<WorldSetting>();
        if (gamedata is null || gamedata.Length == 0) return new WorldSettings(none, 0, 0, null, null, -1, -1);
        if (!TryFindTree(gamedata, out var tree, out int treeOffset, out int treeHeader))
            return new WorldSettings(none, 0, gamedata.Length, "the save has no world tree (gamedata field 31)",
                null, -1, -1);

        var list = new List<WorldSetting>();
        int i = 0;
        string? warning = null;
        while (i < tree.Length)
        {
            int entryAt = i;
            byte type = tree[i++];
            if (type == 0) break; // the tree ends with a zero byte
            if (!ReadText(tree, ref i, out var name))
            {
                warning = $"the tree ends inside the name at byte {entryAt}";
                i = entryAt;
                break;
            }

            // The value starts here: for a string that is its length prefix, for a number the four bytes. An edit
            // replaces from this offset, so it lives on the entry rather than being recomputed from the name later.
            int prefixAt = i;
            if (!TryReadValue(tree, ref i, type, out var kind, out var value, out int size))
            {
                // A value type this build does not know: its length is unknown, so the rest of the tree
                // cannot be walked. Stop here and say so instead of guessing a length.
                warning = $"value type {type} of '{name}' at byte {entryAt} is not one this build reads " +
                          $"({list.Count} entries read, rest skipped)";
                i = entryAt;
                break;
            }

            list.Add(new WorldSetting
            {
                Name = name,
                Kind = kind,
                Value = value,
                ValueBytes = size,
                Index = list.Count,
                EntryOffset = entryAt,
                ValuePrefixStart = prefixAt,
                ValueEnd = i
            });
        }

        return new WorldSettings(list, i, tree.Length, warning, tree, treeOffset, treeHeader);
    }

    /// <summary>
    ///     Finds the tree field and, unlike a plain field walk, remembers where its payload begins: an edit needs
    ///     that offset to put the rebuilt tree back into `gamedata`. Walks the wire format by hand for the same
    ///     reason — the reader API returns copies without positions.
    /// </summary>
    private static bool TryFindTree(byte[] gamedata, out byte[] tree, out int offset, out int headerStart)
    {
        tree = Array.Empty<byte>();
        offset = -1;
        headerStart = -1;
        int i = 0;
        while (i < gamedata.Length)
        {
            var (tag, afterTag) = PbReader.ReadVarint(gamedata, i);
            if (tag < 0) return false;
            int field = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            switch (wire)
            {
                case 0:
                {
                    var (_, next) = PbReader.ReadVarint(gamedata, afterTag);
                    if (next <= afterTag) return false; // truncated varint
                    i = next;
                    break;
                }
                case 1: // fixed64
                    if (afterTag + 8 > gamedata.Length) return false;
                    i = afterTag + 8;
                    break;
                case 5: // fixed32
                    if (afterTag + 4 > gamedata.Length) return false;
                    i = afterTag + 4;
                    break;
                case 2:
                {
                    var (len, start) = PbReader.ReadVarint(gamedata, afterTag);
                    if (!PbReader.LengthFits(len, start, gamedata.Length)) return false;
                    int n = (int)len;
                    if (field == 31 && n > 0)
                    {
                        tree = gamedata[start..(start + n)];
                        offset = start;
                        headerStart = afterTag; // the length varint begins right after the tag
                        return true;
                    }

                    i = start + n;
                    break;
                }
                default:
                    return false; // unknown wire type: the rest cannot be walked
            }
        }

        return false;
    }

    /// <summary>Reads one value by its type byte; false means the type is unknown or the tree ends inside it.</summary>
    private static bool TryReadValue(byte[] tree, ref int i, byte type, out WorldSettingKind kind, out string value, out int size)
    {
        kind = WorldSettingKind.String;
        value = "";
        size = 0;
        switch (type)
        {
            case 5: // string
            {
                if (!ReadText(tree, ref i, out var text, out int payload)) return false;
                kind = WorldSettingKind.String;
                value = text;
                size = payload;
                return true;
            }
            case 9: // bool, one byte
                if (i + 1 > tree.Length) return false;
                kind = WorldSettingKind.Bool;
                value = tree[i++] != 0 ? "true" : "false";
                size = 1;
                return true;
            case 1: // int32
                if (i + 4 > tree.Length) return false;
                kind = WorldSettingKind.Integer;
                value = BitConverter.ToInt32(tree, i).ToString(CultureInfo.InvariantCulture);
                size = 4;
                i += 4;
                return true;
            case 4: // float32
                if (i + 4 > tree.Length) return false;
                kind = WorldSettingKind.Float;
                value = BitConverter.ToSingle(tree, i).ToString("0.#######", CultureInfo.InvariantCulture);
                size = 4;
                i += 4;
                return true;
            default:
                return false;
        }
    }

    /// <summary>`&lt;varint length&gt;&lt;UTF-8&gt;` inside the tree; a length that runs past the end is a failure, not an exception.</summary>
    private static bool ReadText(byte[] b, ref int i, out string text) => ReadText(b, ref i, out text, out _);

    /// <summary>The same, also reporting the payload length (the length prefix is framing, not value).</summary>
    private static bool ReadText(byte[] b, ref int i, out string text, out int payloadBytes)
    {
        text = "";
        payloadBytes = 0;
        if (i >= b.Length) return false;
        var (len, next) = PbReader.ReadVarint(b, i);
        if (len < 0 || !PbReader.LengthFits(len, next, b.Length)) return false;
        int n = (int)len;
        text = Encoding.UTF8.GetString(b, next, n);
        i = next + n;
        payloadBytes = n;
        return true;
    }
}
