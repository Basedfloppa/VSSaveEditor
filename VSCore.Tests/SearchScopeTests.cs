using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>
    ///     The search scope of the tabs: parsing, containment of exact points, and the scoped indexes
    ///     (chunks, entities, item stacks). The scope used to be implicit - "the whole save" - and every tab searched
    ///     all of it; these tests pin down what "this chunk" means for each kind of record.
    /// </summary>
    public class SearchScopeTests
    {
        // ---------------------------------------------------------------- parsing

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData("all")]
        [InlineData("SAVE")]
        [InlineData("*")]
        public void Whole_save_aliases(string text)
        {
            var s = SearchScope.Parse(text);
            Assert.Equal(SearchScope.ScopeKind.Everything, s.Kind);
            Assert.True(s.IsEverything);
            Assert.Null(s.ChunkScopeOf);
            Assert.True(s.ContainsChunk(int.MinValue, int.MaxValue));
            Assert.True(s.ContainsPoint(-1e9, 1e9));
        }

        [Fact]
        public void Unplaced_scope_contains_nothing_placed_but_is_a_restriction()
        {
            var s = SearchScope.Parse("unplaced");
            Assert.Equal(SearchScope.ScopeKind.Unplaced, s.Kind);
            Assert.False(s.ContainsChunk(0, 0));
            Assert.False(s.ContainsPoint(512, 512));
            // null would mean "no restriction", so a restriction object must come back; it carries the same kind,
            // and the "contains nothing" answer comes from the kind, not from the rectangle
            var restriction = s.ChunkScopeOf;
            Assert.NotNull(restriction);
            Assert.Equal(SearchScope.ScopeKind.Unplaced, restriction!.Kind);
            Assert.False(restriction.ContainsChunk(0, 0));
        }

        [Fact]
        public void Two_numbers_are_a_column_and_four_a_rectangle()
        {
            var col = SearchScope.Parse("15991, 15983");
            Assert.Equal(SearchScope.ScopeKind.Column, col.Kind);
            Assert.Equal((15991, 15983, 15991, 15983), (col.MinCx, col.MinCz, col.MaxCx, col.MaxCz));
            Assert.Equal("chunk 15991, 15983", col.Label);

            var rect = SearchScope.Parse("100,200,300,400");
            Assert.Equal(SearchScope.ScopeKind.Rectangle, rect.Kind);
            Assert.Equal((100, 200, 300, 400), (rect.MinCx, rect.MinCz, rect.MaxCx, rect.MaxCz));
            Assert.Contains("X 100..300", rect.Label);
        }

        [Fact]
        public void Prefixed_forms_and_reversed_corners_are_accepted()
        {
            var a = SearchScope.Parse("chunk=7,8");
            Assert.True(a.ContainsChunk(7, 8));
            Assert.False(a.ContainsChunk(8, 7));

            var b = SearchScope.Parse("rect=20,10,5,1");
            Assert.Equal((5, 1, 20, 10), (b.MinCx, b.MinCz, b.MaxCx, b.MaxCz));
        }

        [Fact]
        public void Garbage_and_area_labels_are_rejected_with_the_accepted_forms()
        {
            var ex = Assert.Throws<FormatException>(() => SearchScope.Parse("north of spawn"));
            Assert.Contains("not a scope", ex.Message);
            Assert.Throws<FormatException>(() => SearchScope.Parse("area=2"));
            Assert.Throws<FormatException>(() => SearchScope.Parse("5,6,7"));
        }

        [Fact]
        public void Area_scope_covers_the_area_rectangle()
        {
            var area = new ChunkArea { MinCx = 10, MinCz = 20, MaxCx = 12, MaxCz = 24, Columns = 40, Rows = 120 };
            var s = SearchScope.ForArea(area, 3);
            Assert.Equal(SearchScope.ScopeKind.Area, s.Kind);
            Assert.Contains("40 chunks", s.Label);
            Assert.True(s.ContainsChunk(10, 20));
            Assert.True(s.ContainsChunk(12, 24));
            Assert.False(s.ContainsChunk(9, 20));
            Assert.False(s.ContainsChunk(10, 25));
        }

        // ---------------------------------------------------------------- exact points

        [Fact]
        public void Everything_contains_any_point()
        {
            Assert.True(SearchScope.Everything.ContainsPoint(512000, 512000));
            Assert.True(SearchScope.Everything.ContainsPoint(-32, -1));
        }

        [Fact]
        public void Points_are_placed_by_floor_not_by_cast()
        {
            var s = SearchScope.ForColumn(0, 0);
            // block -1 is in chunk -1 — a cast (-1/32 = 0) would land it in chunk 0
            Assert.False(s.ContainsPoint(-0.5, 5));
            Assert.True(s.ContainsPoint(0, 0));
            Assert.True(s.ContainsPoint(31.99, 31.99));
            Assert.False(s.ContainsPoint(32, 0));
        }

        [Fact]
        public void Y_levels_of_a_column_are_all_inside()
        {
            var s = SearchScope.ForColumn(3, 4);
            // the scope is (cx,cz): a chest at y=-200 and one at y=300 are in the same column
            Assert.True(s.ContainsPoint(3 * 32 + 5, 4 * 32 + 5));
            Assert.False(s.ContainsPoint(4 * 32 + 5, 4 * 32 + 5));
        }

        // ---------------------------------------------------------------- scoped indexes

        private static SaveScanResult Fixture()
        {
            var res = new SaveScanResult();

            // chunk (5,7), y1: a wolf, a chest with 2 stacks, plus a creature inventory; (5,7) y2: another chest
            var c1 = new ChunkScanData { Position = 100, ChunkX = 5, ChunkY = 1, ChunkZ = 7 };
            c1.Entities.Add(new EntityInfo
            {
                Species = "game:wolf-eurasian-adult-male", Kind = "wolf", ChunkX = 5, ChunkY = 1, ChunkZ = 7,
                Position = 100, Occurrence = 0, Fields = { "maxhealth = 12" },
                Inventories = { Inv(InventoryOwnerKind.Entity, chunk: (5, 1, 7), items: 1) }
            });
            c1.BlockEntities.Add(new BlockEntityInfo
            {
                Type = "Chest", ChunkX = 5, ChunkY = 1, ChunkZ = 7, Position = 100, Occurrence = 1,
                Inventories = { Inv(InventoryOwnerKind.BlockEntity, chunk: (5, 1, 7), items: 2, x: 5 * 32 + 3, z: 7 * 32 + 4) }
            });
            res.Chunks.Add(c1);

            var c2 = new ChunkScanData { Position = 101, ChunkX = 5, ChunkY = 2, ChunkZ = 7 };
            c2.BlockEntities.Add(new BlockEntityInfo
            {
                Type = "Barrel", ChunkX = 5, ChunkY = 2, ChunkZ = 7, Position = 101, Occurrence = 0,
                Inventories = { Inv(InventoryOwnerKind.BlockEntity, chunk: (5, 2, 7), items: 1) }
            });
            res.Chunks.Add(c2);

            var c3 = new ChunkScanData { Position = 200, ChunkX = 9, ChunkY = 2, ChunkZ = 9 };
            c3.BlockEntities.Add(new BlockEntityInfo
            {
                Type = "Chest", ChunkX = 9, ChunkY = 2, ChunkZ = 9, Position = 200, Occurrence = 0,
                Inventories = { Inv(InventoryOwnerKind.BlockEntity, chunk: (9, 2, 9), items: 3) }
            });
            res.Chunks.Add(c3);

            // playerdata has no position and rides along with every scope
            res.Inventories.Add(Inv(InventoryOwnerKind.Player, chunk: (0, 0, 0), items: 1, playerId: 7));
            foreach (var c in res.Chunks)
            foreach (var e in c.Entities) res.Inventories.AddRange(e.Inventories);
            foreach (var c in res.Chunks)
            foreach (var be in c.BlockEntities) res.Inventories.AddRange(be.Inventories);

            res.EntityCount = 1;
            res.BlockEntityCount = 3;
            res.ItemStackCount = res.Inventories.Sum(i => i.Items.Count);
            return res;
        }

        private static InventoryInfo Inv(InventoryOwnerKind kind, (int, int, int) chunk, int items,
            long playerId = -1, int? x = null, int? z = null)
        {
            var inv = new InventoryInfo
            {
                OwnerKind = kind,
                Name = "inventory",
                ChunkX = chunk.Item1,
                ChunkY = chunk.Item2,
                ChunkZ = chunk.Item3,
                PlayerId = playerId,
                HasChunk = kind != InventoryOwnerKind.Player,
                X = x,
                Z = z,
                SlotCount = 8
            };
            for (var i = 0; i < items; i++)
                inv.Items.Add(new ItemStackInfo { Code = "ore-quartz", Count = 1, Slot = i.ToString() });
            inv.OwnerLabel = kind == InventoryOwnerKind.Player ? "zorolin" : "chest";
            return inv;
        }

        [Fact]
        public void Everything_scope_builds_every_column()
        {
            var s = SearchScope.Everything;
            Assert.True(s.ContainsChunk(5, 7));
            Assert.True(s.ContainsChunk(9, 9));
            Assert.Equal(2, ChunkBrowser.Build(Fixture(), s).Count);
        }

        [Fact]
        public void Chunk_columns_are_filtered_at_build_time()
        {
            var all = ChunkBrowser.Build(Fixture());
            Assert.Equal(2, all.Count);

            var scoped = ChunkBrowser.Build(Fixture(), SearchScope.ForColumn(5, 7));
            Assert.Single(scoped);
            Assert.Equal((5, 7), (scoped[0].Cx, scoped[0].Cz));
            // both y columns of (5,7): the scope has no Y borders
            Assert.Equal(2, scoped[0].Columns.Count);
            // "no filter" suggests the most populated chunk OF THE SCOPE
            Assert.Equal("wolf", ChunkBrowser.Filter(scoped, ChunkQuery.Parse(""), 400).Single().Columns[0].Entities[0].Kind);
        }

        [Fact]
        public void Unplaced_scope_leaves_no_columns()
        {
            Assert.Empty(ChunkBrowser.Build(Fixture(), SearchScope.UnplacedOnly.ChunkScopeOf));
        }

        [Fact]
        public void Entities_inside_the_column_of_the_scope_only()
        {
            var scoped = EntityBrowser.Build(Fixture(), SearchScope.ForColumn(9, 9));
            Assert.Single(scoped);
            Assert.Equal("Chest", scoped[0].Name);
            Assert.Equal((9, 9), (scoped[0].ChunkX, scoped[0].ChunkZ));

            var whole = EntityBrowser.Build(Fixture());
            Assert.Equal(4, whole.Count);
            Assert.Equal(4, EntityBrowser.Build(Fixture(), SearchScope.Everything.ChunkScopeOf).Count);
        }

        [Fact]
        public void Item_stacks_of_a_column_are_the_placed_ones_only()
        {
            var rows = InventoryBrowser.Build(Fixture(), SearchScope.ForColumn(5, 7));
            // 1 creature + 2 chest + 1 barrel; (9,9) and playerdata are outside
            Assert.Equal(4, rows.Count);
            Assert.DoesNotContain(rows, r => r.IsPlayer);
        }

        [Fact]
        public void Item_stacks_of_unplaced_scope_are_the_players()
        {
            // the inventory index takes the scope itself (not ChunkScopeOf): it recognises "unplaced" by kind
            var playersOnly = InventoryBrowser.Build(Fixture(), SearchScope.UnplacedOnly);
            Assert.Single(playersOnly);
            Assert.True(playersOnly[0].IsPlayer);

            // without a scope everything is there, players included, as before
            Assert.Equal(8, InventoryBrowser.Build(Fixture(), null).Count);
        }

        [Fact]
        public void Container_position_wins_over_record_chunk_and_can_exclude_it()
        {
            // a chest whose pos.x/z lie outside the scope's column: the exact position decides, not the record's chunk
            var res = Fixture();
            var cheat = res.Chunks[0].BlockEntities[0].Inventories[0];
            cheat.X = 31 * 32 + 1;
            cheat.Z = 31 * 32 + 1;

            var rows = InventoryBrowser.Build(res, SearchScope.ForColumn(5, 7));
            Assert.DoesNotContain(rows, r => ReferenceEquals(r.Inventory, cheat));

            // ...and appears in the column it actually stands in
            var there = InventoryBrowser.Build(res, SearchScope.ForColumn(31, 31));
            Assert.Contains(there, r => ReferenceEquals(r.Inventory, cheat));
        }
    }
}
