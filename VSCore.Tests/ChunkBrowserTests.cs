using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>
    ///     Query parsing and the chunk index of the "Chunks" tab. The chunk list used to be assembled
    ///     right in the window (a ComboBox with all coordinates) and there was no way to test it — hence this model.
    /// </summary>
    public class ChunkBrowserTests
    {
        private static SaveScanResult Fixture()
        {
            var res = new SaveScanResult();

            // column 5,7: two y-layers, a wolf with a field, a spawner, ore
            var c1 = new ChunkScanData { Position = 100, ChunkX = 5, ChunkY = 1, ChunkZ = 7 };
            c1.SolidCells = 1000;
            c1.BlockCodeCounts["rock-granite"] = 900;
            c1.BlockCodeCounts["ore-quartz-granite"] = 120;
            c1.Entities.Add(new EntityInfo
            {
                Species = "game:wolf-eurasian-adult-male", Kind = "wolf", SexAge = "adult-male",
                ChunkX = 5, ChunkY = 1, ChunkZ = 7, Position = 100, Occurrence = 0,
                Fields = { "onHurt = 7", "maxhealth = 12" }
            });
            c1.BlockEntities.Add(new BlockEntityInfo
            {
                Type = "Spawner", SpeciesCodes = { "game:drifter-normal" }, Tokens = { "Spawner", "entityCodes" },
                ChunkX = 5, ChunkY = 1, ChunkZ = 7, Position = 100, Occurrence = 0
            });
            res.Chunks.Add(c1);

            var c2 = new ChunkScanData { Position = 101, ChunkX = 5, ChunkY = 2, ChunkZ = 7 };
            c2.SolidCells = 500;
            c2.BlockCodeCounts["rock-granite"] = 500;
            res.Chunks.Add(c2);

            // column 9,9: empty (air only), with an inactive node that has no type or attributes
            var c3 = new ChunkScanData { Position = 200, ChunkX = 9, ChunkY = 2, ChunkZ = 9 };
            c3.AirCells = 32768;
            c3.Entities.Add(new EntityInfo { ChunkX = 9, ChunkY = 2, ChunkZ = 9, Position = 200, Occurrence = 0 });
            res.Chunks.Add(c3);

            return res;
        }

        // ------------------------- query parsing -------------------------

        [Fact]
        public void Empty_query_matches_everything_and_describes_itself()
        {
            var q = ChunkQuery.Parse("   ");
            Assert.True(q.IsEmpty);
            Assert.Null(q.Error);
            Assert.Equal("no filter", q.Describe());
            Assert.True(q.Matches(ChunkBrowser.Build(Fixture())[0]));
        }

        [Fact]
        public void Coordinate_pair_and_single_axis_are_parsed()
        {
            var columns = ChunkBrowser.Build(Fixture());

            var pair = ChunkQuery.Parse("5 7");
            Assert.Single(columns, pair.Matches);
            Assert.Equal("X=5 · Z=7", pair.Describe());

            var axisX = ChunkQuery.Parse("x9");
            Assert.Single(columns, axisX.Matches);
            Assert.DoesNotContain(columns, c => axisX.Matches(c) && c.Cx != 9);

            // a single number is a coordinate on any axis: 9 matches cx=9 (and cz=9)
            var any = ChunkQuery.Parse("9");
            Assert.Single(columns, any.Matches);

            // a comma is the same separator as a space
            var comma = ChunkQuery.Parse("5,7");
            Assert.Single(columns, comma.Matches);
        }

        [Fact]
        public void Ranges_apply_to_one_axis_or_any()
        {
            var columns = ChunkBrowser.Build(Fixture());

            var cxRange = ChunkQuery.Parse("cx5..6");
            var match = Assert.Single(columns, cxRange.Matches);
            Assert.Equal(5, match.Cx);

            var anyRange = ChunkQuery.Parse("4..8");
            Assert.Single(columns, anyRange.Matches); // 5 matches, 9 does not

            var zRange = ChunkQuery.Parse("cz8..10");
            Assert.Equal(9, Assert.Single(columns, zRange.Matches).Cz);

            // a reversed range is swapped instead of yielding an empty result
            var reversed = ChunkQuery.Parse("cx7..5");
            Assert.Single(columns, reversed.Matches);
        }

        [Fact]
        public void Counters_layer_and_empty_filters()
        {
            var columns = ChunkBrowser.Build(Fixture());

            Assert.Single(columns, ChunkQuery.Parse("ent>0").Matches);
            Assert.Equal(2, columns.Count(ChunkQuery.Parse("ent>=0").Matches));
            Assert.Single(columns, ChunkQuery.Parse("be>0").Matches);
            // blocks = solid + liquid cells: an empty column has none, so ">= 0" passes for both
            Assert.Single(columns, ChunkQuery.Parse("blocks>0").Matches);
            Assert.Equal(2, columns.Count(ChunkQuery.Parse("blocks>=0").Matches));
            Assert.Equal(2, columns.Count(ChunkQuery.Parse("yc>0").Matches));
            Assert.Single(columns, ChunkQuery.Parse("yc>1").Matches);

            // layer: y1 exists only for the column that has two of them
            Assert.Single(columns, ChunkQuery.Parse("y1").Matches);
            Assert.Equal(2, columns.Count(ChunkQuery.Parse("y2").Matches));

            var empty = Assert.Single(columns, ChunkQuery.Parse("empty").Matches);
            Assert.True(empty.IsEmpty);

            // terms are joined with AND
            Assert.DoesNotContain(columns, ChunkQuery.Parse("5 7 empty").Matches);
            Assert.Single(columns, ChunkQuery.Parse("5 7 y1").Matches);
        }

        [Fact]
        public void Content_matches_species_block_entity_code_and_entity_field()
        {
            var columns = ChunkBrowser.Build(Fixture());

            Assert.Single(columns, ChunkQuery.Parse("wolf").Matches);
            Assert.Single(columns, ChunkQuery.Parse("adult-male").Matches);
            Assert.Single(columns, ChunkQuery.Parse("ore-quartz").Matches);
            Assert.Single(columns, ChunkQuery.Parse("spawner").Matches);
            Assert.Single(columns, ChunkQuery.Parse("drifter").Matches);
            // the value of a parsed entity field
            Assert.Single(columns, ChunkQuery.Parse("maxhealth").Matches);
            // case does not matter
            Assert.Single(columns, ChunkQuery.Parse("WOLF").Matches);
            // a node with no type or attributes stays in the model but does not count as chunk content
            Assert.False(columns.Single(c => c.Cx == 9).Columns[0].Entities[0].HasInfo);
            Assert.DoesNotContain(columns, ChunkQuery.Parse("zzz-nothing").Matches);
        }

        [Fact]
        public void Bad_counter_reports_error_without_throwing()
        {
            var q = ChunkQuery.Parse("ent>99999999999999999999");
            Assert.NotNull(q.Error);
            Assert.Contains("ent>", q.Error);
        }

        // ------------------------- index -------------------------

        [Fact]
        public void Build_groups_y_columns_into_one_row_per_chunk_column()
        {
            var columns = ChunkBrowser.Build(Fixture());
            Assert.Equal(2, columns.Count); // (5,7) and (9,9), while there are three y-columns
            var c = columns.First(x => x.Cx == 5 && x.Cz == 7);
            Assert.Equal(2, c.Columns.Count);
            Assert.Equal(new[] { 1, 2 }, c.Columns.Select(x => x.ChunkY)); // sorted by Y
            Assert.Equal(1, c.Entities);        // the active entity
            Assert.Equal(1, c.BlockEntities);
            Assert.Equal(1500, c.SolidCells);
            Assert.Equal(1, c.MinY);
            Assert.Equal(2, c.MaxY);
            Assert.Contains("2 y-col", c.Summary);
            Assert.Contains("1 ent", c.Summary);
        }

        [Fact]
        public void Filter_without_query_shows_chunks_with_records_first()
        {
            var columns = ChunkBrowser.Build(Fixture());
            var shown = ChunkBrowser.Filter(columns, ChunkQuery.Parse(""));
            var only = Assert.Single(shown); // the empty column (9,9) does not make it into the "interesting" list
            Assert.Equal(5, only.Cx);
            Assert.Equal(1, ChunkBrowser.Count(columns, ChunkQuery.Parse("")));
        }

        [Fact]
        public void Filter_limits_result_and_count_sees_all_matches()
        {
            var columns = ChunkBrowser.Build(Fixture());
            var q = ChunkQuery.Parse("ent>=0");
            Assert.Equal(2, ChunkBrowser.Count(columns, q));
            Assert.Single(ChunkBrowser.Filter(columns, q, limit: 1));
        }
    }
}
