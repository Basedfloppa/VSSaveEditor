using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using VSCore;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>
    ///     An inventory in a save is a nested TreeAttribute tree, not flat ECX fields.
    ///     The tests build such trees byte by byte the way the game does and check that
    ///     items, slots, container coordinates and the player name are not lost.
    /// </summary>
    public class InventoryParserTests
    {
        private static byte[] Lp(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            return Cat(Varint(bytes.Length), bytes);
        }

        private static byte[] Varint(int value)
        {
            var ms = new MemoryStream();
            uint u = (uint)value;
            while ((u & ~0x7f) != 0)
            {
                ms.WriteByte((byte)((u & 0x7f) | 0x80));
                u >>= 7;
            }
            ms.WriteByte((byte)u);
            return ms.ToArray();
        }

        private static byte[] Cat(params byte[][] parts)
        {
            var ms = new MemoryStream();
            foreach (var p in parts) ms.Write(p, 0, p.Length);
            return ms.ToArray();
        }

        private static byte[] I32(int v) => BitConverter.GetBytes(v);
        private static byte[] I64(long v) => BitConverter.GetBytes(v);

        /// <summary>An int attribute: id 1 + LP key + int32.</summary>
        private static byte[] IntAttr(string key, int value) => Cat(new byte[] { 1 }, Lp(key), I32(value));

        /// <summary>A tree attribute: id 6 + LP key + contents + terminator.</summary>
        private static byte[] TreeAttr(string key, params byte[][] children)
            => Cat(new byte[] { 6 }, Lp(key), Cat(children), new byte[] { 0 });

        /// <summary>An itemstack attribute: id 7 + LP key + bool(not null) + class + id + count + attrs tree.</summary>
        private static byte[] ItemAttr(string slot, int cls, int id, int count, params byte[][] attrs)
            => Cat(new byte[] { 7 }, Lp(slot), new byte[] { 0 }, I32(cls), I32(id), I32(count), Cat(attrs), new byte[] { 0 });

        private static byte[] StringAttr(string key, string value) => Cat(new byte[] { 5 }, Lp(key), Lp(value));

        /// <summary>An inventory: qslots + slots holding items.</summary>
        private static byte[] Inventory(string name, int slots, params byte[][] items)
            => TreeAttr(name, IntAttr("qslots", slots), TreeAttr("slots", items));

        private static byte[] Entity(params byte[][] attrs)
        {
            // Entity.ToBytes: type, version, EntityId(int64), WatchedAttributes.
            return Cat(Lp("EntityAgent"), Lp("1.22.7"), I64(42), Cat(attrs), new byte[] { 0 });
        }

        [Fact(DisplayName = "Entity inventory: items, slot and stack attributes")]
        public void Entity_inventory_is_parsed()
        {
            var payload = Entity(Inventory("backpack", 20,
                ItemAttr("slot-2", 1, 42, 5, StringAttr("condition", "worn"))));
            var owner = new EntityInfo
            {
                Species = "game:wolf-eurasian-adult-male", CustomName = "Moonlight",
                ChunkX = 10, ChunkY = 2, ChunkZ = 20, Position = 123, Occurrence = 4
            };

            var invs = InventoryParser.ParseEntity(payload, owner, null, null);
            var inv = Assert.Single(invs);
            Assert.Equal("backpack", inv.Name);
            Assert.Equal(20, inv.SlotCount);
            var item = Assert.Single(inv.Items);
            Assert.Equal("slot-2", item.Slot);
            Assert.Equal(1, item.Class);
            Assert.Equal(42, item.Id);
            Assert.Equal(5, item.Count);
            Assert.Equal("item:42", item.DisplayCode);
            Assert.Contains(item.Attributes, a => a.Key == "condition" && a.Value == "worn");
            Assert.Equal("Moonlight", inv.OwnerLabel);
            Assert.Equal(InventoryOwnerKind.Entity, inv.OwnerKind);
            Assert.Equal(6, inv.Field);
        }

        [Fact(DisplayName = "Block entity inventory: posx/posy/posz coordinates and blockCode")]
        public void Block_entity_inventory_has_position()
        {
            var payload = Cat(Lp("Chest"),
                IntAttr("posx", 100), IntAttr("posy", 65), IntAttr("posz", -7),
                StringAttr("blockCode", "chest-east"),
                Inventory("inventory", 8, ItemAttr("3", 1, 7, 12)),
                new byte[] { 0 });
            var owner = new BlockEntityInfo
            {
                Type = "GenericTypedContainer", ChunkX = 3, ChunkY = 2, ChunkZ = 4,
                Position = 777, Occurrence = 1
            };

            var invs = InventoryParser.ParseBlockEntity(payload, owner, null, null);
            var inv = Assert.Single(invs);
            Assert.Equal("inventory", inv.Name);
            Assert.Equal(100, inv.X);
            Assert.Equal(65, inv.Y);
            Assert.Equal(-7, inv.Z);
            Assert.Equal("chest-east", inv.ContainerCode);
            var item = Assert.Single(inv.Items);
            Assert.Equal("3", item.Slot);
            Assert.Equal(12, item.Count);
            Assert.Equal(8, inv.Field);
        }

        [Fact(DisplayName = "Player inventory: the body root and the nested contents")]
        public void Player_inventory_and_nested_contents_are_parsed()
        {
            var body = Cat(
                IntAttr("qslots", 15),
                TreeAttr("slots",
                    ItemAttr("2", 1, 5, 1,
                        TreeAttr("contents", IntAttr("qslots", 1),
                            TreeAttr("slots", ItemAttr("0", 0, 99, 1))))),
                new byte[] { 0 });

            var invs = InventoryParser.ParsePlayerInventory(body, "character", null, null);
            Assert.NotEmpty(invs);
            var root = Assert.Single(invs, i => i.Name == "character");
            Assert.Equal(15, root.SlotCount);
            var item = Assert.Single(root.Items);
            Assert.Equal("2", item.Slot);
            Assert.Equal("item:5", item.DisplayCode);
            var nestedInv = Assert.Single(invs, i => i.Name == "contents");
            Assert.Equal("block:99", Assert.Single(nestedInv.Items).DisplayCode);
        }

        [Fact(DisplayName = "Player name: read from the nametag of the embedded EntityPlayer")]
        public void Player_entity_name_is_read()
        {
            var payload = Cat(Lp("EntityPlayer"), Lp("1.22.7"), I64(1),
                TreeAttr("nametag", StringAttr("name", "zorolin")), new byte[] { 0 });
            Assert.Equal("zorolin", InventoryParser.ParsePlayerEntityName(payload));
        }

        [Fact(DisplayName = "Inventory search: code, owner, container, count, chunk")]
        public void Inventory_query_matches_expected_rows()
        {
            var inv = new InventoryInfo
            {
                OwnerKind = InventoryOwnerKind.BlockEntity, OwnerLabel = "GenericTypedContainer",
                OwnerCode = "chest-east", ContainerCode = "chest-east", Name = "inventory",
                ChunkX = 10, ChunkY = 1, ChunkZ = 20, Position = 5, Occurrence = 0, Field = 8, SlotCount = 8
            };
            var stack = new ItemStackInfo { Class = 0, Id = 7, Count = 12, Code = "ore-quartz", Slot = "3" };
            inv.Items.Add(stack);
            var row = new ItemStackRow(inv, stack);

            Assert.True(row.Matches(InventoryQuery.Parse("quartz")));
            Assert.True(row.Matches(InventoryQuery.Parse("owner:chest")));
            Assert.True(row.Matches(InventoryQuery.Parse("container:inventory")));
            Assert.True(row.Matches(InventoryQuery.Parse("count>=12")));
            Assert.True(row.Matches(InventoryQuery.Parse("chunk:10,20")));
            Assert.True(row.Matches(InventoryQuery.Parse("slot:3")));
            Assert.True(row.Matches(InventoryQuery.Parse("key:b:5:0:inventory")));
            Assert.False(row.Matches(InventoryQuery.Parse("chunk:11,20")));
            Assert.False(row.Matches(InventoryQuery.Parse("count>12")));
            Assert.False(row.Matches(InventoryQuery.Parse("owner:zorolin")));
        }

        [Fact(DisplayName = "Registries: ItemIDs and BlockIDs are told apart by the same id")]
        public void Item_and_block_registries_are_separate()
        {
            var gamedata = Gamedata(("BlockIDs", new[] { (1, "soil-medium-normal") }),
                ("ItemIDs", new[] { (1, "clothes-upperbody-commoner-shirt") }));
            var blocks = BlockRegistry.FromGamedata(gamedata);
            var items = ItemRegistry.FromGamedata(gamedata);
            Assert.Equal("soil-medium-normal", blocks.CodeOf(1));
            Assert.Equal("clothes-upperbody-commoner-shirt", items.CodeOf(1));
        }

        /// <summary>gamedata: marker + tag field2/wire2 + LP payload of messages {field1=id, field2=code}.</summary>
        private static byte[] Gamedata(params (string Marker, (int Id, string Code)[] Entries)[] sections)
        {
            var ms = new MemoryStream();
            foreach (var (marker, entries) in sections)
            {
                var payload = new MemoryStream();
                foreach (var (id, code) in entries)
                {
                    var msg = Cat(new byte[] { 8 }, Varint(id), new byte[] { 18 }, Lp(code));
                    payload.Write(new byte[] { 18 }, 0, 1);
                    payload.Write(Varint(msg.Length), 0, Varint(msg.Length).Length);
                    payload.Write(msg, 0, msg.Length);
                }

                ms.Write(Lp(marker), 0, Lp(marker).Length);
                ms.WriteByte(0x12);
                var len = Varint((int)payload.Length);
                ms.Write(len, 0, len.Length);
                payload.WriteTo(ms);
            }

            return ms.ToArray();
        }

        [Fact(DisplayName = "Save scan: entity, block entity and player inventories all make it into the index")]
        public void Scanner_builds_inventory_index()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "invscan_" + Guid.NewGuid().ToString("N") + ".vcdbs");
            try
            {
                using (var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
                {
                    con.Open();
                    Exec(con, "CREATE TABLE chunk(position INTEGER PRIMARY KEY, data BLOB);");
                    Exec(con, "CREATE TABLE mapchunk(position INTEGER PRIMARY KEY, data BLOB);");
                    Exec(con, "CREATE TABLE mapregion(position INTEGER PRIMARY KEY, data BLOB);");
                    Exec(con, "CREATE TABLE gamedata(savegameid INTEGER PRIMARY KEY, data BLOB);");
                    Exec(con, "CREATE TABLE playerdata(playerid INTEGER PRIMARY KEY AUTOINCREMENT, playeruid TEXT, data BLOB);");

                    var entity = Entity(Inventory("backpack", 4, ItemAttr("0", 1, 5, 2)));
                    var blockEntity = Cat(Lp("Chest"), IntAttr("posx", 1), IntAttr("posy", 2), IntAttr("posz", 3),
                        Inventory("inventory", 4, ItemAttr("1", 0, 6, 3)), new byte[] { 0 });
                    var chunk = Cat(TagLen(6, entity), TagLen(8, blockEntity));

                    using var ins = con.CreateCommand();
                    ins.CommandText = "INSERT INTO chunk(position,data) VALUES(1,$d);";
                    ins.Parameters.AddWithValue("$d", chunk);
                    ins.ExecuteNonQuery();

                    using var gd = con.CreateCommand();
                    gd.CommandText = "INSERT INTO gamedata(savegameid,data) VALUES(1,$d);";
                    gd.Parameters.AddWithValue("$d", Gamedata(("BlockIDs", new[] { (6, "chest-east") }),
                        ("ItemIDs", new[] { (5, "ore-quartz") })));
                    gd.ExecuteNonQuery();

                    var player = Cat(TagLen(1, Lp("uid-1")),
                        TagLen(2, Cat(TagLen(1, Lp("backpack")),
                            TagLen(2, Cat(IntAttr("qslots", 2), TreeAttr("slots", ItemAttr("1", 1, 5, 4)), new byte[] { 0 })))),
                        TagLen(3, Cat(Lp("EntityPlayer"), Lp("1.22.7"), I64(7),
                            TreeAttr("nametag", StringAttr("name", "zorolin")), new byte[] { 0 })));
                    using var pi = con.CreateCommand();
                    pi.CommandText = "INSERT INTO playerdata(playerid,playeruid,data) VALUES(1,'uid-1',$d);";
                    pi.Parameters.AddWithValue("$d", player);
                    pi.ExecuteNonQuery();
                }

                using var save = VSSaveFile.OpenReadOnly(dbPath);
                var scan = SaveScanner.Scan(save, captureRaw: false);
                Assert.Equal(3, scan.Inventories.Count);
                Assert.Equal(3, scan.ItemStackCount);
                Assert.Contains(scan.Inventories, i => i.OwnerKind == InventoryOwnerKind.Entity && i.Items.Single().Code == "ore-quartz");
                Assert.Contains(scan.Inventories, i => i.OwnerKind == InventoryOwnerKind.BlockEntity && i.Items.Single().Code == "chest-east");
                var playerInv = Assert.Single(scan.Inventories, i => i.OwnerKind == InventoryOwnerKind.Player);
                Assert.Equal("zorolin", playerInv.OwnerLabel);
                Assert.Equal(4, playerInv.Items.Single().Count);
            }
            finally
            {
                if (File.Exists(dbPath)) File.Delete(dbPath);
            }
        }

        private static byte[] TagLen(int field, byte[] payload)
            => Cat(Varint((field << 3) | 2), Varint(payload.Length), payload);

        private static void Exec(SqliteConnection con, string sql)
        {
            using var c = con.CreateCommand();
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }
    }
}
