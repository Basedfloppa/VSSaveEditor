using System.Text;
using VSCore.Format;

namespace VSCore;

/// <summary>
///     One saved-data entry of the world: the `gamedata` blob keeps a dictionary of free-form keys
///     (`SaveGame.ModData`), and mods put their state there — per player or per world.
/// </summary>
public sealed class ModDataEntry
{
    /// <summary>The key as written by the mod (`divineascension_religions`, `oreMapMarkers-&lt;uid&gt;`).</summary>
    public required string Key { get; init; }

    public byte[] Value { get; init; } = Array.Empty<byte>();

    /// <summary>Byte range of the whole entry (tag + length + key + value) in the blob it was read from:
    /// removing an entry means cutting exactly this range, so every other field stays byte-identical.</summary>
    public int Start { get; init; }

    public int End { get; init; }

    /// <summary>The player this entry belongs to, when the key names one of the save's player uids.
    /// null — the entry is world-scoped (or names a player the save does not have any more).</summary>
    public string? PlayerUid { get; set; }

    /// <summary>Whether the value bytes contain a known player uid. A mod may keep the whole roster in one
    /// world-scoped key (both `*ByPlayerUid` keys of the vanilla save do), and deleting that key then affects
    /// every player at once — the UI has to be able to say so instead of counting it as "world data".</summary>
    public bool MentionsPlayer { get; set; }

    public int Length => End - Start;
}

/// <summary>Mod data of one mod (or of one vanilla key group): the player-scoped entries and the world-scoped ones.</summary>
public sealed class ModDataGroup
{
    public required string Id { get; init; }          // as it appears in the keys (the first spelling seen)
    public List<ModDataEntry> Entries { get; } = new();

    public List<ModDataEntry> PlayerEntries
    {
        get
        {
            var list = new List<ModDataEntry>();
            foreach (var e in Entries) if (e.PlayerUid is not null) list.Add(e);
            return list;
        }
    }

    public List<ModDataEntry> WorldEntries
    {
        get
        {
            var list = new List<ModDataEntry>();
            foreach (var e in Entries) if (e.PlayerUid is null) list.Add(e);
            return list;
        }
    }

    public int PlayerCount
    {
        get
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in Entries) if (e.PlayerUid is not null) set.Add(e.PlayerUid);
            return set.Count;
        }
    }

    public long Bytes
    {
        get
        {
            long n = 0;
            foreach (var e in Entries) n += e.Value.Length;
            return n;
        }
    }

    /// <summary>World-scoped keys whose value mentions a player: deleting them erases data for several players.</summary>
    public int WorldKeysMentioningPlayers
    {
        get
        {
            int n = 0;
            foreach (var e in Entries) if (e.PlayerUid is null && e.MentionsPlayer) n++;
            return n;
        }
    }
}

/// <summary>Outcome of a removal: the new blob and exactly which entries were cut out of the old one.</summary>
public sealed record ModDataRemoval(byte[] Blob, IReadOnlyList<ModDataEntry> Removed)
{
    public bool Changed => Removed.Count > 0;
}

/// <summary>
///     Reading and editing of the world's saved mod data.
///     The `gamedata` blob is a FastSerializer message; the mod data is its repeated field 11, each entry
///     `{1: key (string), 2: value (bytes)}` — the same shape for every mod, which is why one editor can serve
///     all of them. Removal cuts whole entries out of the blob and leaves every other field byte-identical:
///     the block/item registries (megabytes in this blob) must not be rebuilt from a parse.
///     Key convention found in real saves: `&lt;modid&gt;_&lt;key&gt;` (`divineascension_religions`), and the
///     per-player variants end with the player uid (`divineascension_playerprogressiondata_&lt;uid&gt;`).
///     Other separators exist (`Wiltoga.OfflineFoodNoSpoil.PlayerData`, `terratag:labels_v1`), so the mod id is
///     "everything before the first separator" and the player is "an exact uid at the end of the key".
/// </summary>
public static class ModDataStore
{
    /// <summary>Field of the gamedata message that holds the mod data dictionary.</summary>
    public const int ModDataField = 11;

    /// <summary>Inside an entry: field 1 is the key, field 2 the value. Read and written through the same pair
    /// of constants — the pair is half of the format, the other half being the two-field message shape.</summary>
    private const int KeyField = 1;

    private const int ValueField = 2;

    // '-' is here because vanilla and mod keys use it too (`oreMapMarkers-<uid>`, `roadnetwork-Roads`).
    private static readonly char[] Separators = { '_', '.', ':', '-' };

    /// <summary>
    ///     The mod data entries of a gamedata blob. Malformed entries are skipped, not thrown on: a save with
    ///     one broken key must still list all the others (and the other tables are read independently anyway).
    /// </summary>
    /// <param name="playerUids">
    ///     Known player uids of the save. With them every entry gets <see cref="ModDataEntry.PlayerUid" />
    ///     and <see cref="ModDataEntry.MentionsPlayer" />; without them the whole dictionary reads as world data.
    /// </param>
    public static List<ModDataEntry> Read(byte[]? gamedata, IReadOnlySet<string>? playerUids = null)
    {
        var list = new List<ModDataEntry>();
        if (gamedata is not { Length: > 0 }) return list;
        foreach (var f in ChunkDataEditor.WalkTop(gamedata))
        {
            if (f.Field != ModDataField) continue;
            var (key, value) = ParseEntry(gamedata, f.PayloadStart, f.PayloadEnd);
            if (key is null || key.Length == 0) continue;
            var entry = new ModDataEntry
            {
                Key = key,
                Value = value ?? Array.Empty<byte>(),
                Start = f.Start,
                End = f.PayloadEnd
            };
            if (playerUids is { Count: > 0 })
            {
                entry.PlayerUid = PlayerUidIn(key, playerUids);
                foreach (var uid in playerUids)
                {
                    if (ContainsAscii(entry.Value, uid)) { entry.MentionsPlayer = true; break; }
                }
            }

            list.Add(entry);
        }

        return list;
    }

    /// <summary>Groups entries by mod id, case-insensitively (`DivineAscension.ModConfig` belongs to the same mod
    /// as `divineascension_religions`). Order: mods with player data first, then by name — that is the order the
    /// "who can I clear" list is read in.</summary>
    public static List<ModDataGroup> Group(IEnumerable<ModDataEntry> entries)
    {
        var byId = new Dictionary<string, ModDataGroup>(StringComparer.OrdinalIgnoreCase);
        var order = new List<ModDataGroup>();
        foreach (var e in entries)
        {
            var id = ModIdOf(BaseKey(e.Key, e.PlayerUid));
            if (!byId.TryGetValue(id, out var g))
            {
                g = new ModDataGroup { Id = id };
                byId[id] = g;
                order.Add(g);
            }

            g.Entries.Add(e);
        }

        return order
            .OrderByDescending(g => g.PlayerCount)
            .ThenByDescending(g => g.Entries.Count)
            .ThenBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    ///     Cuts the given entries out of the blob they were read from. Ranges come from
    ///     <see cref="Read" />, so the entries must belong to THIS blob (the UI re-reads it every time).
    ///     Nothing matched — the same array is returned, so a "remove" that found nothing is a no-op, not a rewrite.
    /// </summary>
    public static ModDataRemoval Remove(byte[] gamedata, IEnumerable<ModDataEntry> entries)
    {
        var drop = entries.Where(e => InBlob(e, gamedata.Length)).OrderBy(e => e.Start).ToList();
        if (drop.Count == 0) return new ModDataRemoval(gamedata, Array.Empty<ModDataEntry>());
        return new ModDataRemoval(ByteSplice.Apply(gamedata, drop.Select(e => ByteSplice.Cut(e.Start, e.End))), drop);
    }

    /// <summary>An entry whose byte range can be cut out of a blob of this length.</summary>
    private static bool InBlob(ModDataEntry e, int blobLength)
        => e.Start >= 0 && e.End > e.Start && e.End <= blobLength;

    /// <summary>Removal by key (the whole key, not the prefix): an unknown key leaves the blob untouched.</summary>
    public static ModDataRemoval RemoveKeys(byte[] gamedata, IEnumerable<string> keys)
    {
        var set = new HashSet<string>(keys, StringComparer.Ordinal);
        return Remove(gamedata, Read(gamedata).Where(e => set.Contains(e.Key)).ToList());
    }

    /// <summary>
    ///     Rewrites entry values and drops entries in ONE pass. One pass is not a style choice: after the first
    ///     value that changes length every later byte range of the blob is stale, so entry-by-entry splicing
    ///     would corrupt the dictionary. Entries that are neither rewritten nor dropped keep their exact bytes.
    /// </summary>
    public static byte[] Rewrite(byte[] gamedata, IReadOnlyDictionary<string, byte[]>? newValues = null,
        IReadOnlySet<string>? removed = null)
    {
        var edits = new List<ByteSplice.Edit>();
        foreach (var e in Read(gamedata).Where(e => InBlob(e, gamedata.Length)))
        {
            if (removed is not null && removed.Contains(e.Key))
            {
                edits.Add(ByteSplice.Cut(e.Start, e.End));
                continue;
            }

            if (newValues is not null && newValues.TryGetValue(e.Key, out var value)
                && !value.AsSpan().SequenceEqual(e.Value))
                edits.Add(ByteSplice.Replace(e.Start, e.End,
                    PbWriter.MessageBytes(ModDataField, EntryBytes(e.Key, value))));
        }

        return ByteSplice.Apply(gamedata, edits);
    }

    /// <summary>The bytes of one entry: `{1: key, 2: value}` — the same shape the game writes.</summary>
    public static byte[] EntryBytes(string key, byte[] value)
    {
        var ms = new MemoryStream(key.Length + value.Length + 8);
        PbWriter.Message(ms, KeyField, Encoding.UTF8.GetBytes(key));
        PbWriter.Message(ms, ValueField, value);
        return ms.ToArray();
    }

    /// <summary>The mod id of a key: everything before the first separator. A key without one is a group of its own.</summary>
    public static string ModIdOf(string key)
    {
        int i = key.IndexOfAny(Separators);
        return i > 0 ? key[..i] : key;
    }

    /// <summary>
    ///     The player uid a key ends with (`&lt;anything&gt;_&lt;uid&gt;`, also `-uid`/`:uid`/`.uid`), or null.
    ///     Only an exact uid counts and only right after a separator: a substring match would also hit
    ///     `loreDiscoveriesByPlayerUid`, which is one key for the whole roster rather than a per-player one.
    /// </summary>
    public static string? PlayerUidIn(string key, IReadOnlySet<string> uids)
    {
        if (uids.Count == 0) return null;
        foreach (var uid in uids)
        {
            if (uid.Length == 0 || key.Length <= uid.Length) continue;
            if (!key.EndsWith(uid, StringComparison.Ordinal)) continue;
            if (Array.IndexOf(Separators, key[key.Length - uid.Length - 1]) >= 0) return uid;
        }

        return null;
    }

    /// <summary>The key without its `&lt;separator&gt;&lt;uid&gt;` tail — the part the mod id is read from.</summary>
    public static string BaseKey(string key, string? playerUid)
        => playerUid is { Length: > 0 } && key.Length > playerUid.Length + 1
           && key.EndsWith(playerUid, StringComparison.Ordinal)
            ? key[..^(playerUid.Length + 1)]
            : key;

    private static (string? Key, byte[]? Value) ParseEntry(byte[] blob, int start, int end)
    {
        string? key = null;
        byte[]? value = null;
        int i = start;
        while (i < end)
        {
            var (tag, ni) = PbReader.ReadVarint(blob, i);
            if (ni <= i) return (null, null);
            i = ni;
            int field = (int)(tag >> 3);
            int wt = (int)(tag & 7);
            if (wt == 0)
            {
                var (_, vi) = PbReader.ReadVarint(blob, i);
                if (vi <= i) return (null, null);
                i = vi;
                continue;
            }

            // The entry is {string, bytes}; any other wire type means this is not a mod-data entry at all.
            if (wt != 2) return (null, null);
            var (len, li) = PbReader.ReadVarint(blob, i);
            if (li < i || !PbReader.LengthFits(len, li, end)) return (null, null);
            var payloadStart = li;
            var payloadEnd = li + (int)len;
            if (field == KeyField) key = Encoding.UTF8.GetString(blob, payloadStart, payloadEnd - payloadStart);
            else if (field == ValueField) value = blob[payloadStart..payloadEnd];
            i = payloadEnd;
        }

        return (key, value);
    }

    private static bool ContainsAscii(byte[] haystack, string needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == (byte)needle[j]) j++;
            if (j == needle.Length) return true;
        }

        return false;
    }
}
