using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace VSCore.Tests
{
    public class PlayerDataParserTests
    {
        private static byte[] LP(string s) { var b = new byte[1 + s.Length]; b[0] = (byte)s.Length; Encoding.ASCII.GetBytes(s, 0, s.Length, b, 1); return b; }
        private static byte[] Cat(params byte[][] ps) { var ms = new MemoryStream(); foreach (var p in ps) ms.Write(p, 0, p.Length); return ms.ToArray(); }
        private static byte[] TagLen(int field, byte[] payload)
        {
            var ms = new MemoryStream();
            ms.WriteByte((byte)((field << 3) | 2));
            ms.WriteByte((byte)payload.Length);
            ms.Write(payload, 0, payload.Length);
            return ms.ToArray();
        }

        /// <summary>playerdata: field1=uid, field2=message{field1=name, field2=ECX body}.</summary>
        [Fact]
        public void Player_inventories_parsed()
        {
            byte[] ecx = Cat(
                new byte[] { 0x01 }, LP("qslots"), BitConverter.GetBytes(12),
                new byte[] { 0x05 }, LP("type"), LP("leather"),
                new byte[] { 0x06 }, LP("extraInfoText"), new byte[] { 0x00 }
            );
            byte[] invMsg = Cat(TagLen(1, LP("hotbar-abc")), TagLen(2, ecx));
            byte[] blob = Cat(TagLen(1, LP("uid-1")), TagLen(2, invMsg));

            var pd = PlayerDataParser.Parse(blob, 1);
            Assert.Equal("uid-1", pd.Uid);
            var inv = Assert.Single(pd.Inventories);
            Assert.Equal("hotbar-abc", inv.Name);
            Assert.Equal(12, Assert.Single(inv.Fields, f => f.Key == "qslots").IntValue);
            Assert.Equal("leather", Assert.Single(inv.Fields, f => f.Key == "type").StringValue);
        }

        /// <summary>Replacing the inventory ECX body: the name does not change, the fields are updated, the uid stays untouched.</summary>
        [Fact]
        public void Replace_inventory_body_round_trips()
        {
            byte[] ecx = Cat(new byte[] { 0x05 }, LP("type"), LP("leather"));
            byte[] invMsg = Cat(TagLen(1, LP("backpack")), TagLen(2, ecx));
            byte[] blob = Cat(TagLen(1, LP("uid-1")), TagLen(2, invMsg));

            // the edit: type = "iron"
            var pd = PlayerDataParser.Parse(blob, 1);
            var fld = Assert.Single(pd.Inventories).Fields.First(f => f.Key == "type");
            ChunkDataEditor.SetString(fld, "iron");
            byte[] newBody = Cat(fld.Raw);

            var newBlob = PlayerDataParser.ReplaceInventoryBody(blob, "backpack", newBody);
            var pd2 = PlayerDataParser.Parse(newBlob, 1);
            Assert.Equal("uid-1", pd2.Uid);
            var inv2 = Assert.Single(pd2.Inventories);
            Assert.Equal("backpack", inv2.Name);
            Assert.Equal("iron", Assert.Single(inv2.Fields, f => f.Key == "type").StringValue);
        }

        /// <summary>Inventory message: field1=name, field2=ECX body with a single string field type.</summary>
        private static byte[] InvMsg(string name, string type)
        {
            byte[] ecx = Cat(new byte[] { 0x05 }, LP("type"), LP(type));
            return Cat(TagLen(1, LP(name)), TagLen(2, ecx));
        }

        /// <summary>
        /// Two inventories with the SAME name: an edit by index must change only the one given,
        /// not "the first one it finds" (B3).
        /// </summary>
        [Fact]
        public void Replace_inventory_body_by_index_picks_the_right_duplicate()
        {
            byte[] blob = Cat(
                TagLen(1, LP("uid-1")),
                TagLen(2, InvMsg("backpack", "leather")),
                TagLen(2, InvMsg("backpack", "wool")));

            var pd = PlayerDataParser.Parse(blob, 1);
            Assert.Equal(2, pd.Inventories.Count);
            Assert.Equal(new[] { "backpack", "backpack" }, pd.Inventories.Select(i => i.Name).ToArray());

            // edit the SECOND inventory (index 1)
            var fld = pd.Inventories[1].Fields.First(f => f.Key == "type");
            ChunkDataEditor.SetString(fld, "iron");
            byte[] newBody = Cat(fld.Raw);

            var newBlob = PlayerDataParser.ReplaceInventoryBody(blob, 1, newBody);

            var pd2 = PlayerDataParser.Parse(newBlob, 1);
            Assert.Equal("uid-1", pd2.Uid);
            Assert.Equal(2, pd2.Inventories.Count);
            Assert.Equal("leather", pd2.Inventories[0].Fields.Single(f => f.Key == "type").StringValue);
            Assert.Equal("iron", pd2.Inventories[1].Fields.Single(f => f.Key == "type").StringValue);
        }

        /// <summary>A name that occurs several times is ambiguous: an edit by name must fail instead of guessing.</summary>
        [Fact]
        public void Replace_inventory_body_by_ambiguous_name_throws()
        {
            byte[] blob = Cat(
                TagLen(1, LP("uid-1")),
                TagLen(2, InvMsg("backpack", "leather")),
                TagLen(2, InvMsg("backpack", "wool")));

            var ex = Assert.Throws<ArgumentException>(() => PlayerDataParser.ReplaceInventoryBody(blob, "backpack", new byte[] { 0x05 }));
            Assert.Contains("ambiguous", ex.Message);
        }

        /// <summary>An index beyond the inventory list is an explicit error, not an edit of the wrong block.</summary>
        [Fact]
        public void Replace_inventory_body_by_index_out_of_range_throws()
        {
            byte[] blob = Cat(TagLen(1, LP("uid-1")), TagLen(2, InvMsg("backpack", "leather")));
            Assert.Throws<ArgumentException>(() => PlayerDataParser.ReplaceInventoryBody(blob, 5, new byte[] { 0x05 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => PlayerDataParser.ReplaceInventoryBody(blob, -1, new byte[] { 0x05 }));
        }

        /// <summary>Real save: playerdata parses (uid + inventories with fields).</summary>
        [Fact]
        public void Real_playerdata_parsed()
        {
            const string RealSave = "/home/user/.config/VintagestoryData/Saves/dark cave valley.vcdbs";
            if (!File.Exists(RealSave)) return;
            using var save = VSSaveFile.OpenReadOnly(RealSave);
            var players = save.PlayerRows;
            Assert.NotEmpty(players);
            var p = players[0];
            var pd = PlayerDataParser.Parse(p.Data, p.Id);
            Assert.NotNull(pd.Uid);
            Assert.NotEmpty(pd.Inventories);
            Assert.True(pd.HasParsedFields, "not a single inventory field was parsed");
            // lossless: rebuilding the ECX body does not change the bytes
            var first = pd.Inventories[0];
            byte[] rebuilt = ChunkDataEditor.SerializeFields(first.Fields);
            Assert.Equal(first.RawBytes, rebuilt);
        }
    }
}
