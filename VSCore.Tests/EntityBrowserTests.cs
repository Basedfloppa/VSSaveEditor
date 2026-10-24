using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>
    ///     The flat record list of the "Entities" tab. This used to be a text dump with the
    ///     occasional card inside — there was no way to search it for a record; now it is a filterable
    ///     index, and it is covered by tests.
    /// </summary>
    public class EntityBrowserTests
    {
        private static SaveScanResult Fixture()
        {
            var res = new SaveScanResult();

            var c1 = new ChunkScanData { Position = 100, ChunkX = 5, ChunkY = 1, ChunkZ = 7 };
            c1.Entities.Add(new EntityInfo
            {
                Species = "game:wolf-eurasian-adult-male", Domain = "game", Kind = "wolf", SexAge = "adult-male",
                ChunkX = 5, ChunkY = 1, ChunkZ = 7, Position = 100, Occurrence = 0, DataLength = 395,
                Attributes = { ["maxhealth"] = "1" },
                Fields = { "onHurt = 7" }
            });
            c1.Entities.Add(new EntityInfo
            {
                Species = "game:chicken-hen", Kind = "chicken", SexAge = "hen",
                ChunkX = 5, ChunkY = 1, ChunkZ = 7, Position = 100, Occurrence = 1, DataLength = 120
            });
            // a node with no type or attributes: there is nothing to show for it in the list
            c1.Entities.Add(new EntityInfo { ChunkX = 5, ChunkY = 1, ChunkZ = 7, Position = 100, Occurrence = 2 });
            c1.BlockEntities.Add(new BlockEntityInfo
            {
                Type = "Spawner", SpeciesCodes = { "game:drifter-normal" },
                Tokens = { "Spawner", "entityCodes", "meta-spawner" },
                ChunkX = 5, ChunkY = 1, ChunkZ = 7, Position = 100, Occurrence = 0, DataLength = 88
            });
            res.Chunks.Add(c1);

            var c2 = new ChunkScanData { Position = 200, ChunkX = 9, ChunkY = 2, ChunkZ = 9 };
            c2.Entities.Add(new EntityInfo
            {
                Species = null, Kind = null, ChunkX = 9, ChunkY = 2, ChunkZ = 9,
                Position = 200, Occurrence = 0, DataLength = 40,
                Attributes = { ["entityDead"] = "1" }
            });
            c2.BlockEntities.Add(new BlockEntityInfo
            {
                Type = "GenericTypedContainer", Tokens = { "GenericTypedContainer" },
                ChunkX = 9, ChunkY = 2, ChunkZ = 9, Position = 200, Occurrence = 0, DataLength = 640
            });
            res.Chunks.Add(c2);

            return res;
        }

        [Fact]
        public void Build_skips_inactive_entities_and_keeps_block_entities()
        {
            var rows = EntityBrowser.Build(Fixture());
            // 3 entities: a wolf, a chicken, and a typeless node -> the last one does not make it in
            Assert.Equal(3, rows.Count(r => !r.IsBlockEntity));
            Assert.Equal(2, rows.Count(r => r.IsBlockEntity));
            Assert.DoesNotContain(rows, r => r.Name.Length == 0);
        }

        [Fact]
        public void Build_sorts_by_name_and_carries_the_edit_address()
        {
            var rows = EntityBrowser.Build(Fixture());
            var names = rows.Select(r => r.Name).ToList();
            var sorted = names.OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase).ToList();
            Assert.Equal(sorted, names);

            var wolf = rows.Single(r => r.Name == "wolf-eurasian-adult-male");
            Assert.Equal(6, wolf.Field);
            Assert.Equal(100, wolf.Position);
            Assert.Equal(0, wolf.Occurrence);
            Assert.Equal(395, wolf.Size);
            Assert.True(wolf.IsAddressable);
            Assert.Equal("[wolf]", wolf.KindBadge);
            Assert.Equal("chunk 5:7 y1", wolf.ChunkText);
            // the row name is the code path, while the full code (with the domain) stays available separately
            Assert.Equal("game:wolf-eurasian-adult-male", wolf.Code);
            Assert.Equal("game", wolf.Domain);
            Assert.Equal("", wolf.DomainBadge); // a vanilla domain gets no badge

            var spawner = rows.Single(r => r.IsBlockEntity && r.Name == "Spawner");
            Assert.Equal(8, spawner.Field);
            Assert.Equal(100, spawner.Position);
            Assert.True(spawner.IsAddressable);
            Assert.Equal("[block entity]", spawner.KindBadge);
            Assert.Contains("drifter", spawner.Sub);
        }

        [Fact]
        public void Name_falls_back_to_attributes_when_species_is_missing()
        {
            var rows = EntityBrowser.Build(Fixture());
            var unnamed = rows.Single(r => r.Name == "entityDead");
            Assert.False(unnamed.IsBlockEntity);
            Assert.Equal("", unnamed.KindBadge); // the kind is not recognized — no badge
        }

        [Fact]
        public void Category_filter_separates_creatures_from_block_entities()
        {
            var all = EntityBrowser.Build(Fixture());
            Assert.Equal(3, EntityBrowser.Count(all, EntityCategory.Entities));
            Assert.Equal(2, EntityBrowser.Count(all, EntityCategory.BlockEntities));
            Assert.Equal(5, EntityBrowser.Count(all, EntityCategory.All));
            Assert.All(EntityBrowser.Filter(all, EntityCategory.Entities, ""), r => Assert.False(r.IsBlockEntity));
            Assert.All(EntityBrowser.Filter(all, EntityCategory.BlockEntities, ""), r => Assert.True(r.IsBlockEntity));
        }

        [Fact]
        public void Text_filter_matches_species_kind_sexage_fields_codes_and_tokens()
        {
            var all = EntityBrowser.Build(Fixture());

            Assert.Single(EntityBrowser.Filter(all, EntityCategory.All, "wolf"));
            Assert.Single(EntityBrowser.Filter(all, EntityCategory.All, "WOLF")); // case does not matter
            Assert.Single(EntityBrowser.Filter(all, EntityCategory.All, "hen"));
            Assert.Single(EntityBrowser.Filter(all, EntityCategory.All, "adult-male"));
            Assert.Single(EntityBrowser.Filter(all, EntityCategory.All, "maxhealth")); // an attribute key
            Assert.Single(EntityBrowser.Filter(all, EntityCategory.All, "onHurt = 7")); // a field value
            Assert.Single(EntityBrowser.Filter(all, EntityCategory.All, "drifter"));    // a block entity code
            Assert.Single(EntityBrowser.Filter(all, EntityCategory.All, "container"));  // a block entity token
            Assert.Empty(EntityBrowser.Filter(all, EntityCategory.All, "zzz-nothing"));

            // the category and the text apply together
            Assert.Empty(EntityBrowser.Filter(all, EntityCategory.Entities, "spawner"));
            Assert.Single(EntityBrowser.Filter(all, EntityCategory.BlockEntities, "spawner"));
        }

        [Fact]
        public void Top_names_counts_by_name_and_is_ordered()
        {
            var all = EntityBrowser.Build(Fixture());
            var top = EntityBrowser.TopNames(all);
            Assert.Equal(5, top.Count); // 5 distinct names, the fixture contains no duplicates
            Assert.All(top, t => Assert.Equal(1, t.Count));

            var repeated = EntityBrowser.Build(Fixture());
            var wolfRow = repeated.Single(r => r.Name == "wolf-eurasian-adult-male");
            var chickenRow = repeated.Single(r => r.Name == "chicken-hen");
            chickenRow.Entity!.Species = wolfRow.Entity!.Species; // two entities sharing one name
            var grouped = EntityBrowser.TopNames(repeated);
            Assert.Equal(2, grouped[0].Count); // the most frequent name comes first
        }

        [Fact]
        public void List_text_lists_every_shown_row()
        {
            var all = EntityBrowser.Build(Fixture());
            var shown = EntityBrowser.Filter(all, EntityCategory.All, "wolf");
            var text = string.Join("\n", shown.Select(r => r.Display));
            Assert.Contains("wolf-eurasian-adult-male", text);
            Assert.Contains("[wolf]", text);
            Assert.Contains("chunk 5:7 y1", text);
            Assert.Contains("395 B", text);
        }

        /// <summary>
        ///     The exact defect: for a horse with the nametag "Moonlight" the row name was `slot-` — a
        ///     truncated inventory key. This checks that the kind comes from the code, the mod domain is
        ///     visible, and the player name is in the row and participates in the search.
        /// </summary>
        [Fact]
        public void Row_shows_players_name_and_mod_domain_of_the_species()
        {
            var res = new SaveScanResult();
            var c = new ChunkScanData { Position = 300, ChunkX = 16005, ChunkY = 7, ChunkZ = 15986 };
            c.Entities.Add(new EntityInfo
            {
                Species = "equusdestrier:horse-adult-female-destrier", Domain = "equusdestrier",
                Kind = "horse", SexAge = "female-destrier", CustomName = "Moonlight",
                ChunkX = 16005, ChunkY = 7, ChunkZ = 15986, Position = 300, Occurrence = 1, DataLength = 2362
            });
            res.Chunks.Add(c);

            var row = EntityBrowser.Build(res).Single();
            Assert.Equal("horse-adult-female-destrier", row.Name); // the path without the domain
            Assert.Equal("equusdestrier:horse-adult-female-destrier", row.Code);
            Assert.Equal("equusdestrier", row.Domain);
            Assert.Equal(" [mod: equusdestrier]", row.DomainBadge); // the leading space is part of what Display concatenates
            Assert.Equal("Moonlight", row.Label);
            Assert.Equal("Moonlight", row.CustomName);
            Assert.Equal("[horse]", row.KindBadge); // "it should be a horse"
            Assert.Contains("Moonlight", row.Display);
            Assert.Contains("horse-adult-female-destrier", row.Display);
            Assert.Contains("[horse]", row.Display);

            Assert.Single(EntityBrowser.Filter(EntityBrowser.Build(res), EntityCategory.All, "moonlight"));
            Assert.Single(EntityBrowser.Filter(EntityBrowser.Build(res), EntityCategory.All, "equusdestrier"));
            Assert.Single(EntityBrowser.Filter(EntityBrowser.Build(res), EntityCategory.All, "horse"));
            Assert.Empty(EntityBrowser.Filter(EntityBrowser.Build(res), EntityCategory.All, "slot-"));
        }
    }
}
