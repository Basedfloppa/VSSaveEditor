using System;
using System.IO;
using Microsoft.Data.Sqlite;
using VSCore;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>P0-5/P0-10: table names and paths must not break out into SQL or the connection string.</summary>
    public class VSSaveFileSafetyTests
    {
        [Theory]
        [InlineData("chunk")]
        [InlineData("mapchunk")]
        [InlineData("playerdata")]
        [InlineData("_x1")]
        public void SafeTableNames_Accepted(string name) => VSSaveFile.EnsureSafeTableName(name);

        [Theory]
        [InlineData("")]
        [InlineData("1abc")]
        [InlineData("chunk; DROP TABLE chunk; --")]
        [InlineData("chunk\"")]
        [InlineData("ch unk")]
        [InlineData("mapregion\n")]
        [InlineData("naïve")]
        public void UnsafeTableNames_Rejected(string name)
            => Assert.Throws<ArgumentException>(() => VSSaveFile.EnsureSafeTableName(name));

        [Fact]
        public void CopyAndApplyChanges_UnsafeTable_ThrowsBeforeTouchingDisk()
        {
            // srcPath does not exist on purpose: if the check ran after the copy was made, we would get a
            // "file not found" error instead of ArgumentException, and the disk would already be touched
            var changes = new[] { ("chunk\"; DROP TABLE chunk; --", 1L, new byte[] { 1 }) };
            var ex = Assert.Throws<ArgumentException>(
                () => VSSaveFile.CopyAndApplyChanges("/nonexistent/save.vcdbs", changes));
            Assert.Contains("unsafe table name", ex.Message);
        }

        [Theory]
        [InlineData("/tmp/a.vcdbs", "a.vcdbs")]
        [InlineData("/tmp/../..", "save.vcdbs")]
        [InlineData("", "save.vcdbs")]
        [InlineData("/tmp/a\\b.vcdbs", "save.vcdbs")]
        public void SafeCopyFileName_KeepsOnlyPlainNames(string src, string expected)
            => Assert.Equal(expected, VSSaveFile.SafeCopyFileName(src));

        [Fact]
        public void OpenReadOnly_PathWithSemicolonAndQuote_OpensExactlyThatFile()
        {
            var dir = Directory.CreateTempSubdirectory("vssave_path_");
            try
            {
                var path = Path.Combine(dir.FullName, "weird;name\"quote.vcdbs");
                var cs = new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadWriteCreate
                }.ToString();
                using (var con = new SqliteConnection(cs))
                {
                    con.Open();
                    using var cmd = con.CreateCommand();
                    cmd.CommandText =
                        "CREATE TABLE chunk(position INTEGER PRIMARY KEY, data BLOB);" +
                        "INSERT INTO chunk(position, data) VALUES (1, x'0102');";
                    cmd.ExecuteNonQuery();
                }

                using var save = VSSaveFile.OpenReadOnly(path);
                Assert.Equal(path, save.Path);
                Assert.Contains("chunk", save.TableNames);
                Assert.Equal(1, save.CountRows("chunk"));
            }
            finally
            {
                Directory.Delete(dir.FullName, true);
            }
        }
    }
}
