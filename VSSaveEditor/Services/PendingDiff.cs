using System.Text;
using VSCore;

namespace VSSaveEditor;

/// <summary>Differences in one edit area: a chunk (all of its y columns) or a player.</summary>
public sealed class DiffGroup
{
    public string Title { get; init; } = "";
    public long Key { get; init; }
    public bool IsPlayer { get; init; }
    public string? Note { get; set; }
    public List<FieldChange> Rows { get; } = new();
    public int OldBytes { get; set; }
    public int NewBytes { get; set; }
    public int ByteDelta => NewBytes - OldBytes;
}

/// <summary>
///     The "what exactly goes into the working copy" report: compares the accumulated edits with the save data.
///     It reads blobs from the DB, so it is built in the background.
/// </summary>
public sealed class DiffReport
{
    public List<DiffGroup> Groups { get; } = new();

    /// <summary>How many chunk positions and player records are affected (as in the old confirmation).</summary>
    public int ChunkCount { get; set; }

    public int PlayerCount { get; set; }

    public int ChangedValues => Groups.Sum(g => g.Rows.Count);
    public int ByteDelta => Groups.Sum(g => g.ByteDelta);
    public bool IsEmpty => ChunkCount + PlayerCount == 0;

    public string Summary
    {
        get
        {
            if (IsEmpty) return "Nothing pending — there is nothing to apply.";
            var s = $"{ChunkCount} chunk(s) and {PlayerCount} player(s) will be applied to the WORKING COPY; " +
                    $"the original file stays untouched. {ChangedValues} difference(s) shown";
            var delta = ByteDelta;
            if (delta != 0) s += $", {delta:+#;-#;0} bytes in total";
            return s + ".";
        }
    }

    /// <summary>The text report for "Copy as text" / "Export report…".</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Pending changes (applied to the working copy; the original save is not modified)");
        sb.AppendLine(Summary);
        sb.AppendLine();
        foreach (var g in Groups)
        {
            sb.AppendLine($"[{(g.IsPlayer ? "player" : "chunk")}] {g.Title}" +
                          (g.Note is { Length: > 0 } ? $"  ({g.Note})" : ""));
            foreach (var r in g.Rows)
                sb.AppendLine($"    {r.Field}: {r.OldValue}  ->  {r.NewValue}");
            if (g.Rows.Count == 0) sb.AppendLine("    (no readable differences)");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }
}

/// <summary>
///     Builds the pending edits report: for every edit it takes the original blob from the save and
///     shows a "before → after" breakdown (the comparison is <see cref="BlobDiff" />). The original is
///     read-only: no edits go into it.
/// </summary>
public static class PendingDiff
{
    public static DiffReport Build(EditSession edits, VSSaveFile? save, SaveScanResult? scan)
    {
        var report = new DiffReport();
        var positions = edits.PayloadPositions.Union(edits.WholeChunkPositions).OrderBy(p => p).ToList();
        report.ChunkCount = positions.Count;
        report.PlayerCount = edits.PlayerIds.Count;

        foreach (var pos in positions)
        {
            var original = save?.ReadChunk(pos);
            var group = new DiffGroup
                { Title = EditSession.ChunkScope(pos), Key = pos, IsPlayer = false, OldBytes = original?.Length ?? 0 };

            if (edits.TryGetWholeChunk(pos, out var whole) && whole is not null)
            {
                group.NewBytes = whole.Length;
                group.Rows.Add(BlobDiff.Bytes(edits.LabelForWholeChunk(pos), original, whole));
            }
            else if (edits.TryGetPayloadEdits(pos, out var map) && map is { Count: > 0 })
            {
                group.NewBytes = original?.Length ?? 0; // payload edits change the length by a few bytes
                foreach (var kv in map.OrderBy(k => k.Key.field).ThenBy(k => k.Key.occ))
                {
                    var label = edits.LabelForPayload(pos, kv.Key.field, kv.Key.occ);
                    var oldPayload = original is null ? null : BlobDiff.TopPayload(original, kv.Key.field, kv.Key.occ);
                    group.Rows.AddRange(BlobDiff.Payload(kv.Key.field, label, oldPayload, kv.Value));
                }
            }

            if (original is null) group.Note = "original blob unavailable";
            report.Groups.Add(group);
        }

        // PlayerRows is a DB query: fetch it once, not once per player
        var playerRows = save is null ? new List<VSSaveFile.PlayerRow>() : save.PlayerRows;
        foreach (var id in edits.PlayerIds.OrderBy(p => p))
        {
            if (!edits.TryGetPlayerBlob(id, out var newBlob) || newBlob is null) continue;
            var scanPlayer = scan?.Players.FirstOrDefault(p => p.PlayerId == id);
            var row = playerRows.FirstOrDefault(r => r.Id == id);
            var uid = scanPlayer?.PlayerUid ?? row.Uid;
            var old = scanPlayer?.Raw?.Data is { Length: > 0 } raw ? raw :
                row.Data is { Length: > 0 } ? row.Data : null;

            var group = new DiffGroup
            {
                Title = $"player #{id}" + (string.IsNullOrEmpty(uid) ? "" : $" ({uid})"),
                Key = id,
                IsPlayer = true,
                OldBytes = old?.Length ?? 0,
                NewBytes = newBlob.Length
            };
            group.Rows.AddRange(BlobDiff.Player(edits.LabelForPlayer(id), old, newBlob));
            if (old is null) group.Note = "original blob unavailable";
            report.Groups.Add(group);
        }

        return report;
    }
}