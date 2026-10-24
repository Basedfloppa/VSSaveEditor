using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;

namespace VSCore.Tests
{
    public class ChunkDataEditorTests
    {
        private static byte[] LP(string s) { var b = new byte[1 + s.Length]; b[0] = (byte)s.Length; Encoding.ASCII.GetBytes(s, 0, s.Length, b, 1); return b; }
        private static byte[] Cat(params byte[][] ps) { var ms = new MemoryStream(); foreach (var p in ps) ms.Write(p, 0, p.Length); return ms.ToArray(); }
        private static byte[] F(float v) => BitConverter.GetBytes(v);
        private static byte[] D(double v) => BitConverter.GetBytes(v);

        private static byte[] EntityBlob()
        {
            return Cat(
                LP("EntityAgent"), LP("1.22.6"),
                new byte[] { 0x06 }, LP("animations"), new byte[] { 0x00 },
                new byte[] { 0x05 }, LP("dmgTier"), LP("Damage tier: 2"),
                new byte[] { 0x06 }, LP("health"),
                    new byte[] { 0x04 }, LP("basemaxhealth"), F(15f),
                    new byte[] { 0x04 }, LP("currenthealth"), F(15f),
                    new byte[] { 0x00 },
                new byte[] { 0x02 }, LP("herdId"), BitConverter.GetBytes(244741L),
                new byte[] { 0x03 }, LP("totalHoursDead"), D(1234.5),
                new byte[] { 0x09 }, LP("doesEat"), new byte[] { 0x01 },
                new byte[] { 0x08 }, LP("blob"), new byte[] { 0x03, 0x00, 0x01, 0x02, 0x03 }
            );
        }

        private static byte[] ChunkBlobWith(byte[] entityBlob)
        {
            // field1 holds blocks (num=0 means the whole chunk is air), field12 the version, field6 the entity
            byte[] f1 = { 0, 0, 0, 0 };
            byte[] ver = LP("1.22.6");
            return Cat(
                new byte[] { 0x0A }, WriteLen(f1.Length), f1,                      // field1 len-delim
                new byte[] { 0x62 }, WriteLen(ver.Length), ver,                     // field12 version
                new byte[] { 0x32 }, WriteLen(entityBlob.Length), entityBlob       // field6 (tag = 6<<3|2 = 0x32)
            );
        }

        private static byte[] WriteLen(int n)
        {
            var ms = new MemoryStream();
            uint u = (uint)n;
            while (u >= 0x80) { ms.WriteByte((byte)((u & 0x7F) | 0x80)); u >>= 7; }
            ms.WriteByte((byte)u);
            return ms.ToArray();
        }

        /// <summary>Re-serialization with no edits is byte-for-byte identical to the source (lossless round-trip).</summary>
        [Fact]
        public void Reserialize_without_edits_is_byte_identical()
        {
            var blob = EntityBlob();
            var rec = ChunkDataParser.ParseEntity(blob);
            var out2 = ChunkDataEditor.SerializeEntity(rec);
            Assert.Equal(blob, out2);
        }

        /// <summary>Editing a Float field: the value changes, the other fields and bytes stay untouched.</summary>
        [Fact]
        public void Edit_float_value_round_trips()
        {
            var blob = EntityBlob();
            var rec = ChunkDataParser.ParseEntity(blob);
            var hp = rec.Fields.First(f => f.Key == "basemaxhealth");
            ChunkDataEditor.SetFloat(hp, 20.5f);
            var out2 = ChunkDataEditor.SerializeEntity(rec);

            var rec2 = ChunkDataParser.ParseEntity(out2);
            Assert.Equal(20.5f, rec2.Fields.First(f => f.Key == "basemaxhealth").FloatValue, 3);
            Assert.Equal(15f, rec2.Fields.First(f => f.Key == "currenthealth").FloatValue, 3);   // untouched
            Assert.Equal(244741L, rec2.Fields.First(f => f.Key == "herdId").IntValue);
            Assert.Equal("Damage tier: 2", rec2.Fields.First(f => f.Key == "dmgTier").StringValue);
            // lossless layout: re-serialization does not change the blob
            Assert.Equal(out2, ChunkDataEditor.SerializeEntity(rec2));
        }

        /// <summary>Editing Int64 / Double / String / null — every type is rebuilt correctly.</summary>
        [Fact]
        public void Edit_all_value_kinds()
        {
            var blob = EntityBlob();
            var rec = ChunkDataParser.ParseEntity(blob);
            ChunkDataEditor.SetInt(rec.Fields.First(f => f.Key == "herdId"), 999);
            ChunkDataEditor.SetDouble(rec.Fields.First(f => f.Key == "totalHoursDead"), -1.25);
            ChunkDataEditor.SetString(rec.Fields.First(f => f.Key == "dmgTier"), "Damage tier: 5");
            ChunkDataEditor.SetBool(rec.Fields.First(f => f.Key == "doesEat"), false);
            ChunkDataEditor.SetBytes(rec.Fields.First(f => f.Key == "blob"), new byte[] { 0xAA, 0xBB });
            var out2 = ChunkDataEditor.SerializeEntity(rec);

            var rec2 = ChunkDataParser.ParseEntity(out2);
            Assert.Equal(999, rec2.Fields.First(f => f.Key == "herdId").IntValue);
            Assert.Equal(-1.25, rec2.Fields.First(f => f.Key == "totalHoursDead").DoubleValue, 3);
            Assert.Equal("Damage tier: 5", rec2.Fields.First(f => f.Key == "dmgTier").StringValue);
            Assert.False(rec2.Fields.First(f => f.Key == "doesEat").BoolValue);
            Assert.Equal(new byte[] { 0xAA, 0xBB }, rec2.Fields.First(f => f.Key == "blob").BytesValue);
            // a string edit with a different length: the LP length is rebuilt correctly
            ChunkDataEditor.SetString(rec2.Fields.First(f => f.Key == "dmgTier"), "X");
            var out3 = ChunkDataEditor.SerializeEntity(rec2);
            var rec3 = ChunkDataParser.ParseEntity(out3);
            Assert.Equal("X", rec3.Fields.First(f => f.Key == "dmgTier").StringValue);
            Assert.Equal(15f, rec3.Fields.First(f => f.Key == "basemaxhealth").FloatValue, 3);
        }

        /// <summary>Splicing the edited field6 into the top-level chunk blob: blocks/version stay untouched, the entity is updated.</summary>
        [Fact]
        public void Splice_edited_entity_into_chunk_blob()
        {
            var entityBlob = EntityBlob();
            var chunkBlob = ChunkBlobWith(entityBlob);

            var rec = ChunkDataParser.ParseEntity(entityBlob);
            ChunkDataEditor.SetFloat(rec.Fields.First(f => f.Key == "currenthealth"), 7.5f);
            var newEntity = ChunkDataEditor.SerializeEntity(rec);

            var newChunk = ChunkDataEditor.SplicePayloads(chunkBlob, new[] { (6, 0, newEntity) });

            // top-level walk: field1 (blocks) and field12 (version) match the source byte for byte
            var top = ChunkDataEditor.WalkTop(chunkBlob);
            var newTop = ChunkDataEditor.WalkTop(newChunk);
            Assert.Equal(top.Count, newTop.Count);
            foreach (var (t, n) in top.Zip(newTop))
            {
                if (t.Field == 6) continue;   // this is the only one that changed
                Assert.Equal(chunkBlob[t.Start..t.PayloadEnd], newChunk[n.Start..n.PayloadEnd]);
            }
            // the entity is re-parsed with the new value
            var f6 = ChunkDataEditor.WalkTop(newChunk).First(x => x.Field == 6);
            var payload = newChunk[f6.PayloadStart..f6.PayloadEnd];
            var rec2 = ChunkDataParser.ParseEntity(payload);
            Assert.Equal(7.5f, rec2.Fields.First(f => f.Key == "currenthealth").FloatValue, 3);
            Assert.Equal(15f, rec2.Fields.First(f => f.Key == "basemaxhealth").FloatValue, 3);
        }

        /// <summary>Copy + backup: the original stays byte-for-byte untouched, the copy holds the new value.</summary>
        [Fact]
        public void Copy_apply_changes_original_untouched()
        {
            var src = Path.Combine(Path.GetTempPath(), "ed_" + Guid.NewGuid().ToString("N") + ".vcdbs");
            var cs = new SqliteConnectionStringBuilder { DataSource = src }.ToString();
            using (var con = new SqliteConnection(cs))
            {
                con.Open();
                Exec(con, "CREATE TABLE chunk(position INTEGER PRIMARY KEY, data BLOB);");
                using var ins = con.CreateCommand();
                ins.CommandText = "INSERT INTO chunk(position,data) VALUES(123,$d);";
                ins.Parameters.AddWithValue("$d", new byte[] { 1, 2, 3 });
                ins.ExecuteNonQuery();
            }

            var before = File.ReadAllBytes(src);
            try
            {
                var (copy, backup) = VSSaveFile.CopyAndApplyChunkChanges(src, new[] { (123L, new byte[] { 9, 9, 9, 9 }) });

                // the original is unchanged
                Assert.Equal(before, File.ReadAllBytes(src));
                // the backup exists and equals the original
                Assert.True(File.Exists(backup));
                Assert.Equal(before, File.ReadAllBytes(backup));
                // the copy holds the new value
                using var v = VSSaveFile.OpenReadOnly(copy);
                var row = v.ChunkRowsWithData.Single();
                Assert.Equal(123L, row.Position);
                Assert.Equal(new byte[] { 9, 9, 9, 9 }, row.Data);
            }
            finally { if (File.Exists(src)) File.Delete(src); }
        }

        /// <summary>Copy + edit of a real wolf: health changes in the copy, the original stays untouched.</summary>
        [Fact]
        public void Real_wolf_edit_on_copy_original_untouched()
        {
            const string RealSave = "/home/user/.config/VintagestoryData/Saves/dark cave valley.vcdbs";
            if (!File.Exists(RealSave)) return;
            const long wolfPos = 54045349051907689L;

            using var save = VSSaveFile.OpenReadOnly(RealSave);
            var blob = save.ReadChunk(wolfPos)!;
            var pc = ChunkDataParser.Parse(blob, save.BlockRegistry, wolfPos);
            var wolf = pc.Entities.First(e => e.GameCodes.Any(g => g.StartsWith("game:wolf")));
            var hp = wolf.Fields.First(f => f.Key == "currenthealth");
            float before = hp.FloatValue;
            float baseBefore = wolf.Fields.First(f => f.Key == "basemaxhealth").FloatValue;
            ChunkDataEditor.SetFloat(hp, before + 1f);

            // rebuild field6: find its index in the top-level blob
            var top = ChunkDataEditor.WalkTop(blob);
            var occ = top.Where(t => t.Field == 6).ToList();
            var idx = occ.FindIndex(t => blob[t.PayloadStart..t.PayloadEnd].SequenceEqual(wolf.RawBytes));
            Assert.True(idx >= 0, "the wolf field6 was not found in the blob");
            var newEntity = ChunkDataEditor.SerializeEntity(wolf);
            var newChunk = ChunkDataEditor.SplicePayloads(blob, new[] { (6, idx, newEntity) });

            var srcBytes = File.ReadAllBytes(RealSave);
            var (copy, backup) = VSSaveFile.CopyAndApplyChunkChanges(RealSave, new[] { (wolfPos, newChunk) });
            try
            {
                Assert.Equal(srcBytes, File.ReadAllBytes(RealSave));   // the original is untouched
                using var v = VSSaveFile.OpenReadOnly(copy);
                var newBlob = v.ReadChunk(wolfPos)!;
                var pc2 = ChunkDataParser.Parse(newBlob, v.BlockRegistry, wolfPos);
                var wolf2 = pc2.Entities.First(e => e.GameCodes.Any(g => g.StartsWith("game:wolf")));
                Assert.Equal(before + 1f, wolf2.Fields.First(f => f.Key == "currenthealth").FloatValue, 3);
                Assert.Equal(baseBefore, wolf2.Fields.First(f => f.Key == "basemaxhealth").FloatValue, 3);  // the other field is untouched
                Assert.True(File.Exists(backup));
            }
            finally
            {
                var dir = Path.GetDirectoryName(copy);
                if (dir is { } d && Directory.Exists(d))
                    try { Directory.Delete(d, true); } catch { }
            }
        }

        private static void Exec(SqliteConnection con, string sql)
        {
            using var c = con.CreateCommand();
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }
    }
}
