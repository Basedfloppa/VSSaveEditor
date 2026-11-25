using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using VSCore;
using VSCore.Format;
using Xunit;

namespace VSCore.Tests
{
    /// <summary>
    /// The world tree in `gamedata` (field 31) is what the World tab shows: one flat list of
    /// `&lt;type byte&gt;&lt;name&gt;&lt;value&gt;` entries, vanilla settings and mod data side by side.
    /// These tests pin the framing (which byte means which type, where an entry ends), the promise that a
    /// half-understood tree says so instead of looking complete, and the cut that keeps a 53 KB mod blob
    /// out of the list row.
    /// </summary>
    public class WorldSettingsTests
    {
        [Fact(DisplayName = "world tree: string, int, float and bool values are read with their types")]
        public void Parses_All_Kinds()
        {
            var tree = Tree(
                (5, "gameMode", "creative"),
                (1, "spawnRadius", 512),
                (4, "upheavelCommonness", 0.3f),
                (9, "allowMap", true));

            var parsed = WorldSettings.Parse(Gamedata(tree));

            Assert.Null(parsed.Warning);
            Assert.Equal(tree.Length, parsed.ParsedBytes);
            Assert.Equal(tree.Length, parsed.TotalBytes);
            Assert.Equal(4, parsed.Settings.Count);
            Assert.Equal(("gameMode", WorldSettingKind.String, "creative"),
                (parsed.Settings[0].Name, parsed.Settings[0].Kind, parsed.Settings[0].Value));
            Assert.Equal(("spawnRadius", WorldSettingKind.Integer, "512"),
                (parsed.Settings[1].Name, parsed.Settings[1].Kind, parsed.Settings[1].Value));
            Assert.Equal(("upheavelCommonness", WorldSettingKind.Float, "0.3"),
                (parsed.Settings[2].Name, parsed.Settings[2].Kind, parsed.Settings[2].Value));
            Assert.Equal(("allowMap", WorldSettingKind.Bool, "true"),
                (parsed.Settings[3].Name, parsed.Settings[3].Kind, parsed.Settings[3].Value));
        }

        [Fact(DisplayName = "world tree: the tree is found in gamedata field 31, and nothing else is read as settings")]
        public void Finds_The_Tree_Field()
        {
            var tree = Tree((5, "gameMode", "survival"));
            using var ms = new MemoryStream();
            PbWriter.Int(ms, 12, 70_039_109);      // a calendar field that carries numbers, not a tree
            PbWriter.Int(ms, 7, 12345);
            PbWriter.Message(ms, 31, tree);

            var parsed = WorldSettings.Parse(ms.ToArray());

            Assert.Null(parsed.Warning);
            var one = Assert.Single(parsed.Settings);
            Assert.Equal("gameMode", one.Name);
            Assert.Equal("survival", one.Value);
        }

        [Fact(DisplayName = "world tree: a save without the tree says so instead of showing an empty list")]
        public void Missing_Tree_Is_Reported()
        {
            using var ms = new MemoryStream();
            PbWriter.Int(ms, 12, 1);

            var parsed = WorldSettings.Parse(ms.ToArray());

            Assert.Empty(parsed.Settings);
            Assert.Contains("field 31", parsed.Warning);
            Assert.Empty(WorldSettings.Parse(Array.Empty<byte>()).Settings);
            Assert.Empty(WorldSettings.Parse(null).Settings);
        }

        [Fact(DisplayName = "world tree: an unknown value type stops the walk, keeps what was read and names the type")]
        public void Unknown_Type_Stops_With_A_Warning()
        {
            var tree = Tree(
                (5, "gameMode", "creative"),
                (9, "allowMap", true),
                // a type this build does not read, then whatever follows it
                (55, "mod:thing", "…"),
                (5, "daysPerMonth", "9"));

            var parsed = WorldSettings.Parse(Gamedata(tree));

            Assert.Equal(2, parsed.Settings.Count);
            Assert.Equal(new[] { "gameMode", "allowMap" }, parsed.Settings.Select(s => s.Name));
            Assert.NotNull(parsed.Warning);
            Assert.Contains("value type 55", parsed.Warning);
            Assert.Contains("2 entries read", parsed.Warning);
            Assert.True(parsed.ParsedBytes < parsed.TotalBytes);
        }

        [Fact(DisplayName = "world tree: a value cut in the list keeps its real length in the label")]
        public void Long_Value_Is_Cut()
        {
            var json = new string('A', 300);
            var parsed = WorldSettings.Parse(Gamedata(Tree((5, "ToolsmithPartStats", json))));

            var one = Assert.Single(parsed.Settings);
            Assert.True(one.IsLong);
            Assert.Equal(WorldSetting.PreviewChars + "… (300 chars)".Length, one.Preview.Length);
            Assert.EndsWith("(300 chars)", one.Preview);
            Assert.StartsWith("ToolsmithPartStats = AAAA", one.Display);
            Assert.Contains("(300 chars)", one.Display);
            Assert.Equal(json, one.Value); // the card still gets the whole value
        }

        [Fact(DisplayName = "world tree: UTF-8 names and values survive (mods are not ASCII-only)")]
        public void Utf8_Roundtrips()
        {
            var parsed = WorldSettings.Parse(Gamedata(Tree((5, "мод:настройка", "значение"))));

            var one = Assert.Single(parsed.Settings);
            Assert.Equal("мод:настройка", one.Name);
            Assert.Equal("значение", one.Value);
        }

        [Fact(DisplayName = "world tree: a truncated value is a stop with a warning, not an exception")]
        public void Truncated_Value_Does_Not_Throw()
        {
            using var ms = new MemoryStream();
            ms.WriteByte(5);                // a string value…
            Text(ms, "gameMode");
            Text(ms, "creative");
            var tree = ms.ToArray();
            var cut = tree[..^2];           // …whose last two bytes are gone

            var parsed = WorldSettings.Parse(Gamedata(cut));

            Assert.Empty(parsed.Settings);
            Assert.NotNull(parsed.Warning);
            Assert.Contains("gameMode", parsed.Warning);
        }

        // ---- editing: one entry rewritten in place, everything else byte-for-byte ---------------

        [Fact(DisplayName = "world edit: an int changes, and every other byte of gamedata stays as it was")]
        public void Edits_An_Int_And_Keeps_The_Rest()
        {
            var tree = Tree((5, "gameMode", "creative"), (1, "spawnRadius", 512), (9, "allowMap", true));
            var gamedata = Gamedata(tree);
            var parsed = WorldSettings.Parse(gamedata);
            var radius = parsed.Settings[1];

            Assert.True(WorldSettingEdit.TrySetValue(gamedata, radius, "1024", out var edited, out var changed, out var error));
            Assert.Null(error);
            Assert.True(changed);

            var after = WorldSettings.Parse(edited);
            Assert.Equal("1024", after.Settings[1].Value);
            Assert.Equal(3, after.Settings.Count);
            Assert.Equal("creative", after.Settings[0].Value);
            Assert.Equal("true", after.Settings[2].Value);
            // the bytes outside the value are untouched: same length, and the slices around the entry are equal
            Assert.Equal(gamedata.Length, edited.Length);
            var treeAt = parsed.TreeOffset + radius.EntryOffset;
            Assert.Equal(gamedata[..(parsed.TreeOffset + radius.ValuePrefixStart)],
                edited[..(parsed.TreeOffset + radius.ValuePrefixStart)]);
            Assert.Equal(gamedata[(parsed.TreeOffset + radius.ValueEnd)..], edited[(parsed.TreeOffset + radius.ValueEnd)..]);
            Assert.NotEqual(gamedata[treeAt..], edited[treeAt..]);
        }

        [Fact(DisplayName = "world edit: a longer and a shorter string are re-framed, and the tree stays walkable")]
        public void Edits_A_String_Both_Ways()
        {
            var gamedata = Gamedata(Tree((5, "gameMode", "creative"), (1, "playerlives", -1), (5, "tail", "end")));
            var setting = WorldSettings.Parse(gamedata).Settings[0];

            // longer: 8 characters -> 18, so the tree grows by 10 bytes and the field length must follow
            Assert.True(WorldSettingEdit.TrySetValue(gamedata, setting, "survival-and-build", out var longer, out _, out var e1));
            Assert.Null(e1);
            var bigger = WorldSettings.Parse(longer);
            Assert.Equal(("gameMode", "survival-and-build"), (bigger.Settings[0].Name, bigger.Settings[0].Value));
            Assert.Equal(3, bigger.Settings.Count);
            Assert.Equal(WorldSettings.Parse(gamedata).TotalBytes + 10, bigger.TotalBytes);
            Assert.Equal("end", bigger.Settings[2].Value);
            Assert.Equal(-1, Assert.IsType<int>(ParseInt(bigger.Settings[1].Value)));

            // shorter: back down, and the entry after the edited one must still be readable
            Assert.True(WorldSettingEdit.TrySetValue(longer, bigger.Settings[0], "s", out var shorter, out _, out var e2));
            Assert.Null(e2);
            var smaller = WorldSettings.Parse(shorter);
            Assert.Equal("s", smaller.Settings[0].Value);
            Assert.Equal(("playerlives", "-1"), (smaller.Settings[1].Name, smaller.Settings[1].Value));
            Assert.Equal("end", smaller.Settings[2].Value);
        }

        [Fact(DisplayName = "world edit: float and bool values are written in their own framing")]
        public void Edits_Float_And_Bool()
        {
            var gamedata = Gamedata(Tree((4, "landformScale", 1.0f), (9, "caveIns", false)));
            var parsed = WorldSettings.Parse(gamedata);

            Assert.True(WorldSettingEdit.TrySetValue(gamedata, parsed.Settings[0], "3", out var afterFloat, out _, out _));
            Assert.True(WorldSettingEdit.TrySetValue(afterFloat, parsed.Settings[1], "true", out var afterBool, out _, out _));

            var done = WorldSettings.Parse(afterBool);
            Assert.Equal("3", done.Settings[0].Value);
            Assert.Equal(WorldSettingKind.Float, done.Settings[0].Kind);
            Assert.Equal("true", done.Settings[1].Value);
            Assert.Equal(WorldSettingKind.Bool, done.Settings[1].Kind);
            // a float stays four bytes and a bool stays one, so the tree did not grow
            Assert.Equal(WorldSettings.Parse(gamedata).TotalBytes, done.TotalBytes);
        }

        [Fact(DisplayName = "world edit: the tree is spliced back where it was, prefix fields of gamedata untouched")]
        public void Edits_In_Place_After_Other_Fields()
        {
            using var ms = new MemoryStream();
            PbWriter.Int(ms, 9, 4242);                       // some field the world message carries before the tree
            ms.Write(PbWriter.MessageBytes(31, Tree((5, "gameMode", "creative"))));
            PbWriter.Int(ms, 12, 7);                         // and one after it
            var gamedata = ms.ToArray();

            var parsed = WorldSettings.Parse(gamedata);
            Assert.Equal(6, parsed.TreeOffset);              // field 9 (one-byte tag, two-byte varint) + field 31 tag (two bytes) + length byte

            Assert.True(WorldSettingEdit.TrySetValue(gamedata, parsed.Settings[0], "survival", out var edited, out _, out var error));
            Assert.Null(error);

            // the neighbours are still there and still read the same way
            var fields = PbReader.ReadMessage(edited, maxDepth: 0);
            Assert.Equal(4242, fields.First(f => f.Field == 9).IntValue);
            Assert.Equal(7, fields.First(f => f.Field == 12).IntValue);
            Assert.Equal("survival", WorldSettings.Parse(edited).Settings[0].Value);
        }

        [Fact(DisplayName = "world edit: a second edit works even though the first one moved every offset")]
        public void Second_Edit_Uses_The_Fresh_Tree()
        {
            var gamedata = Gamedata(Tree((5, "a", "first"), (1, "b", 1), (5, "c", "third")));
            var first = WorldSettings.Parse(gamedata).Settings[1]; // offsets belong to the ORIGINAL blob

            Assert.True(WorldSettingEdit.TrySetValue(gamedata, first, "1024", out var step1, out _, out _));
            Assert.True(WorldSettingEdit.TrySetValue(step1, WorldSettings.Parse(step1).Settings[0], "much-longer-first-value", out var step1b, out _, out _));
            Assert.Equal("much-longer-first-value", WorldSettings.Parse(step1b).Settings[0].Value);
            // the row object still points into the original layout; the edit must relocate the entry by name
            Assert.True(WorldSettingEdit.TrySetValue(step1b, first, "2", out var step2, out _, out var error));
            Assert.Null(error);
            Assert.Equal("2", WorldSettings.Parse(step2).Settings[1].Value);
            Assert.Equal("much-longer-first-value", WorldSettings.Parse(step2).Settings[0].Value);
            Assert.Equal("third", WorldSettings.Parse(step2).Settings[2].Value);
        }

        [Fact(DisplayName = "world edit: the same value is a no-op, not an undo step")]
        public void Same_Value_Is_Not_A_Change()
        {
            var gamedata = Gamedata(Tree((5, "gameMode", "creative")));
            var setting = WorldSettings.Parse(gamedata).Settings[0];

            Assert.True(WorldSettingEdit.TrySetValue(gamedata, setting, "creative", out var result, out var changed, out var error));
            Assert.Null(error);
            Assert.False(changed);
            Assert.Same(gamedata, result);
        }

        [Theory(DisplayName = "world edit: a value of the wrong shape is refused with a reason")]
        [InlineData(WorldSettingKind.Integer, "1.5", "whole number")]
        [InlineData(WorldSettingKind.Integer, "abc", "not a number")]
        [InlineData(WorldSettingKind.Integer, "99999999999", "whole number")]
        [InlineData(WorldSettingKind.Float, "abc", "not a number")]
        [InlineData(WorldSettingKind.Float, "NaN", "finite")]
        [InlineData(WorldSettingKind.Float, "Infinity", "finite")]
        [InlineData(WorldSettingKind.Bool, "maybe", "boolean")]
        public void Rejects_Bad_Values(WorldSettingKind kind, string text, string expected)
        {
            Assert.False(WorldSettingEdit.TryNormalize(kind, text, out _, out var error));
            Assert.NotNull(error);
            Assert.Contains(expected, error);
        }

        [Fact(DisplayName = "world edit: true/1/yes normalize to true, and a string with a NUL byte is refused")]
        public void Normalizes_Booleans_And_Refuses_Nul()
        {
            Assert.True(WorldSettingEdit.TryNormalize(WorldSettingKind.Bool, " YES ", out var yes, out _));
            Assert.Equal("true", yes);
            Assert.True(WorldSettingEdit.TryNormalize(WorldSettingKind.Bool, "0", out var no, out _));
            Assert.Equal("false", no);

            Assert.False(WorldSettingEdit.TryNormalize(WorldSettingKind.String, "a\0b", out _, out var err));
            Assert.NotNull(err);
        }

        [Fact(DisplayName = "world edit: a tree that was not located cannot be edited, and says so")]
        public void No_Tree_Means_No_Edit()
        {
            var gamedata = PbWriter.MessageBytes(9, new byte[] { 1, 2, 3 });
            var parsed = WorldSettings.Parse(gamedata);
            Assert.False(parsed.CanEdit);

            var bogus = WorldSettings.Parse(Gamedata(Tree((5, "gameMode", "creative")))).Settings[0];
            Assert.False(WorldSettingEdit.TrySetValue(gamedata, bogus, "survival", out _, out _, out var error));
            Assert.NotNull(error);
            Assert.Contains("not located", error);
        }

        [Fact(DisplayName = "world edit: creation-only attributes are known by name (the game reads them once)")]
        public void Knows_Creation_Only_Names()
        {
            Assert.True(WorldSettingNames.IsCreationOnly("landformScale"));
            Assert.True(WorldSettingNames.IsCreationOnly("upheavelCommonness"));
            Assert.False(WorldSettingNames.IsCreationOnly("gameMode"));
        }

        private static object ParseInt(string text) => int.Parse(text, CultureInfo.InvariantCulture);

        // ---- build a tree the way the game stores it: <type><len><name><value>, then a zero byte ----
        private static byte[] Tree(params (byte Type, string Name, object Value)[] entries)
        {
            using var ms = new MemoryStream();
            foreach (var (type, name, value) in entries)
            {
                ms.WriteByte(type);
                Text(ms, name);
                switch (type)
                {
                    case 5: Text(ms, (string)value); break;
                    case 9: ms.WriteByte((byte)((bool)value ? 1 : 0)); break;
                    case 1: ms.Write(BitConverter.GetBytes((int)value)); break;
                    case 4: ms.Write(BitConverter.GetBytes((float)value)); break;
                    case 55: Text(ms, (string)value); break; // unknown to the parser, framed like a string
                }
            }

            ms.WriteByte(0); // the tree ends with a zero byte
            return ms.ToArray();
        }

        private static void Text(Stream s, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            PbWriter.Varint(s, bytes.Length);
            s.Write(bytes);
        }

        private static byte[] Gamedata(byte[] tree) => PbWriter.MessageBytes(31, tree);
    }
}
