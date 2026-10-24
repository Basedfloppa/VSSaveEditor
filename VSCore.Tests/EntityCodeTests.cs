using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>
    ///     The species of an entity and its name. Parsing works on the real blob: VS writes the code as a
    ///     length-prefixed string (`AssetLocation.ToString()`) — `domain:path` for modded and plain `path`
    ///     for vanilla, while nearby sit attribute keys that also look like a code (`jaunt:stamina`) or a
    ///     truncated species (`slot-0` → `slot-`). A bug here is exactly what produced "Moonlight = slot-".
    /// </summary>
    public class EntityCodeTests
    {
        private static byte[] Lp(string s)
        {
            var b = new byte[1 + s.Length];
            b[0] = (byte)s.Length;
            Encoding.ASCII.GetBytes(s, 0, s.Length, b, 1);
            return b;
        }

        /// <summary>Length-prefixed UTF-8 (player names can contain non-ASCII characters).</summary>
        private static byte[] LpUtf8(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            var b = new byte[1 + bytes.Length];
            b[0] = (byte)bytes.Length;
            Array.Copy(bytes, 0, b, 1, bytes.Length);
            return b;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            var ms = new MemoryStream();
            foreach (var p in parts) ms.Write(p, 0, p.Length);
            return ms.ToArray();
        }

        private static byte[] TagLenDelim(int field, byte[] payload)
        {
            var ms = new MemoryStream();
            WriteVarint(ms, (field << 3) | 2);
            WriteVarint(ms, payload.Length);
            ms.Write(payload, 0, payload.Length);
            return ms.ToArray();
        }

        private static void WriteVarint(Stream s, int v)
        {
            uint u = (uint)v;
            while ((u & ~0x7f) != 0) { s.WriteByte((byte)((u & 0x7f) | 0x80)); u >>= 7; }
            s.WriteByte((byte)u);
        }

        /// <summary>Scans a save made of one chunk that holds the given field-6 blobs.</summary>
        private static SaveScanResult ScanEntities(params byte[][] entityBlobs)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "entcode_" + Guid.NewGuid().ToString("N") + ".vcdbs");
            var cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
            try
            {
                using (var con = new SqliteConnection(cs))
                {
                    con.Open();
                    Exec(con, "CREATE TABLE chunk(position INTEGER PRIMARY KEY, data BLOB);");
                    Exec(con, "CREATE TABLE mapchunk(position INTEGER PRIMARY KEY, data BLOB);");
                    Exec(con, "CREATE TABLE mapregion(position INTEGER PRIMARY KEY, data BLOB);");
                    Exec(con, "CREATE TABLE gamedata(savegameid INTEGER PRIMARY KEY, data BLOB);");
                    Exec(con, "CREATE TABLE playerdata(playerid INTEGER PRIMARY KEY AUTOINCREMENT, playeruid TEXT, data BLOB);");

                    var chunkBlob = new MemoryStream();
                    foreach (var e in entityBlobs)
                    {
                        var part = TagLenDelim(6, e);
                        chunkBlob.Write(part, 0, part.Length);
                    }

                    using var ins = con.CreateCommand();
                    ins.CommandText = "INSERT INTO chunk(position,data) VALUES(1,$d);";
                    ins.Parameters.AddWithValue("$d", chunkBlob.ToArray());
                    ins.ExecuteNonQuery();
                }

                using var save = VSSaveFile.OpenReadOnly(dbPath);
                return SaveScanner.Scan(save, captureRaw: false);
            }
            finally { if (File.Exists(dbPath)) File.Delete(dbPath); }
        }

        private static void Exec(SqliteConnection con, string sql)
        {
            using var c = con.CreateCommand();
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }

        /// <summary>A horse blob with the real layout: header, inventory with `slot-N` keys,
        /// the player name under the `nametag` group, an attribute key with a domain, and the species
        /// code at the end (in a real blob the code sits in the second half — measured: 0.68–0.96 of the length).</summary>
        private static byte[] HorseBlob(string code, string? name)
        {
            var parts = new System.Collections.Generic.List<byte[]>
            {
                Lp("EntityDestrier"), Lp("1.22.7"),
                Lp("backpack"), Lp("slots"),
                Lp("slot-0"), Lp("slot-1"), Lp("slot-2"),
                Lp("seatdata"), Lp("seatid"), Lp("baseseat-0"),
                Lp("currentgait"), Lp("idle"), Lp("jaunt:stamina")
            };
            if (name is not null)
            {
                parts.Add(Lp("nametag"));
                parts.Add(Lp("name"));
                parts.Add(LpUtf8(name));
                parts.Add(Lp("showtagonlywhentargeted"));
            }
            parts.Add(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });   // binary attributes
            parts.Add(Lp("generation"));
            parts.Add(Lp(code));
            parts.Add(Lp("dmgkb"));
            return Concat(parts.ToArray());
        }

        [Fact(DisplayName = "Species: truncated slot keys do not become the entity name")]
        public void Truncated_slot_keys_are_not_the_species()
        {
            var res = ScanEntities(HorseBlob("equusdestrier:horse-adult-female-destrier", "Moonlight"));
            var ent = Assert.Single(res.Chunks[0].Entities);

            Assert.Equal("equusdestrier:horse-adult-female-destrier", ent.Species);
            Assert.Equal("equusdestrier", ent.Domain);
            Assert.Equal("horse-adult-female-destrier", ent.CodePath);
            Assert.Equal("horse", ent.Kind);
            Assert.Equal("female-destrier", ent.SexAge);
            Assert.DoesNotContain(ent.SpeciesCodes, c => c.EndsWith("-"));
        }

        [Fact(DisplayName = "Species: the player name is read from nametag and is searchable")]
        public void Custom_name_is_read_from_nametag()
        {
            var res = ScanEntities(HorseBlob("equusdestrier:horse-adult-male-destrier", "Moonlight"));
            var ent = Assert.Single(res.Chunks[0].Entities);
            Assert.Equal("Moonlight", ent.CustomName);

            var row = EntityBrowser.Build(res).Single();
            Assert.Equal("Moonlight", row.Label);
            Assert.Single(EntityBrowser.Filter(EntityBrowser.Build(res), EntityCategory.All, "moonlight"));
        }

        [Fact(DisplayName = "Species: a player name in non-ASCII characters is not lost (UTF-8, not ASCII)")]
        public void Custom_name_supports_utf8()
        {
            var res = ScanEntities(HorseBlob("equusdestrier:horse-adult-male-destrier", "Lúna"));
            var ent = Assert.Single(res.Chunks[0].Entities);
            Assert.Equal("Lúna", ent.CustomName);
            Assert.Single(EntityBrowser.Filter(EntityBrowser.Build(res), EntityCategory.All, "lúna"));
        }

        [Fact(DisplayName = "Species: the attribute key `jaunt:stamina` does not pass itself off as the species")]
        public void Attribute_key_with_domain_is_not_the_species()
        {
            // the entity has no modded domain in its species code: the only `domain:path` is an attribute key
            var res = ScanEntities(HorseBlob("tameddeer-elk-male-adult", null));
            var ent = Assert.Single(res.Chunks[0].Entities);
            Assert.Equal("tameddeer-elk-male-adult", ent.Species);
            Assert.Equal("tameddeer", ent.Kind);
            Assert.Null(ent.Domain);
        }

        [Fact(DisplayName = "Species: with no \"bare\" token, the later domain:path is used (toad, bairdicrab)")]
        public void Species_without_hyphen_comes_from_the_late_domain_code()
        {
            // the path has no hyphen and is shorter than five characters — the "bare" token pass does not see it
            var blob = Concat(
                Lp("EntityAgent"), Lp("1.22.7"),
                Lp("nametag"), Lp("name"), Lp("Toady"),
                Lp("jaunt:stamina"),
                new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                Lp("feverstonewilds:toad"),
                Lp("tick"));
            var ent = Assert.Single(ScanEntities(blob).Chunks[0].Entities);
            Assert.Equal("feverstonewilds:toad", ent.Species);
            Assert.Equal("feverstonewilds", ent.Domain);
            Assert.Equal("toad", ent.Kind);
        }

        [Fact(DisplayName = "Species: a truncated game: code is not accepted")]
        public void Truncated_game_code_is_rejected()
        {
            var blob = Concat(Lp("EntityAgent"), Lp("1.22.7"), Lp("maxhealth"), new byte[] { 1 }, Lp("game:wolf-"));
            var ent = Assert.Single(ScanEntities(blob).Chunks[0].Entities);
            Assert.Null(ent.Species);
        }

        [Fact(DisplayName = "Species: a length byte that is itself printable does not stick to the domain")]
        public void Printable_length_byte_does_not_pollute_the_domain()
        {
            // the code string length is 48 = 0x30 = the character '0': without the framing check the domain would become `0crfish`
            const string code = "crfish:cagedfish-freshwater-perch-european-adult";
            Assert.Equal(48, code.Length);
            var ent = Assert.Single(ScanEntities(Concat(Lp("EntityAgent"), Lp("1.22.7"), Lp(code), Lp("tick")))
                .Chunks[0].Entities);
            Assert.Equal(code, ent.Species);
            Assert.Equal("crfish", ent.Domain);
            Assert.Equal("cagedfish-freshwater-perch-european-adult", ent.CodePath);
        }

        [Fact(DisplayName = "Species: the killer does not become the victim's species (deathByEntity)")]
        public void Killer_is_not_the_species_of_the_victim()
        {
            // the real layout: `deathByEntity` writes the killer's code, and its own code comes last
            var blob = Concat(
                Lp("EntityAgent"), Lp("1.22.7"),
                Lp("entityDead"), Lp("deathReason"), Lp("deathDamageType"),
                Lp("game:fox-red-adult-male"),                   // the one that did the killing
                Lp("fowlmod:duck-blue-hen"),                     // the duck itself
                Lp("dmgkb"), Lp("tick"));
            var ent = Assert.Single(ScanEntities(blob).Chunks[0].Entities);
            Assert.Equal("fowlmod:duck-blue-hen", ent.Species);
            Assert.Equal("fowlmod", ent.Domain);
            Assert.Equal("duck", ent.Kind);
        }

        [Fact(DisplayName = "Species: its own code, not the code of the fetus/item (a pregnant mare)")]
        public void Own_code_wins_over_nested_foetus_and_item_codes()
        {
            // in a pregnant mare the blob first holds the fetus code and the inventory item codes
            var blob = Concat(
                Lp("EntityDestrier"), Lp("1.22.7"),
                Lp("isPregnant"),
                Lp("equusdestrier:horse-baby-female-destrier"),
                Lp("equusdestrier:item-creature-horse-adult-female-destrier"),
                Lp("jaunt:stamina"),
                Lp("nametag"), Lp("name"), LpUtf8("Sunny"),
                Lp("slot-0"), Lp("baseseat-0"),
                Lp("equusdestrier:horse-adult-female-destrier"),   // its own code comes last
                Lp("dmgkb"));
            var ent = Assert.Single(ScanEntities(blob).Chunks[0].Entities);
            Assert.Equal("equusdestrier:horse-adult-female-destrier", ent.Species);
            Assert.Equal("female-destrier", ent.SexAge);   // the last two parts of the path (as before)
            Assert.Equal("Sunny", ent.CustomName);
        }

        [Fact(DisplayName = "Species: a behaviour token does not become the name (armor stand)")]
        public void Behaviour_token_is_not_the_species()
        {
            // on a real save this named 570 armor stands out of 581 entities
            var blob = Concat(
                Lp("EntityArmorStand"), Lp("1.22.7"),
                Lp("ownerUid"), Lp("seraphinventory"), Lp("slots"),
                Lp("entitybehavior-debuff-essenceexhaustion-eligible"),
                Lp("armorstand-aged"),
                Lp("tick"));
            var ent = Assert.Single(ScanEntities(blob).Chunks[0].Entities);
            Assert.Equal("armorstand-aged", ent.Species);
            Assert.Equal("armorstand", ent.Kind);
            Assert.Equal("aged", ent.SexAge);
        }

        [Fact(DisplayName = "Code: the path and the domain are parsed, including for a code without a domain")]
        public void Path_and_domain_helpers()
        {
            Assert.Equal("horse-adult-female-destrier", EntityInfo.PathOf("equusdestrier:horse-adult-female-destrier"));
            Assert.Equal("drifter-deep", EntityInfo.PathOf("drifter-deep"));
            Assert.Equal("", EntityInfo.PathOf(null));
            Assert.Equal("equusdestrier", EntityInfo.DomainOf("equusdestrier:horse-adult-female-destrier"));
            Assert.Equal("game", EntityInfo.DomainOf("game:wolf-eurasian-adult-male"));
            Assert.Null(EntityInfo.DomainOf("drifter-deep"));
            Assert.Null(EntityInfo.DomainOf(null));
        }
    }
}
