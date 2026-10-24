using System.Text;
using VSCore.Format;

namespace VSCore;

/// <summary>
///     Registry of save items: item id -> code, from the `ItemIDs` section of the `gamedata` blob.
///     Built the same way as <see cref="BlockRegistry" />, but item ids and block ids are different
///     namespaces: in one and the same save the same number may mean `soil-medium-normal`
///     (a block) and `clothes-upperbody-commoner-shirt` (an item). That is why resolution goes by
///     <c>ItemStack.Class</c>: 0 — <see cref="BlockRegistry" />, 1 — this registry.
/// </summary>
public sealed class ItemRegistry
{
    private readonly Dictionary<int, string> _idToCode = new();

    public string? CodeOf(int id) => _idToCode.TryGetValue(id, out var c) ? c : null;
    public int Count => _idToCode.Count;
    public IReadOnlyCollection<string> Codes => _idToCode.Values;

    public static ItemRegistry FromGamedata(byte[]? gamedata)
    {
        var reg = new ItemRegistry();
        if (gamedata is null || gamedata.Length == 0) return reg;

        int marker = IndexOf(gamedata, "ItemIDs");
        if (marker < 0) return reg;
        int i = marker + "ItemIDs".Length;
        if (i >= gamedata.Length) return reg;

        // The section name is followed by a tag (field 2, wiretype 2) and the payload length. This is the same frame
        // as in BlockIDs: a repeated message {field1 = id, field2 = code}.
        var (tag, t2) = PbReader.ReadVarint(gamedata, i);
        i = t2;
        if ((tag & 7) != 2) return reg;
        var (len, l2) = PbReader.ReadVarint(gamedata, i);
        i = l2;
        if (!PbReader.LengthFits(len, i, gamedata.Length)) return reg;

        var payload = new byte[(int)len];
        Array.Copy(gamedata, i, payload, 0, payload.Length);

        foreach (var e in PbReader.ReadMessage(payload, maxDepth: 1, maxFields: 200_000))
        {
            if (e.WireType != 2 || e.Bytes is null) continue;
            int id = -1;
            string? code = null;
            foreach (var f in PbReader.ReadMessage(e.Bytes, maxDepth: 1, maxFields: 64))
            {
                if (f.WireType == 0) id = (int)f.IntValue;
                else if (f.WireType == 2 && f.Bytes is { } seg) code = Encoding.UTF8.GetString(seg);
            }

            if (id >= 0 && code is { Length: > 0 }) reg._idToCode[id] = code;
        }

        return reg;
    }

    private static int IndexOf(byte[] hay, string needle)
    {
        var n = Encoding.ASCII.GetBytes(needle);
        for (int i = 0; i <= hay.Length - n.Length; i++)
        {
            int j = 0;
            while (j < n.Length && hay[i + j] == n[j]) j++;
            if (j == n.Length) return i;
        }

        return -1;
    }
}
