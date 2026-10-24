using System;
using System.Collections.Generic;

namespace VSCore;

/// <summary>
/// One undoable edit: what has to be shown to the user (area + label) plus two
/// operations returning the "before" and "after" states. The operations close over the state of whoever
/// recorded the edit, so the journal knows nothing about chunks or blob formats.
/// </summary>
public sealed class EditRecord
{
    public EditRecord(long key, bool isPlayer, string scope, string label,
        Action undo, Action redo, string? coalesceKey = null)
    {
        Key = key;
        IsPlayer = isPlayer;
        Scope = scope;
        Label = label;
        CoalesceKey = coalesceKey;
        UndoAction = undo;
        RedoAction = redo;
    }

    /// <summary>Chunk position or player id — the UI uses it to find what to redraw after a rollback.</summary>
    public long Key { get; }
    public bool IsPlayer { get; }

    /// <summary>Human-readable area ("chunk 3,-2 y5") — the history groups entries by it.</summary>
    public string Scope { get; }

    /// <summary>What exactly changed ("entity[2] game:wolf", "raw hex", "inventory 'backpack'").</summary>
    public string Label { get; }

    internal Action UndoAction { get; }
    internal Action RedoAction { get; set; }

    /// <summary>Edits with the same key in a row merge into one step: editing the same
    /// field twice must not require two Undo presses. null — never merge.</summary>
    internal string? CoalesceKey { get; }
}

/// <summary>A history entry as the UI sees it: flagged as "already undone" and "next to undo".</summary>
public sealed class HistoryEntry
{
    public HistoryEntry(EditRecord record, bool isFuture, bool isNext)
    {
        Record = record;
        IsFuture = isFuture;
        IsNext = isNext;
    }

    public EditRecord Record { get; }
    /// <summary>true — the edit is already undone and awaits redo (drawn dimmed).</summary>
    public bool IsFuture { get; }
    /// <summary>Cursor marker: the next edit to undo (or the first one to redo).</summary>
    public bool IsNext { get; }
}

/// <summary>
/// Journal of deferred edits: an undo stack + a redo stack and the rule "a new edit discards the redo".
/// It lives in VSCore (rather than next to EditSession in VSSaveEditor) because it is pure logic without UI —
/// that way ordinary unit tests cover it without pulling Avalonia into the test project.
/// </summary>
public sealed class EditJournal
{
    private readonly List<EditRecord> _past = new();     // old -> new
    private readonly List<EditRecord> _future = new();   // _future[0] is the next to redo

    /// <summary>How many steps to keep. The oldest are evicted: their edits are already applied, and only
    /// the ability to roll back further is lost.</summary>
    public int Capacity { get; set; } = 200;

    public bool CanUndo => _past.Count > 0;
    public bool CanRedo => _future.Count > 0;
    public int UndoCount => _past.Count;
    public int RedoCount => _future.Count;

    /// <summary>Accumulated (not undone) edits — old -> new.</summary>
    public IReadOnlyList<EditRecord> Past => _past;

    public EditRecord? NextUndo => _past.Count > 0 ? _past[^1] : null;
    public EditRecord? NextRedo => _future.Count > 0 ? _future[0] : null;

    public void Push(EditRecord record)
    {
        // A new edit makes the redo branch unreachable — it must not be kept, or Redo would restore
        // a state that will never exist again.
        _future.Clear();
        if (record.CoalesceKey is { Length: > 0 } key && _past.Count > 0)
        {
            var last = _past[^1];
            if (last.CoalesceKey == key)
            {
                last.RedoAction = record.RedoAction;   // "before" stays from the first edit, "after" comes from the last one
                return;
            }
        }
        _past.Add(record);
        if (Capacity > 0 && _past.Count > Capacity) _past.RemoveRange(0, _past.Count - Capacity);
    }

    /// <summary>Rolls back the top edit and returns it (null — nothing to roll back).</summary>
    public EditRecord? Undo()
    {
        if (_past.Count == 0) return null;
        var r = _past[^1];
        _past.RemoveAt(_past.Count - 1);
        r.UndoAction();
        _future.Insert(0, r);
        return r;
    }

    /// <summary>Redoes the first undone edit and returns it (null — nothing to redo).</summary>
    public EditRecord? Redo()
    {
        if (_future.Count == 0) return null;
        var r = _future[0];
        _future.RemoveAt(0);
        r.RedoAction();
        _past.Add(r);
        return r;
    }

    public void Clear()
    {
        _past.Clear();
        _future.Clear();
    }

    /// <summary>
    /// The history as one list for the panel: the edit closest to being undone is at the top, below it the
    /// already undone ones in redo order. The journal cursor sits between the last accumulated and the first undone edit.
    /// </summary>
    public List<HistoryEntry> BuildView()
    {
        var view = new List<HistoryEntry>(_past.Count + _future.Count);
        for (int i = _past.Count - 1; i >= 0; i--)
            view.Add(new HistoryEntry(_past[i], isFuture: false, isNext: i == _past.Count - 1));
        for (int i = 0; i < _future.Count; i++)
            view.Add(new HistoryEntry(_future[i], isFuture: true, isNext: i == 0));
        return view;
    }
}
