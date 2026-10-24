using System.Globalization;
using System.Text.RegularExpressions;

namespace VSCore;

/// <summary>
///     One chunk column (cx, cz) with all of its y columns — what the "Chunks" tab
///     shows and edits. Previously the chunk list was assembled right in the window (a `ComboBox` with all
///     world coordinates) and was built in full on a large save, while the search went through four categories
///     at once. Here the same thing is done by a model: a column knows its counters and can answer a query.
/// </summary>
public sealed class ChunkColumnInfo
{
    public int Cx { get; init; }
    public int Cz { get; init; }

    /// <summary>The y columns of this column, in ascending Y order.</summary>
    public List<ChunkScanData> Columns { get; } = new();

    public int Entities { get; private set; }
    public int BlockEntities { get; private set; }
    public long SolidCells { get; private set; }
    public long FluidCells { get; private set; }
    public long AirCells { get; private set; }
    public int MinY { get; private set; }
    public int MaxY { get; private set; }

    public string CoordText => $"{Cx}, {Cz}";

    /// <summary>List row summary: it identifies the chunk without opening it.</summary>
    public string Summary { get; private set; } = "";

    /// <summary>Whether the column holds anything besides air (per column, not per row).</summary>
    public bool IsEmpty => SolidCells == 0 && FluidCells == 0;

    /// <summary>Computes the counters and builds <see cref="Summary" />. Called after the column is filled.</summary>
    public void Recompute()
    {
        Columns.Sort((a, b) => a.ChunkY.CompareTo(b.ChunkY));
        Entities = 0;
        BlockEntities = 0;
        SolidCells = FluidCells = AirCells = 0;
        foreach (var c in Columns)
        {
            // "active" entities: a bare node with no kind and no attributes has nothing to show on the card
            Entities += c.Entities.Count(e => e.HasInfo);
            BlockEntities += c.BlockEntities.Count;
            SolidCells += c.SolidCells;
            FluidCells += c.FluidCells;
            AirCells += c.AirCells;
        }

        MinY = Columns.Count > 0 ? Columns[0].ChunkY : 0;
        MaxY = Columns.Count > 0 ? Columns[^1].ChunkY : 0;
        Summary = $"{CoordText} · {Columns.Count} y-col · {Entities} ent · {BlockEntities} block ent"
                  + $" · {SolidCells:N0} blocks";
    }

    /// <summary>
    ///     Whether the column contains the substring: entity kinds/sex-age/fields, block entity types and codes,
    ///     block codes. No allocations — case-insensitive comparison straight over the model strings.
    /// </summary>
    public bool MatchesContent(string needle)
    {
        foreach (var c in Columns)
        {
            foreach (var e in c.Entities)
            {
                if (Hit(e.Species) || Hit(e.Kind) || Hit(e.SexAge) || Hit(e.AttributeSummary)) return true;
                foreach (var f in e.Fields)
                    if (Hit(f))
                        return true;
            }

            foreach (var be in c.BlockEntities)
            {
                if (Hit(be.Type) || Hit(be.CodeSummary)) return true;
                foreach (var t in be.Tokens)
                    if (Hit(t))
                        return true;
            }

            foreach (var code in c.BlockCodeCounts.Keys)
                if (Hit(code))
                    return true;
        }

        return false;

        bool Hit(string? s) => s is { Length: > 0 } && s.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
///     A parsed query for the chunk list. Terms are combined with AND; each term is a coordinate,
///     a range, a counter, a Y layer or a content substring. Parsing is separate from the model, so
///     it is covered by tests without UI.
///
///     Grammar (case-insensitive; terms are separated by spaces and commas):
///       coordinates  15991 15983 | 15991,15983 | x15991 | z15983 | cx15991 cz15983
///       range        15991..16000 (any axis) | cx15991..16000 | cz15983..15990
///       counters     ent&gt;3 | entities&gt;=2 | be&gt;0 | blocks&gt;100 | yc&gt;1 | raw&gt;4096
///       layer        y110 (a y column exists at this level)
///       empty        empty (the column has no solid or liquid cells)
///       content      wolf | ore- | chest — substring over kinds, fields, block entities and block codes
/// </summary>
public sealed class ChunkQuery
{
    private enum Kind
    {
        CoordX, CoordZ, RangeX, RangeZ, RangeAny, MinCount, MaxCount, LayerY, Empty, Content
    }

    private readonly record struct Term(Kind Kind, int A, int B, string Text);

    private static readonly Regex RangeRe = new(@"^(cx|cz|x|z)?(\d+)\.\.(\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AxisRe = new(@"^(cx|cz|x|z)(\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CountRe = new(@"^(ent|entities|entity|be|blockent|blockentity|blocks?|yc|ycols?|raw)(>=|<=|>|<|=)(\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LayerRe = new(@"^(y|cy)(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly List<Term> _terms = new();
    private readonly List<string> _content = new();

    private ChunkQuery(string text)
    {
        Text = text;
    }

    public string Text { get; }
    public string? Error { get; private set; }
    public bool IsEmpty => _terms.Count == 0 && _content.Count == 0;

    /// <summary>Parses the query. <see cref="Error" /> is filled in on an unparsable counter, and parsing does not throw.</summary>
    public static ChunkQuery Parse(string? text)
    {
        var raw = (text ?? "").Trim();
        var q = new ChunkQuery(raw);
        if (raw.Length == 0) return q;

        var tokens = raw.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];

            if (RangeRe.Match(t) is { Success: true } rm)
            {
                var lo = int.Parse(rm.Groups[2].Value, CultureInfo.InvariantCulture);
                var hi = int.Parse(rm.Groups[3].Value, CultureInfo.InvariantCulture);
                if (lo > hi) (lo, hi) = (hi, lo);
                var axis = rm.Groups[1].Value.ToLowerInvariant();
                q._terms.Add(new Term(axis switch
                {
                    "cx" or "x" => Kind.RangeX,
                    "cz" or "z" => Kind.RangeZ,
                    _ => Kind.RangeAny
                }, lo, hi, t));
                continue;
            }

            if (CountRe.Match(t) is { Success: true } cm)
            {
                var key = cm.Groups[1].Value.ToLowerInvariant();
                var op = cm.Groups[2].Value;
                if (!int.TryParse(cm.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                {
                    q.Error = $"bad number in '{t}'";
                    continue;
                }

                var isMin = op is ">" or ">=" or "=";
                // "greater than N" and "less than N" are strict: we shift the bound so that matching is a single comparison
                var bound = op switch
                {
                    ">" => v + 1,
                    "<" => v - 1,
                    _ => v
                };
                q._terms.Add(new Term(isMin ? Kind.MinCount : Kind.MaxCount, Field(key), bound, t));
                continue;
            }

            if (LayerRe.Match(t) is { Success: true } lm)
            {
                q._terms.Add(new Term(Kind.LayerY, int.Parse(lm.Groups[2].Value, CultureInfo.InvariantCulture), 0, t));
                continue;
            }

            if (t.Equals("empty", StringComparison.OrdinalIgnoreCase))
            {
                q._terms.Add(new Term(Kind.Empty, 0, 0, t));
                continue;
            }

            if (AxisRe.Match(t) is { Success: true } am)
            {
                var axis = am.Groups[1].Value.ToLowerInvariant();
                var v = int.Parse(am.Groups[2].Value, CultureInfo.InvariantCulture);
                q._terms.Add(new Term(axis is "cx" or "x" ? Kind.CoordX : Kind.CoordZ, v, 0, t));
                continue;
            }

            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bare))
            {
                // Two numbers in a row are a coordinate pair (15991 15983), the way the save itself writes them.
                if (i + 1 < tokens.Length
                    && int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bare2))
                {
                    q._terms.Add(new Term(Kind.CoordX, bare, 0, t));
                    q._terms.Add(new Term(Kind.CoordZ, bare2, 0, tokens[i + 1]));
                    i++;
                }
                else
                {
                    // a single number is a coordinate on any axis: searching "the chunk at X=15991" is just as legitimate
                    q._terms.Add(new Term(Kind.RangeAny, bare, bare, t));
                }

                continue;
            }

            q._content.Add(t.ToLowerInvariant());
        }

        return q;
    }

    /// <summary>The number a counter refers to: 0 — entities, 1 — block entities, 2 — blocks, 3 — y columns, 4 — raw blob.</summary>
    private static int Field(string key) => key switch
    {
        "ent" or "entities" or "entity" => 0,
        "be" or "blockent" or "blockentity" => 1,
        "yc" or "ycol" or "ycols" => 3,
        "raw" => 4,
        _ => 2
    };

    public bool Matches(ChunkColumnInfo c)
    {
        foreach (var t in _terms)
        {
            var ok = t.Kind switch
            {
                Kind.CoordX => c.Cx == t.A,
                Kind.CoordZ => c.Cz == t.A,
                Kind.RangeX => c.Cx >= t.A && c.Cx <= t.B,
                Kind.RangeZ => c.Cz >= t.A && c.Cz <= t.B,
                Kind.RangeAny => (c.Cx >= t.A && c.Cx <= t.B) || (c.Cz >= t.A && c.Cz <= t.B),
                Kind.LayerY => c.MinY <= t.A && t.A <= c.MaxY,
                Kind.Empty => c.IsEmpty,
                Kind.MinCount => Count(t.A) >= t.B,
                Kind.MaxCount => Count(t.A) <= t.B,
                _ => true
            };
            if (!ok) return false;
        }

        foreach (var needle in _content)
            if (!c.MatchesContent(needle))
                return false;
        return true;

        long Count(int what) => what switch
        {
            0 => c.Entities,
            1 => c.BlockEntities,
            3 => c.Columns.Count,
            4 => c.Columns.Sum(x => (long)(x.Raw?.Length ?? 0)),
            _ => c.SolidCells + c.FluidCells
        };
    }

    /// <summary>Human-readable rendering of the query — shown next to the list.</summary>
    public string Describe()
    {
        if (IsEmpty) return "no filter";
        var parts = new List<string>();
        foreach (var t in _terms)
            parts.Add(t.Kind switch
            {
                Kind.CoordX => $"X={t.A}",
                Kind.CoordZ => $"Z={t.A}",
                Kind.RangeX => $"X {t.A}..{t.B}",
                Kind.RangeZ => $"Z {t.A}..{t.B}",
                Kind.RangeAny => $"{t.A}..{t.B}",
                Kind.LayerY => $"y{t.A}",
                Kind.Empty => "empty",
                Kind.MinCount => $"{Counter(t.A)} >= {t.B}",
                _ => $"{Counter(t.A)} <= {t.B}"
            });
        foreach (var c in _content) parts.Add($"\"{c}\"");
        return string.Join(" · ", parts);
    }

    private static string Counter(int what) => what switch
    {
        0 => "entities",
        1 => "block entities",
        3 => "y-columns",
        4 => "raw bytes",
        _ => "blocks"
    };
}

/// <summary>
///     The list of chunks (columns) for the "Chunks" tab: the index is built once per scan,
///     filtering is done by a query, and an empty query suggests the "most populated" ones.
/// </summary>
public static class ChunkBrowser
{
    /// <summary>How many rows to hand to the list: the rest only as a count (the ListBox is virtualised, but 70k rows is no longer "search").</summary>
    public const int DefaultLimit = 400;

    /// <summary>Builds the (cx, cz) columns grouped from the flat list of y columns of the scan.</summary>
    public static List<ChunkColumnInfo> Build(SaveScanResult r)
    {
        var map = new Dictionary<(int X, int Z), ChunkColumnInfo>();
        foreach (var cd in r.Chunks)
        {
            if (!map.TryGetValue((cd.ChunkX, cd.ChunkZ), out var col))
            {
                col = new ChunkColumnInfo { Cx = cd.ChunkX, Cz = cd.ChunkZ };
                map[(cd.ChunkX, cd.ChunkZ)] = col;
            }

            col.Columns.Add(cd);
        }

        var list = map.Values.ToList();
        foreach (var col in list) col.Recompute();
        list.Sort((a, b) => a.Cx != b.Cx ? a.Cx.CompareTo(b.Cx) : a.Cz.CompareTo(b.Cz));
        return list;
    }

    /// <summary>
    ///     Applies the query. An empty query means "the most interesting first": chunks with entities and
    ///     block entities, then by coordinates; that way the list is useful even without any input.
    /// </summary>
    public static List<ChunkColumnInfo> Filter(IEnumerable<ChunkColumnInfo> all, ChunkQuery query, int limit = DefaultLimit)
    {
        var res = new List<ChunkColumnInfo>();
        if (query.IsEmpty)
        {
            foreach (var c in all.OrderByDescending(c => c.Entities)
                         .ThenByDescending(c => c.BlockEntities)
                         .ThenBy(c => c.Cx).ThenBy(c => c.Cz))
            {
                if (res.Count >= limit) break;
                if (c.Entities == 0 && c.BlockEntities == 0) break; // the rest are only empty ones — we do not show them
                res.Add(c);
            }

            return res;
        }

        foreach (var c in all)
        {
            if (res.Count >= limit) break;
            if (query.Matches(c)) res.Add(c);
        }

        return res;
    }

    /// <summary>How many columns match the query (without trimming by <paramref name="limit" />).</summary>
    public static int Count(IEnumerable<ChunkColumnInfo> all, ChunkQuery query)
    {
        if (query.IsEmpty)
            return all.Count(c => c.Entities > 0 || c.BlockEntities > 0);
        var n = 0;
        foreach (var c in all)
            if (query.Matches(c))
                n++;
        return n;
    }
}
