using System.Globalization;

namespace VSCore;

/// <summary>Which sources to show on the Items tab.</summary>
public enum InventorySourceFilter
{
    All,
    Players,
    Entities,
    BlockEntities
}

/// <summary>
///     Inventory search row: one item stack plus its owner/container.
///     The row copies neither the tree nor the blob — only references to the scan model and to the
///     already parsed scalars, so 35,000 stacks do not turn into 35,000 copies of text.
/// </summary>
public sealed class ItemStackRow
{
    public InventoryInfo Inventory { get; }
    public ItemStackInfo Stack { get; }

    public ItemStackRow(InventoryInfo inventory, ItemStackInfo stack)
    {
        Inventory = inventory;
        Stack = stack;
    }

    public string Code => Stack.DisplayCode;
    public int Count => Stack.Count;
    public string OwnerLabel => Inventory.OwnerLabel.Length > 0 ? Inventory.OwnerLabel : Inventory.OwnerCode;
    public string OwnerCode => Inventory.OwnerCode;
    public string OwnerBadge => Inventory.OwnerBadge;
    public string Container => Inventory.ContainerLabel;
    public string ContainerName => Inventory.Name;
    public string Slot => Stack.Slot;
    public string ChunkText => Inventory.ChunkText;
    public string PositionText => Inventory.PositionText;
    public string InventoryKey => Inventory.Key;
    public string Attributes => Stack.AttributeText;

    public bool IsPlayer => Inventory.OwnerKind == InventoryOwnerKind.Player;
    public bool IsEntity => Inventory.OwnerKind == InventoryOwnerKind.Entity;
    public bool IsBlockEntity => Inventory.OwnerKind == InventoryOwnerKind.BlockEntity;

    /// <summary>What is visible in the list: the item, the count, who holds it and where it is kept.</summary>
    public string Display
    {
        get
        {
            var where = ChunkText.Length > 0 ? " — " + ChunkText : "";
            var pos = PositionText.Length > 0 ? " " + PositionText : "";
            return $"{Count} × {Code} — {OwnerLabel} {OwnerBadge} · {Container}[{Slot}]{where}{pos}";
        }
    }

    private string? _haystack;

    /// <summary>Everything the free-text search looks at. Built once per row and only when searching.</summary>
    public string Haystack => _haystack ??= string.Join(' ',
        Code, Stack.Code, Stack.FallbackCode, OwnerLabel, OwnerCode, Inventory.PlayerUid ?? "",
        Container, ContainerName, Inventory.ContainerCode, Slot, InventoryKey, ChunkText, PositionText,
        Stack.AttributeText);

    public bool Matches(InventoryQuery query)
    {
        foreach (var term in query.Terms)
            if (!term.Matches(this))
                return false;
        return true;
    }
}

/// <summary>
///     Parsing of the inventory search string. Plain terms are combined with AND:
///     <c>diamond</c>, <c>owner:Moonlight</c>, <c>container:chest</c>, <c>slot:3</c>,
///     <c>count&gt;=10</c>, <c>chunk:15991,15983</c>, <c>attr:condition</c>,
///     <c>key:e:…</c> (the exact inventory address for navigation from the cards).
/// </summary>
public sealed class InventoryQuery
{
    private readonly List<Term> _terms = new();

    public IReadOnlyList<Term> Terms => _terms;
    public bool IsEmpty => _terms.Count == 0;
    public string Error { get; private set; } = "";

    public static InventoryQuery Parse(string? text)
    {
        var q = new InventoryQuery();
        foreach (var raw in Tokenize(text ?? ""))
        {
            var token = raw.Trim('"');
            if (token.Length == 0) continue;

            if (TryPrefix(token, "item:", out var item) || TryPrefix(token, "code:", out item))
            {
                q._terms.Add(Term.ForText(TermKind.Item, item));
                continue;
            }

            if (TryPrefix(token, "owner:", out var owner) || TryPrefix(token, "player:", out owner))
            {
                q._terms.Add(Term.ForText(TermKind.Owner, owner));
                continue;
            }

            if (TryPrefix(token, "container:", out var container))
            {
                q._terms.Add(Term.ForText(TermKind.Container, container));
                continue;
            }

            if (TryPrefix(token, "slot:", out var slot))
            {
                q._terms.Add(Term.ForText(TermKind.Slot, slot));
                continue;
            }

            if (TryPrefix(token, "attr:", out var attr))
            {
                var eq = attr.IndexOf('=');
                q._terms.Add(eq < 0
                    ? Term.ForText(TermKind.AttrKey, attr)
                    : Term.ForAttr(attr[..eq], attr[(eq + 1)..]));
                continue;
            }

            if (TryPrefix(token, "key:", out var key))
            {
                q._terms.Add(Term.ForText(TermKind.Key, key));
                continue;
            }

            if (TryPrefix(token, "chunk:", out var chunk))
            {
                if (TryParseChunk(chunk, out var cx, out var cz))
                    q._terms.Add(Term.ForChunk(cx, cz));
                else
                    q.Error = $"bad chunk '{chunk}' (expected x,z)";
                continue;
            }

            if (TryCount(token, out var op, out var number))
            {
                q._terms.Add(Term.ForCount(op, number));
                continue;
            }

            q._terms.Add(Term.ForText(TermKind.Text, token));
        }

        return q;
    }

    /// <summary>Human-readable description for the summary: it does not retell the whole row, but names the fields.</summary>
    public string Describe()
    {
        if (_terms.Count == 0) return "";
        return string.Join(" · ", _terms.Select(t => t.Describe()));
    }

    private static bool TryPrefix(string token, string prefix, out string value)
    {
        value = "";
        if (!token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        value = token[prefix.Length..].Trim('"');
        return value.Length > 0;
    }

    private static bool TryCount(string token, out CountOp op, out int number)
    {
        op = CountOp.Equal;
        number = 0;
        string rest;
        if (token.StartsWith("count>=", StringComparison.OrdinalIgnoreCase)) { op = CountOp.GreaterEqual; rest = token[7..]; }
        else if (token.StartsWith("count<=", StringComparison.OrdinalIgnoreCase)) { op = CountOp.LessEqual; rest = token[7..]; }
        else if (token.StartsWith("count>", StringComparison.OrdinalIgnoreCase)) { op = CountOp.Greater; rest = token[6..]; }
        else if (token.StartsWith("count<", StringComparison.OrdinalIgnoreCase)) { op = CountOp.Less; rest = token[6..]; }
        else if (token.StartsWith("count=", StringComparison.OrdinalIgnoreCase)) { op = CountOp.Equal; rest = token[6..]; }
        else return false;

        return int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    }

    private static bool TryParseChunk(string s, out int cx, out int cz)
    {
        cx = cz = 0;
        var parts = s.Split(new[] { ',', ':', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out cx)
               && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out cz);
    }

    /// <summary>Whitespace splitting with quote support: <c>owner:"Moon Light"</c>.</summary>
    private static List<string> Tokenize(string text)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                current.Append(c);
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else current.Append(c);
        }

        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    public enum CountOp
    {
        Equal,
        Greater,
        GreaterEqual,
        Less,
        LessEqual
    }

    public enum TermKind
    {
        Text,
        Item,
        Owner,
        Container,
        Slot,
        AttrKey,
        Key,
        Chunk,
        Count
    }

    public sealed class Term
    {
        public TermKind Kind { get; private init; }
        public string Value { get; private init; } = "";
        public string Value2 { get; private init; } = "";
        public int Number { get; private init; }
        public CountOp Op { get; private init; }
        public int ChunkX { get; private init; }
        public int ChunkZ { get; private init; }

        public static Term ForText(TermKind kind, string value) => new() { Kind = kind, Value = value };
        public static Term ForAttr(string key, string value) => new() { Kind = TermKind.AttrKey, Value = key, Value2 = value };
        public static Term ForChunk(int cx, int cz) => new() { Kind = TermKind.Chunk, ChunkX = cx, ChunkZ = cz };
        public static Term ForCount(CountOp op, int n) => new() { Kind = TermKind.Count, Op = op, Number = n };

        public bool Matches(ItemStackRow row)
        {
            switch (Kind)
            {
                case TermKind.Text:
                    return row.Haystack.Contains(Value, StringComparison.OrdinalIgnoreCase);
                case TermKind.Item:
                    return row.Code.Contains(Value, StringComparison.OrdinalIgnoreCase);
                case TermKind.Owner:
                    return Contains(row.OwnerLabel, Value) || Contains(row.OwnerCode, Value)
                           || Contains(row.Inventory.PlayerUid, Value)
                           || Contains(row.Inventory.ContainerCode, Value)
                           || Contains(row.Inventory.Name, Value);
                case TermKind.Container:
                    return Contains(row.Container, Value) || Contains(row.ContainerName, Value)
                           || Contains(row.Inventory.ContainerCode, Value) || Contains(row.OwnerCode, Value);
                case TermKind.Slot:
                    return string.Equals(row.Slot, Value, StringComparison.OrdinalIgnoreCase)
                           || row.Slot.Contains(Value, StringComparison.OrdinalIgnoreCase);
                case TermKind.AttrKey:
                    return row.Stack.Attributes.Any(a =>
                        a.Key.Contains(Value, StringComparison.OrdinalIgnoreCase)
                        && (Value2.Length == 0 || a.Value.Contains(Value2, StringComparison.OrdinalIgnoreCase)));
                case TermKind.Key:
                    return row.InventoryKey.Contains(Value, StringComparison.OrdinalIgnoreCase);
                case TermKind.Chunk:
                    return row.Inventory.HasChunk && row.Inventory.ChunkX == ChunkX && row.Inventory.ChunkZ == ChunkZ;
                case TermKind.Count:
                    return Op switch
                    {
                        CountOp.Equal => row.Count == Number,
                        CountOp.Greater => row.Count > Number,
                        CountOp.GreaterEqual => row.Count >= Number,
                        CountOp.Less => row.Count < Number,
                        _ => row.Count <= Number
                    };
                default:
                    return true;
            }
        }

        public string Describe() => Kind switch
        {
            TermKind.Item => $"item \"{Value}\"",
            TermKind.Owner => $"owner \"{Value}\"",
            TermKind.Container => $"container \"{Value}\"",
            TermKind.Slot => $"slot \"{Value}\"",
            TermKind.AttrKey => Value2.Length > 0 ? $"attr {Value}={Value2}" : $"attr {Value}",
            TermKind.Key => $"key \"{Value}\"",
            TermKind.Chunk => $"chunk {ChunkX},{ChunkZ}",
            TermKind.Count => $"count {Op switch { CountOp.Equal => "=", CountOp.Greater => ">", CountOp.GreaterEqual => ">=", CountOp.Less => "<", _ => "<=" }} {Number}",
            _ => $"\"{Value}\""
        };

        private static bool Contains(string? hay, string needle)
            => hay is { Length: > 0 } && hay.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Flat index of item stacks for the Items tab.</summary>
public static class InventoryBrowser
{
    public static List<ItemStackRow> Build(SaveScanResult? r) => Build(r, null);

    /// <summary>
    ///     Stacks of the inventories inside the scope. Containers and creature backpacks are placed exactly by their
    ///     <c>pos.x/y/z</c>; the chunk of the record is the fallback for trees without those fields.
    ///
    ///     Playerdata is a deliberate exception: it has no position — the backpack rides with the player — so it is kept
    ///     only in a scope that contains no placed records at all (<see cref="SearchScope.UnplacedOnly" />) and left out
    ///     of a chunk scope. Anything else would answer "the player's items are in this chunk" when they are not.
    /// </summary>
    public static List<ItemStackRow> Build(SaveScanResult? r, SearchScope? scope)
    {
        var rows = new List<ItemStackRow>(r?.ItemStackCount ?? 0);
        if (r is null) return rows;
        var unplacedOnly = scope?.Kind == SearchScope.ScopeKind.Unplaced;
        foreach (var inv in r.Inventories)
        {
            if (scope is not null)
            {
                if (inv.OwnerKind == InventoryOwnerKind.Player)
                {
                    if (!unplacedOnly) continue; // the backpack travels with the player, not with the chunk
                }
                else if (unplacedOnly)
                {
                    continue;
                }
                else
                {
                    // the block position is the corner of the block; +0.5 puts the point inside the block itself
                    var inside = inv is { X: { } x, Z: { } z }
                        ? scope.ContainsPoint(x + 0.5, z + 0.5)
                        : scope.ContainsChunk(inv.ChunkX, inv.ChunkZ);
                    if (!inside) continue;
                }
            }

            foreach (var stack in inv.Items)
                rows.Add(new ItemStackRow(inv, stack));
        }

        SortRows(rows);
        return rows;
    }

    /// <summary>
    ///     Record-level check for the "Items in scope" run: a player inventory is inside a scope without a rectangle -
    ///     and only then, since playerdata cannot be narrowed by a chunk, a container must stand in a chunk of the scope.
    /// </summary>
    public static bool InventoryInRecordScope(InventoryInfo inv, SearchScope? scope)
    {
        if (scope is null) return true;
        if (inv.OwnerKind == InventoryOwnerKind.Player) return !scope.HasRegion;
        return scope.ContainsChunk(inv.ChunkX, inv.ChunkZ);
    }

    /// <summary>
    ///     The saved <c>key:</c> prefix of one inventory, so that the scoped lists can be assembled from per-record
    ///     loads and still have an address the "key:" filter understands. <c>-1</c> for
    ///     <c>Position</c>/<c>Occurrence</c> when the record's blob is not in the scan - that inventory then
    ///     cannot be opened in the editor and deliberately gets no key.
    /// </summary>
    public static string ScopedKey(InventoryInfo inv)
    {
        if (inv.OwnerKind == InventoryOwnerKind.Player) return $"p:{inv.PlayerId}:{inv.Name}";
        var pos = inv.OwnerKind == InventoryOwnerKind.Entity
            ? inv.Position
            : inv.OwnerKind == InventoryOwnerKind.BlockEntity ? inv.Position : -1;
        return pos >= 0 && inv.Occurrence >= 0
            ? $"{(inv.OwnerKind == InventoryOwnerKind.Entity ? "e" : "b")}:{pos}:{inv.Occurrence}:{inv.Name}"
            : "";
    }

    /// <summary>
    ///     The "Players" source of a scope lists the players themselves: all of them — playerdata has no position, so
    ///     there is nothing to narrow by the 2D rectangle.
    /// </summary>
    public static List<ItemStackRow> FilterPlayers(IEnumerable<ItemStackRow> rows)
        => rows.Where(r => r.IsPlayer).ToList();

    /// <summary>
    ///     The "Items in scope" run: the scan keeps playerdata inventories without the player's uid (for example to find
    ///     a player by uid, or to open the right <c>key:</c>), and <see cref="Build" /> needs rows to attach records to.
    ///     With a scope the players' items are parsed from the save's own playerdata here, the uid comes from the row,
    ///     and placed inventories keep the addresses the holder can open. A damaged row is skipped, as in the scan.
    /// </summary>
    public static List<ItemStackRow> AddPlayerInventories(List<ItemStackRow> rows, IEnumerable<VSSaveFile.PlayerRow> players,
        BlockRegistry? blocks, ItemRegistry? items)
    {
        foreach (var p in players)
        {
            try
            {
                var data = PlayerDataParser.Parse(p.Data ?? Array.Empty<byte>(), p.Id, blocks, items);
                var label = data.CustomName is { Length: > 0 } name ? name : p.Uid ?? ("player #" + p.Id);
                foreach (var inv in data.Inventories)
                {
                    var info = new InventoryInfo
                    {
                        OwnerKind = InventoryOwnerKind.Player,
                        OwnerLabel = label,
                        OwnerCode = p.Uid ?? "",
                        PlayerId = p.Id,
                        PlayerUid = p.Uid,
                        Position = -1,
                        Field = -2,
                        Occurrence = -1,
                        HasChunk = false
                    };
                    AddInv(info, inv.Name ?? "inventory", inv.SlotCount, inv.Items, rows);
                    // a backpack inside a backpack: the scan keeps the nested ones as separate inventories too
                    foreach (var nested in inv.Nested)
                        AddInv(info, nested.Name, nested.SlotCount, nested.Items, rows);
                }
            }
            catch
            {
                // a damaged record must not hide the other players' items
            }
        }

        return rows;
    }

    private static void AddInv(InventoryInfo owner, string name, int slotCount, IEnumerable<ItemStackInfo> stacks,
        List<ItemStackRow> rows)
    {
        var info = new InventoryInfo
        {
            OwnerKind = owner.OwnerKind,
            OwnerLabel = owner.OwnerLabel,
            OwnerCode = owner.OwnerCode,
            PlayerId = owner.PlayerId,
            PlayerUid = owner.PlayerUid,
            Position = owner.Position,
            Field = owner.Field,
            Occurrence = owner.Occurrence,
            HasChunk = false,
            Name = name,
            SlotCount = slotCount
        };
        foreach (var s in stacks)
        {
            info.Items.Add(s);
            rows.Add(new ItemStackRow(info, s));
        }
    }

    /// <summary>Two indexes side by side: the players' inventories first, so identical items do not split a sort run.</summary>
    public static void SortRows(List<ItemStackRow> rows) => rows.Sort(CompareRows);

    private static int CompareRows(ItemStackRow a, ItemStackRow b)
    {
        var c = string.Compare(a.Code, b.Code, StringComparison.OrdinalIgnoreCase);
        if (c != 0) return c;
        c = b.Count.CompareTo(a.Count);
        if (c != 0) return c;
        return string.Compare(a.OwnerLabel, b.OwnerLabel, StringComparison.OrdinalIgnoreCase);
    }

    public static List<ItemStackRow> Filter(IEnumerable<ItemStackRow> rows, InventorySourceFilter source, InventoryQuery query)
    {
        var result = new List<ItemStackRow>();
        foreach (var row in rows)
        {
            var kind = row.Inventory.OwnerKind;
            if (source == InventorySourceFilter.Players && kind != InventoryOwnerKind.Player) continue;
            if (source == InventorySourceFilter.Entities && kind != InventoryOwnerKind.Entity) continue;
            if (source == InventorySourceFilter.BlockEntities && kind != InventoryOwnerKind.BlockEntity) continue;
            if (!row.Matches(query)) continue;
            result.Add(row);
        }

        return result;
    }

    public static int CountInventories(IEnumerable<ItemStackRow> rows)
        => rows.Select(r => r.InventoryKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>Short inventory label for the cards: "backpack (20 slots, 1 item) · …".
    /// Empty inventories are named too — otherwise nothing shows that the container exists at all.</summary>
    public static string Summary(IReadOnlyList<InventoryInfo> inventories, int max = 3)
    {
        if (inventories.Count == 0) return "";
        var parts = inventories.Take(max).Select(i =>
            $"{i.Name} ({i.SlotCount} slot{(i.SlotCount == 1 ? "" : "s")}, {i.Items.Count} item{(i.Items.Count == 1 ? "" : "s")})");
        var text = string.Join(" · ", parts);
        if (inventories.Count > max) text += $" · +{inventories.Count - max} more";
        return text;
    }

    /// <summary>The `key:` filter over all inventories of one record. The card uses it to open Items.</summary>
    public static string KeyPrefix(InventoryOwnerKind kind, long position, int occurrence, long playerId = -1)
        => kind switch
        {
            InventoryOwnerKind.Player => $"key:p:{playerId}:",
            InventoryOwnerKind.Entity => $"key:e:{position}:{occurrence}:",
            _ => $"key:b:{position}:{occurrence}:"
        };
}
