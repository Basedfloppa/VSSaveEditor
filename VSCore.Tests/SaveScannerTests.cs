using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;

namespace VSCore.Tests
{
    public class SaveScannerTests
    {
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

        private static byte[] Concat(params byte[][] parts)
        {
            var ms = new MemoryStream();
            foreach (var p in parts) ms.Write(p, 0, p.Length);
            return ms.ToArray();
        }

        private static byte[] LP(string s)   // length-prefixed string: [len][ascii]
        {
            var b = new byte[1 + s.Length];
            b[0] = (byte)s.Length;
            Encoding.ASCII.GetBytes(s, 0, s.Length, b, 1);
            return b;
        }

        /// <summary>All "name = value" fields are extracted from the entity ECX blob.</summary>
        [Fact]
        public void Entity_fields_extracted_as_key_value()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "scanf_" + Guid.NewGuid().ToString("N") + ".vcdbs");
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
                    byte[] e = Concat(LP("EntityAgent"), LP("1.22.6"), LP("dmgTier"), LP("Damage tier: 0"), LP("animalWeight"), new byte[] { 0x2a });
                    using var ins = con.CreateCommand();
                    ins.CommandText = "INSERT INTO chunk(position,data) VALUES(1,$d);";
                    ins.Parameters.AddWithValue("$d", TagLenDelim(6, e));
                    ins.ExecuteNonQuery();
                }
                using (var save = VSSaveFile.OpenReadOnly(dbPath))
                {
                    var res = SaveScanner.Scan(save, captureRaw: false);
                    var ent = Assert.Single(res.Chunks[0].Entities);
                    Assert.Contains("dmgTier = Damage tier: 0", ent.Fields);
                    Assert.Contains("EntityAgent = 1.22.6", ent.Fields);
                    Assert.Contains("animalWeight = 42", ent.Fields);
                }
            }
            finally { if (File.Exists(dbPath)) File.Delete(dbPath); }
        }

        private static void Exec(SqliteConnection con, string sql)
        {
            using var c = con.CreateCommand();
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }

        /// <summary>The scan collects all 5 tables: chunk (blocks+entity+block entity), mapchunk (raw),
        /// playerdata, plus the raw bytes of every blob.</summary>
        [Fact]
        public void Scan_collects_all_five_tables_and_raw_blobs()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), "scan_" + Guid.NewGuid().ToString("N") + ".vcdbs");
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

                    byte[] blockEmpty = { 0, 0, 0, 0 }; // int32 num=0 => the whole chunk is air
                    // blobs as in a real save: strings are length-prefixed, the entity's own code is the last
                    // of them (that is how VS writes it; see EntityCodeTests)
                    byte[] entity = Concat(LP("EntityWolf"), LP("1.22.7"), LP("maxhealth"),
                                           LP("game:wolf-eurasian-adult-male"), LP("wasFedToAdulthood"), LP("dmgkb"));
                    byte[] entityBare = Concat(LP("EntityBowtorn"), LP("bowtorn-nightmare"), LP("maxhealth"),
                                               LP("health"), LP("tick")); // a species without the game: prefix
                    byte[] be = Encoding.ASCII.GetBytes("Spawner entityCodes game:drifter-normal meta-spawner");

                    var chunkBlob = new MemoryStream();
                    foreach (var part in new[] { TagLenDelim(1, blockEmpty), TagLenDelim(6, entity), TagLenDelim(6, entityBare), TagLenDelim(8, be) })
                        chunkBlob.Write(part, 0, part.Length);

                    using (var ins = con.CreateCommand())
                    { ins.CommandText = "INSERT INTO chunk(position,data) VALUES(1, $d);"; ins.Parameters.AddWithValue("$d", chunkBlob.ToArray()); ins.ExecuteNonQuery(); }
                    using (var ins = con.CreateCommand())
                    { ins.CommandText = "INSERT INTO mapchunk(position,data) VALUES(2, $d);"; ins.Parameters.AddWithValue("$d", new byte[] { 1, 2, 3, 4 }); ins.ExecuteNonQuery(); }
                    using (var ins = con.CreateCommand())
                    { ins.CommandText = "INSERT INTO playerdata(playerid,playeruid,data) VALUES(1,'uid1',$d);"; ins.Parameters.AddWithValue("$d", new byte[] { 9 }); ins.ExecuteNonQuery(); }
                }

                using (var save = VSSaveFile.OpenReadOnly(dbPath))
                {
                    var res = SaveScanner.Scan(save, captureRaw: true);

                    Assert.Single(res.Chunks);
                    Assert.Single(res.Mapchunks);
                    Assert.Equal(1, res.Meta.MapchunkCount);
                    Assert.True(res.Meta.PlayerCount >= 1);

                    // the raw bytes of each blob are captured
                    Assert.NotNull(res.Chunks[0].Raw);
                    Assert.NotNull(res.Mapchunks[0].Raw);

                    // field6 entities are recognized with both a game: code and a "bare" species code
                    Assert.Equal(2, res.Chunks[0].Entities.Count);
                    var ent = res.Chunks[0].Entities.First(e => e.Species == "bowtorn-nightmare");
                    Assert.True(ent.Attributes.ContainsKey("maxhealth"));
                    var wolf = res.Chunks[0].Entities.First(e => e.Species == "game:wolf-eurasian-adult-male");
                    Assert.StartsWith("game:wolf", wolf.Species);
                    Assert.True(wolf.Attributes.ContainsKey("maxhealth"));
                    Assert.True(wolf.Attributes.ContainsKey("wasFedToAdulthood"));

                    // addressing for edits: the chunk row key + the field 6 ordinal (that is how the dump
                    // card finds the entity inside the blob — like WalkTop/ChunkDataParser)
                    Assert.Equal(1, wolf.Position);
                    Assert.Equal(1, ent.Position);
                    Assert.Equal(0, wolf.Occurrence);
                    Assert.Equal(1, ent.Occurrence);

                    // field8 block entities are addressed like entities: without this list "Entities"
                    // could not open a chest/spawner found by the scan in the editor
                    Assert.True(res.Chunks[0].BlockEntities.Count >= 1);
                    var be = res.Chunks[0].BlockEntities[0];
                    Assert.Equal(1, be.Position);
                    Assert.Equal(0, be.Occurrence);
                    Assert.Equal(1, be.ChunkX); // position=1 decodes to cx=1, cy=0, cz=0
                    Assert.Equal(0, be.ChunkZ);

                    // field1 num=0 => all 32768 cells are air
                    Assert.Equal(32768, res.Chunks[0].AirCells);
                }
            }
            finally { if (File.Exists(dbPath)) File.Delete(dbPath); }
        }
    }
}
