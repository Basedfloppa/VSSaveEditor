using System;
using System.Collections.Generic;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>Undo/redo journal: restoring the "before" state, dropping the redo branch, merging consecutive edits.</summary>
    public class EditJournalTests
    {
        /// <summary>The state the test edits "work" on: a single cell.</summary>
        private sealed class Cell
        {
            public string Value = "original";
        }

        [Fact]
        public void Undo_restores_previous_value_and_Redo_reapplies()
        {
            var cell = new Cell();
            var journal = new EditJournal();
            string before = cell.Value;
            cell.Value = "edited";
            journal.Push(new EditRecord(1, false, "scope", "value",
                undo: () => cell.Value = before, redo: () => cell.Value = "edited", coalesceKey: null));

            Assert.True(journal.CanUndo);
            Assert.False(journal.CanRedo);

            var undone = journal.Undo();
            Assert.NotNull(undone);
            Assert.Equal("original", cell.Value);
            Assert.False(journal.CanUndo);
            Assert.True(journal.CanRedo);

            journal.Redo();
            Assert.Equal("edited", cell.Value);
            Assert.True(journal.CanUndo);
            Assert.False(journal.CanRedo);
        }

        [Fact]
        public void New_edit_discards_the_redo_branch()
        {
            var journal = new EditJournal();
            int state = 0;
            journal.Push(new EditRecord(1, false, "s", "a", () => state = 0, () => state = 1, null));
            journal.Undo();
            Assert.Equal(0, state);
            Assert.True(journal.CanRedo);

            // Push only records a step: the caller applies the edit itself. The redo branch is
            // dropped in the process — there is no way back to the undone "a".
            journal.Push(new EditRecord(1, false, "s", "b", () => state = 0, () => state = 2, null));
            Assert.False(journal.CanRedo);
            Assert.Equal(1, journal.UndoCount);

            Assert.Null(journal.Redo());
            Assert.Equal(0, state);
            journal.Undo();
            journal.Redo();
            Assert.Equal(2, state);   // redo yields a new edit, not the undone one
        }

        [Fact]
        public void Consecutive_edits_of_the_same_target_merge_into_one_step()
        {
            var journal = new EditJournal();
            int state = 0;
            journal.Push(new EditRecord(1, false, "s", "field", () => state = 0, () => state = 1, "payload:1:6:0"));
            journal.Push(new EditRecord(1, false, "s", "field", () => state = 1, () => state = 2, "payload:1:6:0"));

            Assert.Equal(1, journal.UndoCount);
            journal.Undo();
            Assert.Equal(0, state);          // the "before" state came from the first edit
            journal.Redo();
            Assert.Equal(2, state);          // the "after" state came from the last one
        }

        [Fact]
        public void Different_targets_do_not_merge()
        {
            var journal = new EditJournal();
            journal.Push(new EditRecord(1, false, "s", "a", () => { }, () => { }, "payload:1:6:0"));
            journal.Push(new EditRecord(1, false, "s", "b", () => { }, () => { }, "payload:1:6:1"));
            Assert.Equal(2, journal.UndoCount);
        }

        [Fact]
        public void Null_coalesce_key_never_merges()
        {
            var journal = new EditJournal();
            journal.Push(new EditRecord(1, false, "s", "hex", () => { }, () => { }, null));
            journal.Push(new EditRecord(1, false, "s", "hex", () => { }, () => { }, null));
            Assert.Equal(2, journal.UndoCount);
        }

        [Fact]
        public void Capacity_drops_the_oldest_steps()
        {
            var journal = new EditJournal { Capacity = 3 };
            for (int i = 0; i < 5; i++) journal.Push(new EditRecord(i, false, "s", "e" + i, () => { }, () => { }, null));
            Assert.Equal(3, journal.UndoCount);
            Assert.Equal("e4", journal.NextUndo!.Label);
        }

        [Fact]
        public void BuildView_marks_the_cursor_between_undone_and_pending()
        {
            var journal = new EditJournal();
            journal.Push(new EditRecord(1, false, "s", "a", () => { }, () => { }, null));
            journal.Push(new EditRecord(2, false, "s", "b", () => { }, () => { }, null));
            journal.Undo();

            var view = journal.BuildView();
            Assert.Equal(2, view.Count);
            Assert.Equal("a", view[0].Record.Label);       // the entry closest to undo is on top
            Assert.True(view[0].IsNext);
            Assert.False(view[0].IsFuture);
            Assert.Equal("b", view[1].Record.Label);       // the undone one is below, dimmed
            Assert.True(view[1].IsFuture);
            Assert.True(view[1].IsNext);
        }

        [Fact]
        public void Clear_empties_both_stacks()
        {
            var journal = new EditJournal();
            journal.Push(new EditRecord(1, false, "s", "a", () => { }, () => { }, null));
            journal.Undo();
            journal.Clear();
            Assert.False(journal.CanUndo);
            Assert.False(journal.CanRedo);
            Assert.Empty(journal.BuildView());
        }

        [Fact]
        public void Undo_and_Redo_on_empty_journal_are_no_ops()
        {
            var journal = new EditJournal();
            Assert.Null(journal.Undo());
            Assert.Null(journal.Redo());
        }
    }
}
