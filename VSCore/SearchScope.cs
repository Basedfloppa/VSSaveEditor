using System.Globalization;
using System.Linq;

namespace VSCore;

/// <summary>
///     How far a tab's search reaches: the whole save, one map area, several areas at once, a rectangle of chunks,
///     or one chunk column.
///     The scope is 2D and has no Y borders: it is a set of (cx,cz) columns, and every Y level of those columns is inside.
///
///     Not every record of a save can be placed in such a scope. Playerdata has no coordinates at all — the player's
///     backpack is not where the client happens to stand, it travels with the player — so a chunk scope leaves it out
///     (<see cref="InventoryBrowser.Build" />), and only the explicit <see cref="UnplacedOnly" /> scope selects it alone.
///     Regions, mapchunks and gamedata are addressed by region/mapchunk number, not by a chunk, and stay
///     "whole save only": a scope either contains such a record entirely or not at all, never half of it.
/// </summary>
public sealed class SearchScope
{
    public enum ScopeKind
    {
        /// <summary>No restriction - the behaviour every tab had before the scope existed.</summary>
        Everything,
        Area,
        Rectangle,
        Column,

        /// <summary>Two or more whole areas at once: their union, not the box around them (see <see cref="ForAreas" />).</summary>
        Areas,

        /// <summary>Only the records without a position (playerdata): the one part of the save a chunk scope cannot narrow.</summary>
        Unplaced
    }

    private int _minCx, _minCz, _maxCx, _maxCz;

    // The chosen areas of an area scope (one or several), whose union IS the scope. A rectangle cannot express
    // several of them: two distant areas would take the gap between them along, and the gap belongs to no area.
    private IReadOnlyList<ChunkArea>? _members;

    private SearchScope(ScopeKind kind, string label)
    {
        Kind = kind;
        Label = label;
    }

    public ScopeKind Kind { get; }

    /// <summary>Short label for a toolbar/pill: "whole save", "area 1 of 3 — X 512000..517119, Z …", "15991, 15983".</summary>
    public string Label { get; }

    /// <summary>The scope is nameable but contains nothing in this save (for example a rectangle over a gap between areas).</summary>
    public string? EmptyReason { get; set; }

    public static SearchScope Everything { get; } = new(ScopeKind.Everything, "the whole save");

    public static SearchScope UnplacedOnly { get; } = new(ScopeKind.Unplaced, "records without a position (playerdata)");

    public static SearchScope ForArea(ChunkArea area, int total)
        => new(ScopeKind.Area, area.Label(total))
        {
            _members = new[] { area },
            _minCx = area.MinCx, _minCz = area.MinCz, _maxCx = area.MaxCx, _maxCz = area.MaxCz
        };

    /// <summary>
    ///     Several whole areas at once. The scope is their union: a chunk is inside when it is inside any one of
    ///     them, so the empty gap between two distant areas is outside (a box around both would take it in and
    ///     make the tab search - and count - places the user did not pick).
    ///     One area is not a set: it goes through <see cref="ForArea" /> so that "area=2" and "areas=2" mean the
    ///     same scope, with the same label.
    /// </summary>
    public static SearchScope ForAreas(IReadOnlyList<ChunkArea> areas, int total)
    {
        if (areas.Count == 0) return Everything;
        if (areas.Count == 1) return ForArea(areas[0], total);

        var ordered = areas.OrderBy(a => a.Index).ToArray();
        var numbers = string.Join(", ", ordered.Select(a => a.Index + 1));
        long columns = ordered.Sum(a => (long)a.Columns);
        return new SearchScope(ScopeKind.Areas, $"areas {numbers} of {total} — {columns:N0} chunks")
        {
            _members = ordered,
            _minCx = ordered.Min(a => a.MinCx),
            _minCz = ordered.Min(a => a.MinCz),
            _maxCx = ordered.Max(a => a.MaxCx),
            _maxCz = ordered.Max(a => a.MaxCz)
        };
    }

    /// <summary>Chunk columns, inclusive on both ends; the ends are swapped when they come in reversed.</summary>
    public static SearchScope ForRectangle(int cx1, int cz1, int cx2, int cz2)
    {
        int minX = Math.Min(cx1, cx2), maxX = Math.Max(cx1, cx2);
        int minZ = Math.Min(cz1, cz2), maxZ = Math.Max(cz1, cz2);
        long cols = (long)(maxX - minX + 1) * (maxZ - minZ + 1);
        var label = minX == maxX && minZ == maxZ
            ? $"chunk {minX}, {minZ}"
            : $"chunks X {minX}..{maxX}, Z {minZ}..{maxZ} ({cols:N0})";
        return new SearchScope(minX == maxX && minZ == maxZ ? ScopeKind.Column : ScopeKind.Rectangle, label)
        {
            _minCx = minX, _minCz = minZ, _maxCx = maxX, _maxCz = maxZ
        };
    }

    public static SearchScope ForColumn(int cx, int cz) => ForRectangle(cx, cz, cx, cz);

    public int MinCx => _minCx;
    public int MinCz => _minCz;
    public int MaxCx => _maxCx;
    public int MaxCz => _maxCz;

    /// <summary>Whether the scope has a rectangle of chunks at all.</summary>
    public bool HasRegion => Kind is ScopeKind.Area or ScopeKind.Areas or ScopeKind.Rectangle or ScopeKind.Column;

    /// <summary>The chosen areas of an area scope — one for <see cref="ScopeKind.Area" />, several for
    /// <see cref="ScopeKind.Areas" />, empty for every other kind. The tab reconciles its ticks against this.</summary>
    public IReadOnlyList<ChunkArea> Areas => _members ?? (IReadOnlyList<ChunkArea>)Array.Empty<ChunkArea>();

    /// <summary>A named scope that turns out to contain nothing: the tab then shows "0 of N" with the reason instead
    /// of silently falling back to the whole save.</summary>
    public bool IsEmpty => EmptyReason is not null;

    public bool IsEverything => Kind == ScopeKind.Everything;

    /// <summary>
    ///     The restriction a chunk-placed index (chunks, entities, inventories) must apply: null when there is none.
    ///     This is not just "this" with a null check: <see cref="UnplacedOnly" /> is not null either, because null means
    ///     "no restriction" and the index would take the whole save.
    /// </summary>
    public SearchScope? ChunkScopeOf => Kind switch
    {
        ScopeKind.Everything => null,
        ScopeKind.Unplaced => UnplacedOnly,
        _ => this
    };

    /// <summary>Free-form scope text: "all", "unplaced", "cx,cz" or "cx1,cz1,cx2,cz2".</summary>
    public static SearchScope Parse(string? text)
    {
        var t = (text ?? "").Trim();
        string tail;
        if (t.StartsWith("area=", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("areas=", StringComparison.OrdinalIgnoreCase))
        {
            // the numbering of areas depends on the save, so a label cannot be parsed back into rectangles;
            // the caller resolves "area=N" and "areas=1,3" against its own area list (ForArea / ForAreas)
            throw new FormatException("'area=' and 'areas=' scopes are resolved by the caller against the area list, not parsed");
        }

        if (t.StartsWith("rect=", StringComparison.OrdinalIgnoreCase)) tail = t[5..];
        else if (t.StartsWith("chunks=", StringComparison.OrdinalIgnoreCase)) tail = t[7..];
        else if (t.StartsWith("chunk=", StringComparison.OrdinalIgnoreCase)) tail = t[6..];
        else tail = t;

        if (tail.Length == 0 || tail.Equals("*", StringComparison.OrdinalIgnoreCase) ||
            tail.Equals("all", StringComparison.OrdinalIgnoreCase) ||
            tail.Equals("save", StringComparison.OrdinalIgnoreCase) ||
            tail.Equals("everything", StringComparison.OrdinalIgnoreCase))
            return Everything;
        if (tail.Equals("unplaced", StringComparison.OrdinalIgnoreCase) ||
            tail.Equals("playerdata", StringComparison.OrdinalIgnoreCase) ||
            tail.Equals("players", StringComparison.OrdinalIgnoreCase))
            return UnplacedOnly;

        var parts = tail.Split(new[] { ',', ' ', ':', '/', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 4
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b)
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)
            && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var d))
            return ForRectangle(a, b, c, d);
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cx)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cz))
            return ForColumn(cx, cz);
        throw new FormatException($"'{text}' is not a scope: use all, unplaced, cx,cz or cx1,cz1,cx2,cz2");
    }

    /// <summary>
    ///     Whether the chunk column (cx,cz) is inside the scope. A chunk-sized record (<c>ChunkScanData</c>) is inside
    ///     exactly when its own column is: the scope never cuts a chunk in half. An empty rectangle and
    ///     <see cref="UnplacedOnly" /> both contain nothing — the equality below is not a rectangle test for them.
    ///     For <see cref="ScopeKind.Areas" /> a column is inside when it is inside any one of the chosen areas; the
    ///     union bounding box is only a cheap rejection first, because every chunk of the save is tested here.
    /// </summary>
    public bool ContainsChunk(int cx, int cz)
    {
        if (Kind == ScopeKind.Unplaced) return false;
        if (!HasRegion) return true;
        if (cx < _minCx || cx > _maxCx || cz < _minCz || cz > _maxCz) return false;
        if (_members is null) return true;
        foreach (var area in _members)
            if (area.ContainsChunk(cx, cz)) return true;
        return false;
    }

    /// <summary>
    ///     Whether a record with exact world coordinates (an entity, a block entity, a container) is inside.
    ///     The whole 32×32 column belongs to the scope, and every Y level of it: the scope is 2D by design.
    ///     Floor, not a cast — negative world coordinates would otherwise round towards zero and land in the wrong chunk.
    /// </summary>
    public bool ContainsPoint(double wx, double wz)
        => Kind != ScopeKind.Unplaced && ContainsChunk((int)Math.Floor(wx / 32.0), (int)Math.Floor(wz / 32.0));

    /// <summary>When a tab has nothing to show because the scope is empty or excludes everything, it says why.</summary>
    public string DescribeExclusion(string what)
        => IsEmpty ? $"{EmptyReason} — nothing to search for {what}" : $"{Label} contains no {what}";

    public override string ToString() => Label;
}
