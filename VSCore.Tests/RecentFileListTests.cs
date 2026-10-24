using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>MRU list of recent saves: ordering, deduplication, capacity limit, removal.</summary>
    public class RecentFileListTests
    {
        [Fact]
        public void Push_puts_the_new_path_first()
        {
            var res = RecentFileList.Push(new[] { "/a/one.vcdbs", "/b/two.vcdbs" }, "/c/three.vcdbs");
            Assert.Equal(new[] { "/c/three.vcdbs", "/a/one.vcdbs", "/b/two.vcdbs" }, res);
        }

        [Fact]
        public void Push_moves_an_existing_entry_instead_of_duplicating_it()
        {
            var res = RecentFileList.Push(new[] { "/a/one.vcdbs", "/b/two.vcdbs" }, "/b/two.vcdbs");
            Assert.Equal(new[] { "/b/two.vcdbs", "/a/one.vcdbs" }, res);
        }

        [Fact]
        public void Push_compares_paths_case_insensitively()
        {
            var res = RecentFileList.Push(new[] { "/a/One.vcdbs" }, "/a/one.vcdbs");
            Assert.Single(res);
        }

        [Fact]
        public void Push_respects_the_capacity()
        {
            var current = Enumerable.Range(0, 5).Select(i => $"/s/{i}.vcdbs").ToArray();
            var res = RecentFileList.Push(current, "/s/new.vcdbs", capacity: 3);
            Assert.Equal(3, res.Count);
            Assert.Equal("/s/new.vcdbs", res[0]);
            Assert.DoesNotContain("/s/4.vcdbs", res);
        }

        [Fact]
        public void Push_with_an_empty_path_keeps_the_list()
        {
            var res = RecentFileList.Push(new[] { "/a/one.vcdbs" }, "   ");
            Assert.Equal(new[] { "/a/one.vcdbs" }, res);
        }

        [Fact]
        public void Remove_drops_only_the_matching_path()
        {
            var res = RecentFileList.Remove(new[] { "/a/one.vcdbs", "/b/two.vcdbs" }, "/A/One.vcdbs");
            Assert.Equal(new[] { "/b/two.vcdbs" }, res);
        }

        [Fact]
        public void SamePath_normalizes_dot_segments()
        {
            Assert.True(RecentFileList.SamePath("/a/b/../b/c.vcdbs", "/a/b/c.vcdbs"));
            Assert.False(RecentFileList.SamePath("/a/b.vcdbs", "/a/c.vcdbs"));
        }

        [Fact]
        public void Exists_reports_real_files_only()
        {
            var dir = Path.Combine(Path.GetTempPath(), "vse-recent-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var file = Path.Combine(dir, "save.vcdbs");
                File.WriteAllText(file, "x");
                Assert.True(RecentFileList.Exists(file));
                Assert.False(RecentFileList.Exists(Path.Combine(dir, "gone.vcdbs")));
                Assert.False(RecentFileList.Exists(null));
                Assert.False(RecentFileList.Exists(""));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Push_survives_a_null_list()
        {
            Assert.Empty(RecentFileList.Push(null, "/a/one.vcdbs"));
            Assert.Empty(RecentFileList.Remove(null, "/a/one.vcdbs"));
        }
    }
}
