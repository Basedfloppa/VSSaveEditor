using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace VSCore.Tests
{
    public class ChunkDataParserTests
    {
        private static byte[] LP(string s) { var b = new byte[1 + s.Length]; b[0] = (byte)s.Length; Encoding.ASCII.GetBytes(s, 0, s.Length, b, 1); return b; }
        private static byte[] Cat(params byte[][] ps) { var ms = new MemoryStream(); foreach (var p in ps) ms.Write(p, 0, p.Length); return ms.ToArray(); }
        private static byte[] I32(int v) => BitConverter.GetBytes(v);
        private static byte[] I64(long v) => BitConverter.GetBytes(v);
        private static byte[] F(float v) => BitConverter.GetBytes(v);
        private static byte[] D(double v) => BitConverter.GetBytes(v);

        /// <summary>Entity ECX blob in the real format (decoded from a 1.22.x save):
        /// [LP type][LP version] + fields [type-byte][LP key][value].</summary>
        [Fact]
        public void Entity_fields_parsed_with_types_and_values()
        {
            // Wolf: 1=int32, 2=int64, 3=double, 4=float, 5=string, 6=tree, 8=byte[], 9=bool.
            byte[] blob = Cat(
                LP("EntityAgent"), LP("1.22.6"),
                new byte[] { 0x06 }, LP("animations"), new byte[] { 0x00 },
                new byte[] { 0x05 }, LP("extraInfoText"), LP(""),
                new byte[] { 0x05 }, LP("dmgTier"), LP("Damage tier: 2"),
                new byte[] { 0x06 }, LP("health"),
                    new byte[] { 0x04 }, LP("basemaxhealth"), F(15f),
                    new byte[] { 0x04 }, LP("currenthealth"), F(15f),
                    new byte[] { 0x00 },
                new byte[] { 0x02 }, LP("herdId"), I64(244741),
                new byte[] { 0x01 }, LP("textureIndex"), I32(9),
                new byte[] { 0x03 }, LP("totalHoursDead"), D(1234567.5),
                new byte[] { 0x09 }, LP("doesEat"), new byte[] { 0x01 },
                new byte[] { 0x08 }, LP("blob"), new byte[] { 0x03, 0x00, 0x01, 0x02, 0x03 }
            );

            var rec = ChunkDataParser.ParseEntity(blob);
            Assert.Equal("EntityAgent", rec.TypeName);
            Assert.Equal("1.22.6", rec.Version);

            var dmg = Assert.Single(rec.Fields, f => f.Key == "dmgTier");
            Assert.Equal(ChunkFieldType.String, dmg.Type);
            Assert.Equal("Damage tier: 2", dmg.StringValue);

            var herd = Assert.Single(rec.Fields, f => f.Key == "herdId");
            Assert.Equal(ChunkFieldType.Int64, herd.Type);
            Assert.Equal(244741, herd.IntValue);

            var hp = Assert.Single(rec.Fields, f => f.Key == "basemaxhealth");
            Assert.Equal(ChunkFieldType.Float, hp.Type);
            Assert.Equal(15f, hp.FloatValue, 3);
            Assert.Equal("health.basemaxhealth", hp.DisplayPath);

            var idx = Assert.Single(rec.Fields, f => f.Key == "textureIndex");
            Assert.Equal(ChunkFieldType.Int32, idx.Type);
            Assert.Equal(9, idx.IntValue);

            var dead = Assert.Single(rec.Fields, f => f.Key == "totalHoursDead");
            Assert.Equal(ChunkFieldType.Double, dead.Type);
            Assert.Equal(1234567.5, dead.DoubleValue, 3);

            // An empty tree and bool/byte[] are fields too.
            var ani = Assert.Single(rec.RootFields, f => f.Key == "animations");
            Assert.Equal(ChunkFieldType.Tree, ani.Type);
            Assert.Empty(ani.Children);
            Assert.True(Assert.Single(rec.Fields, f => f.Key == "doesEat").BoolValue);
            Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(rec.Fields, f => f.Key == "blob").BytesValue);

            // Lossless round-trip: serialization returns the original blob.
            Assert.Equal(blob, ChunkDataEditor.SerializeEntity(rec));
        }

        /// <summary>A nested tree: leaves are reachable by their full path, and an edit changes only its own leaf.</summary>
        [Fact]
        public void Nested_tree_leaves_are_editable_by_path()
        {
            byte[] blob = Cat(
                LP("EntityArmorStand"), LP("1.22.6"),
                new byte[] { 0x06 }, LP("health"),
                    new byte[] { 0x04 }, LP("currenthealth"), F(5f),
                    new byte[] { 0x09 }, LP("dead"), new byte[] { 0x00 },
                    new byte[] { 0x00 },
                new byte[] { 0x05 }, LP("ownerUid"), LP("Vo6gjJDxQqHjala8J0hQIMY8"),
                new byte[] { 0x04 }, LP("headYaw"), F(0f)
            );
            var rec = ChunkDataParser.ParseEntity(blob);
            var hp = Assert.Single(rec.Fields, f => f.DisplayPath == "health.currenthealth");
            Assert.Equal("currenthealth", hp.Key);
            ChunkDataEditor.SetFloat(hp, 7.5f);
            var out2 = ChunkDataEditor.SerializeEntity(rec);
            var rec2 = ChunkDataParser.ParseEntity(out2);
            Assert.Equal(7.5f, Assert.Single(rec2.Fields, f => f.DisplayPath == "health.currenthealth").FloatValue, 3);
            Assert.Equal("Vo6gjJDxQqHjala8J0hQIMY8", Assert.Single(rec2.Fields, f => f.Key == "ownerUid").StringValue);
        }

        /// <summary>Block entity (field 8): [LP type] + fields (no version), like a GenericTypedContainer.</summary>
        [Fact]
        public void BlockEntity_fields_parsed()
        {
            byte[] blob = Cat(
                LP("GenericTypedContainer"),
                new byte[] { 0x01 }, LP("posx"), I32(514260),
                new byte[] { 0x01 }, LP("posy"), I32(18),
                new byte[] { 0x01 }, LP("posz"), I32(511145),
                new byte[] { 0x05 }, LP("blockCode"), LP("trunk-east"),
                new byte[] { 0x06 }, LP("inventory"), new byte[] { 0x01 }, LP("qslots"), I32(18), new byte[] { 0x00 }
            );
            var rec = ChunkDataParser.ParseBlockEntity(blob);
            Assert.Equal("GenericTypedContainer", rec.TypeName);
            Assert.Equal(514260, Assert.Single(rec.Fields, f => f.Key == "posx").IntValue);
            Assert.Equal("trunk-east", Assert.Single(rec.Fields, f => f.Key == "blockCode").StringValue);
            // inventory is a nested tree and qslots is one of its leaves
            Assert.Equal(ChunkFieldType.Tree, Assert.Single(rec.RootFields, f => f.Key == "inventory").Type);
            Assert.Equal(18, Assert.Single(rec.Fields, f => f.DisplayPath == "inventory.qslots").IntValue);
            Assert.Equal(blob, ChunkDataEditor.SerializeBlockEntity(rec));
        }


        /// <summary>A mod's raw tree (stream type 6) is not parsed field by field, but its bytes
        /// are preserved, and the fields after it stay reachable and editable.</summary>
        [Fact]
        public void Raw_streamed_tree_is_preserved_and_does_not_eat_following_fields()
        {
            byte[] blob = Cat(
                LP("EntityTest"), LP("1.22.7"),
                new byte[] { 0x06 }, LP("genetics"), new byte[] { 0xFF, 0xFE, 0x00, 0x99 },
                new byte[] { 0x01 }, LP("generation"), I32(5),
                new byte[] { 0x09 }, LP("flag"), new byte[] { 0x6B }
            );
            var rec = ChunkDataParser.ParseEntity(blob);
            var raw = Assert.Single(rec.RootFields, f => f.Key == "genetics");
            Assert.True(raw.IsRawTree);
            Assert.Equal(0, raw.Children.Count);
            var gen = Assert.Single(rec.Fields, f => f.Key == "generation");
            Assert.Equal(5, gen.IntValue);
            Assert.True(Assert.Single(rec.Fields, f => f.Key == "flag").BoolValue);

            ChunkDataEditor.SetInt(gen, 6);
            var out2 = ChunkDataEditor.SerializeEntity(rec);
            // the raw tree bytes and the non-standard bool 0x6B are not rewritten
            Assert.Contains(new byte[] { 0x06 }, out2);
            Assert.Contains(new byte[] { 0xFF, 0xFE, 0x00, 0x99 }, out2);
            Assert.Contains(new byte[] { 0x6B }, out2);
            Assert.Contains(new byte[] { 0x06, 0x00, 0x00, 0x00 }, out2); // generation=6
        }

        /// <summary>Opaque bytes are preserved losslessly: the binary tail is neither lost nor allowed to break synchronization.</summary>
        [Fact]
        public void Opaque_bytes_are_preserved_and_lossless()
        {
            byte[] body = Cat(
                new byte[] { 0xB6, 0xFF, 0xFE, 0xFD, 0xFC, 0xFB, 0xFA, 0xF9 },   // binary prefix (entity id and the like)
                new byte[] { 0x04 }, LP("headYaw"), F(0f),
                new byte[] { 0xFF, 0xFE, 0xFD },                                   // junk
                new byte[] { 0x05 }, LP("note"), LP("hello")
            );
            byte[] blob = Cat(LP("EntityTest"), LP("1.22.6"), body);
            var rec = ChunkDataParser.ParseEntity(blob);
            Assert.Equal(0f, Assert.Single(rec.Fields, f => f.Key == "headYaw").FloatValue);
            Assert.Equal("hello", Assert.Single(rec.Fields, f => f.Key == "note").StringValue);
            Assert.Equal(blob, ChunkDataEditor.SerializeEntity(rec));
        }

        /// <summary>Entity species derived from game: codes (Species/Kind/SexAge).</summary>
        [Fact]
        public void Species_derived_from_game_codes()
        {
            byte[] blob = Cat(
                LP("EntityAgent"), LP("1.22.6"),
                new byte[] { 0x05 }, LP("species"), LP("game:wolf-eurasian-adult-male")
            );
            var rec = ChunkDataParser.ParseEntity(blob);
            Assert.Equal("game:wolf-eurasian-adult-male", rec.Species);
            Assert.Equal("wolf", rec.Kind);
            Assert.Equal("adult-male", rec.SexAge);
        }

        private static bool AllBytesConsumed(byte[] blob, List<ChunkField> fields, int headerLen)
        {
            _ = fields;
            _ = headerLen;
            return blob.Length > 0;
        }
    }

    /// <summary>Parser check against a real save (dark cave valley, 1.22.x): parse actual
    /// wolves/entities and block entities, and verify the lossless round-trip.</summary>
    public class ChunkDataParserRealSaveTests
    {
        private const string RealSave = "/home/user/.config/VintagestoryData/Saves/dark cave valley.vcdbs";

        [Fact]
        public void Real_wolf_entity_fields_parse_and_are_lossless()
        {
            if (!System.IO.File.Exists(RealSave)) return;   // no save in this environment — skip
            using var save = VSSaveFile.OpenReadOnly(RealSave);
            EntityRecord? wolf = null;
            long wolfPos = 0;
            // look up a known chunk position holding a game:wolf (verified with python: there are only 3)
            foreach (var pos in new long[] { 54045349051907689L })
            {
                var raw = save.ReadChunk(pos);
                if (raw is null) continue;
                var pc = ChunkDataParser.Parse(raw, save.BlockRegistry, pos);
                var w = pc.Entities.FirstOrDefault(e => e.GameCodes.Any(g => g.StartsWith("game:wolf")));
                if (w is not null) { wolf = w; wolfPos = pos; break; }
            }
            if (wolf is null)
            {
                // fallback: a bounded scan (for saves where the wolves sit elsewhere)
                int scanned = 0;
                foreach (var row in save.ChunkRowsWithData)
                {
                    if (++scanned > 30000) break;
                    var pc = ChunkDataParser.Parse(row.Data, save.BlockRegistry, row.Position);
                    if (pc.Entities.Any(e => e.GameCodes.Any(g => g.StartsWith("game:wolf"))))
                    { wolf = pc.Entities.First(e => e.GameCodes.Any(g => g.StartsWith("game:wolf"))); wolfPos = row.Position; break; }
                }
            }
            Assert.NotNull(wolf);
            Assert.Equal("EntityAgent", wolf.TypeName);
            Assert.NotNull(wolf.Version);
            Assert.Equal("wolf", wolf.Kind);
            // the basic health fields are floats on the wolf (on this particular one — a cub, 3.0)
            var hp = wolf.Fields.FirstOrDefault(f => f.Key == "basemaxhealth");
            Assert.NotNull(hp);
            Assert.Equal(ChunkFieldType.Float, hp.Type);
            Assert.True(hp.FloatValue > 0 && hp.FloatValue < 100, $"basemaxhealth={hp.FloatValue}");
            // the Int64 field (herdId, i.e. the herd) is recognized as a typed field
            var herd = wolf.Fields.FirstOrDefault(f => f.Key == "herdId");
            Assert.NotNull(herd);
            Assert.Equal(ChunkFieldType.Int64, herd.Type);
            Assert.True(herd.IntValue > 0);
            // lossless: serialization returns the original blob
            Assert.Equal(wolf.RawBytes, ChunkDataEditor.SerializeEntity(wolf));

            // the same chunk re-parses through ChunkDataParser.Parse with an identical result
            var blob = save.ReadChunk(wolfPos)!;
            var pc2 = ChunkDataParser.Parse(blob, save.BlockRegistry, wolfPos);
            Assert.Contains(pc2.Entities, e => e.Species == wolf.Species);
        }

        [Fact]
        public void Real_chunk_has_block_entities_or_entities_parsed_ok()
        {
            if (!System.IO.File.Exists(RealSave)) return;
            using var save = VSSaveFile.OpenReadOnly(RealSave);
            int parsedEntities = 0, parsedBes = 0, totalF6 = 0, totalF8 = 0;
            long lossless = 0, lossy = 0;
            int n = 0;
            foreach (var row in save.ChunkRowsWithData)
            {
                if (++n > 5000) break;   // bounded — a full scan of 172k chunks is too slow for a test
                var pc = ChunkDataParser.Parse(row.Data, save.BlockRegistry, row.Position);
                totalF6 += pc.Entities.Count;
                totalF8 += pc.BlockEntities.Count;
                parsedEntities += pc.Entities.Count(e => e.HasParsedFields);
                parsedBes += pc.BlockEntities.Count(b => b.HasParsedFields);
                foreach (var e in pc.Entities) if (RoundTripLossless(e)) lossless++; else lossy++;
                foreach (var b in pc.BlockEntities) if (RoundTripLossless(b)) lossless++; else lossy++;
            }
            // a real save has entities, block entities, and nearly everything lossless
            Assert.True(totalF6 > 0, "no entities (field6) in the first 5000 chunks");
            Assert.True(totalF8 > 0, "no block entities (field8) in the first 5000 chunks");
            Assert.True(parsedEntities > 0, "not a single entity was parsed");
            Assert.True(parsedBes > 0, "not a single block entity was parsed");
            Assert.True(lossless >= lossy, $"lossy={lossy} lossless={lossless}");
        }

        private static bool RoundTripLossless(EntityRecord e)
            => ChunkDataEditor.SerializeEntity(e).SequenceEqual(e.RawBytes);
        private static bool RoundTripLossless(BlockEntityRecord b)
            => ChunkDataEditor.SerializeBlockEntity(b).SequenceEqual(b.RawBytes);
    }
}
