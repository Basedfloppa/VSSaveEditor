using System;
using System.Collections.Generic;
using System.Text;
using VSCore.Format;

namespace VSCore;

/// <summary>
/// Block registry of a VS save: block id -> blocktype code.
///
/// Source: the binary "BlockIDs" section in the blob of the `gamedata` table. It holds
/// a protobuf list of pairs (field1 = id varint, field2 = code string), for example
///   0 = air, 1 = mantle, 2897 = water-n-1, 7435 = soil-medium-normal, ...
/// Parsed by our <see cref="PbReader"/>. The code has no domain prefix ("soil-*", "water-*").
/// </summary>
public sealed class BlockRegistry
{
    private readonly Dictionary<int, string> _idToCode = new();
    private bool _empty;

    /// <summary>Block code for an id, or null if it is unknown.</summary>
    public string? CodeOf(int id) => _idToCode.TryGetValue(id, out var c) ? c : null;
    public bool HasId(int id) => _idToCode.ContainsKey(id);
    public int Count => _idToCode.Count;

    /// <summary>All codes in the registry (they may repeat: one code can have several ids). Needed where
    /// the code itself matters, not the id: picking blocks by mod domain ("has a colon") and diagnostics.</summary>
    public IReadOnlyCollection<string> Codes => _idToCode.Values;

    private Dictionary<string, List<int>>? _codeToIds;

    /// <summary>Reverse index: code -> list of ids (one code may correspond to several ids).</summary>
    private Dictionary<string, List<int>> CodeIndex
    {
        get
        {
            if (_codeToIds is null)
            {
                var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in _idToCode)
                {
                    if (!map.TryGetValue(kv.Value, out var list)) map[kv.Value] = list = new List<int>(1);
                    list.Add(kv.Key);
                }
                _codeToIds = map;
            }
            return _codeToIds;
        }
    }

    /// <summary>All block ids with the given code (for the filter overlay: the user picks a code, we filter by id).</summary>
    public IReadOnlyList<int> IdsOf(string code)
        => CodeIndex.TryGetValue(code, out var list) ? list : (IReadOnlyList<int>)Array.Empty<int>();

    public bool HasCode(string code) => CodeIndex.ContainsKey(code);

    /// <summary>Whether the registry holds a code containing the substring (for example "chisel"/"microblock").
    /// A cheap check of whether chiselled blocks are worth the trouble in this save at all.</summary>
    public bool AnyCodeContains(string needle)
    {
        foreach (var c in _idToCode.Values)
            if (c.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static int IndexOf(byte[] hay, string needle)
    {
        byte[] n = Encoding.ASCII.GetBytes(needle);
        for (int i = 0; i <= hay.Length - n.Length; i++)
        {
            int j = 0;
            while (j < n.Length && hay[i + j] == n[j]) j++;
            if (j == n.Length) return i;
        }
        return -1;
    }

    /// <summary>Parses the block registry out of the gamedata blob.</summary>
    public static BlockRegistry FromGamedata(byte[] gamedata)
    {
        var reg = new BlockRegistry();
        if (gamedata is null) return reg;

        int mk = IndexOf(gamedata, "BlockIDs");
        if (mk < 0) return reg;
        int i = mk + "BlockIDs".Length;
        if (i >= gamedata.Length) return reg;

        // wire tag (we expect field2, wiretype2 = len-delim), then the length
        var (tag, t2) = PbReader.ReadVarint(gamedata, i); i = t2;
        if ((tag & 7) != 2) return reg;
        var (len, l2) = PbReader.ReadVarint(gamedata, i); i = l2;
        if (!PbReader.LengthFits(len, i, gamedata.Length)) return reg;
        int payloadLen = (int)len;
        var payload = new byte[payloadLen];
        Array.Copy(gamedata, i, payload, 0, payloadLen);

        // The registry is large (tens of thousands of entries) — it needs a higher field limit than the default 4096.
        foreach (var e in PbReader.ReadMessage(payload, maxDepth: 1, maxFields: 200_000))
        {
            if (e.WireType != 2 || e.Bytes is null) continue;
            int id = -1; string? code = null;
            foreach (var f in PbReader.ReadMessage(e.Bytes, maxDepth: 1, maxFields: 64))
            {
                if (f.WireType == 0) id = (int)f.IntValue;
                else if (f.WireType == 2 && f.Bytes is { } seg) code = Encoding.UTF8.GetString(seg);
            }
            if (id >= 0 && code is { Length: > 0 })
                reg._idToCode[id] = code;
        }
        reg._empty = reg._idToCode.Count == 0;
        return reg;
    }
}
