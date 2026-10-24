using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>Blob comparison for the edit preview (F3): ECX fields, nested inventories, raw bytes.</summary>
    public class BlobDiffTests
    {
        private static byte[] LP(string s)
        {
            var b = new byte[1 + s.Length];
            b[0] = (byte)s.Length;
            Encoding.ASCII.GetBytes(s, 0, s.Length, b, 1);
            return b;
        }

        private static byte[] Cat(params byte[][] parts)
        {
            var ms = new MemoryStream();
            foreach (var p in parts) ms.Write(p, 0, p.Length);
            return ms.ToArray();
        }

        private static byte[] Fld(byte type, string key, byte[] value) => Cat(new[] { type }, LP(key), value);
        private static byte[] Str(string key, string value) => Fld(5, key, LP(value));

        /// <summary>Creature ECX blob: LP(type) + LP(version) + fields.</summary>
        private static byte[] Entity(string type, params byte[][] fields)
        {
            var parts = new List<byte[]> { LP(type), LP("1.0") };
            parts.AddRange(fields);
            return Cat(parts.ToArray());
        }

        private static byte[] Top(params (int field, byte[] payload)[] items)
        {
            var ms = new MemoryStream();
            foreach (var (field, payload) in items)
            {
                WriteVarint(ms, ((long)field << 3) | 2);
                WriteVarint(ms, payload.Length);
                ms.Write(payload, 0, payload.Length);
            }
            return ms.ToArray();
        }

        private static void WriteVarint(Stream ms, long v)
        {
            ulong u = (ulong)v;
            while (u >= 0x80) { ms.WriteByte((byte)((u & 0x7F) | 0x80)); u >>= 7; }
            ms.WriteByte((byte)u);
        }

        [Fact]
        public void Payload_reports_a_changed_entity_field()
        {
            var oldB = Entity("EntityAgent", Str("dmgTier", "2"), Str("health", "15"));
            var newB = Entity("EntityAgent", Str("dmgTier", "3"), Str("health", "15"));

            var rows = BlobDiff.Payload(6, "entity[0]", oldB, newB);

            var row = Assert.Single(rows);
            Assert.Equal("dmgTier", row.Field);
            Assert.Equal("2", row.OldValue);
            Assert.Equal("3", row.NewValue);
            Assert.False(row.ByteLevel);
        }

        [Fact]
        public void Payload_reports_added_and_removed_fields()
        {
            var oldB = Entity("EntityAgent", Str("gone", "x"), Str("kept", "same"));
            var newB = Entity("EntityAgent", Str("kept", "same"), Str("fresh", "y"));

            var rows = BlobDiff.Payload(6, "entity[0]", oldB, newB);

            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.Field == "gone" && r.NewValue == "(removed)" && r.OldValue == "x");
            Assert.Contains(rows, r => r.Field == "fresh" && r.OldValue == "(added)" && r.NewValue == "y");
        }

        [Fact]
        public void Payload_distinguishes_repeated_keys()
        {
            var oldB = Entity("EntityAgent", Str("tag", "a"), Str("tag", "b"));
            var newB = Entity("EntityAgent", Str("tag", "a"), Str("tag", "c"));

            var rows = BlobDiff.Payload(6, "entity[0]", oldB, newB);

            var row = Assert.Single(rows);
            Assert.Equal("tag#2", row.Field);
            Assert.Equal("b", row.OldValue);
            Assert.Equal("c", row.NewValue);
        }

        [Fact]
        public void Payload_falls_back_to_bytes_when_the_field_is_not_ecx()
        {
            var oldB = new byte[] { 1, 2, 3 };
            var newB = new byte[] { 1, 9, 3, 4 };

            var rows = BlobDiff.Payload(3, "field 3[0]", oldB, newB);

            var row = Assert.Single(rows);
            Assert.True(row.ByteLevel);
            Assert.Equal("3 B", row.OldValue);
            Assert.Contains("2 byte(s) differ", row.NewValue);
        }

        [Fact]
        public void Payload_reports_a_missing_old_value()
        {
            var rows = BlobDiff.Payload(8, "block entity[0]", null, new byte[] { 1, 2 });
            var row = Assert.Single(rows);
            Assert.Equal("(no value)", row.OldValue);
            Assert.Equal("2 B", row.NewValue);
        }

        [Fact]
        public void Payload_reports_a_header_only_change_as_bytes()
        {
            // the fields are identical, but the type in the header changed: this must not go unreported
            var oldB = Entity("EntityAgent", Str("health", "15"));
            var newB = Entity("EntityAgent2", Str("health", "15"));

            var rows = BlobDiff.Payload(6, "entity[0]", oldB, newB);
            var row = Assert.Single(rows);
            Assert.True(row.ByteLevel);
            Assert.Contains("byte(s) differ", row.NewValue);
        }

        [Fact]
        public void CountDiffering_counts_length_delta_too()
        {
            Assert.Equal(0, BlobDiff.CountDiffering(new byte[] { 1, 2 }, new byte[] { 1, 2 }));
            Assert.Equal(1, BlobDiff.CountDiffering(new byte[] { 1, 2 }, new byte[] { 1, 3 }));
            Assert.Equal(1, BlobDiff.CountDiffering(new byte[] { 1, 2 }, new byte[] { 1, 2, 3 }));
        }

        [Fact]
        public void TopPayload_finds_the_requested_occurrence()
        {
            var entity = Entity("EntityAgent", Str("health", "15"));
            var other = new byte[] { 7, 7 };
            var blob = Top((1, new byte[] { 0, 0 }), (6, entity), (6, other));

            Assert.Equal(entity, BlobDiff.TopPayload(blob, 6, 0));
            Assert.Equal(other, BlobDiff.TopPayload(blob, 6, 1));
            Assert.Null(BlobDiff.TopPayload(blob, 8, 0));
        }

        [Fact]
        public void Player_diff_points_at_the_inventory_and_the_field()
        {
            var oldBody = Cat(Str("itemcode", "game:axe"), Str("stacksize", "1"));
            var newBody = Cat(Str("itemcode", "game:axe"), Str("stacksize", "5"));
            var oldBlob = PlayerBlob("uid-1", "backpack", oldBody);
            var newBlob = PlayerBlob("uid-1", "backpack", newBody);

            var rows = BlobDiff.Player("inventory[0]", oldBlob, newBlob);

            var row = Assert.Single(rows);
            Assert.Equal("inventory[0] 'backpack' · stacksize", row.Field);
            Assert.Equal("1", row.OldValue);
            Assert.Equal("5", row.NewValue);
        }

        [Fact]
        public void Player_diff_reports_a_uid_change()
        {
            var body = Str("itemcode", "game:axe");
            var rows = BlobDiff.Player("blob", PlayerBlob("uid-1", "hotbar", body), PlayerBlob("uid-2", "hotbar", body));
            var row = Assert.Single(rows);
            Assert.Equal("uid", row.Field);
        }

        /// <summary>playerdata: field1 = uid, field2 = inventory {field1 = name, field2 = ECX body}.</summary>
        private static byte[] PlayerBlob(string uid, string inventory, byte[] body)
        {
            var inv = Top((1, Encoding.ASCII.GetBytes(inventory)), (2, body));
            return Top((1, Encoding.ASCII.GetBytes(uid)), (2, inv));
        }
    }
}
