using System.Text;
using VSCore.Format;
using Xunit;

namespace VSCore.Tests;

public class PbReaderTests
{
    [Fact]
    public void Varint_SingleByte()
    {
        var (v, next) = PbReader.ReadVarint(new byte[] { 0x7F, 0x00 }, 0);
        Assert.Equal(127, v);
        Assert.Equal(1, next);
    }

    [Fact]
    public void Varint_MultiByte()
    {
        // 0x80 | 0x01 -> varint 128
        var (v, _) = PbReader.ReadVarint(new byte[] { 0x80, 0x01 }, 0);
        Assert.Equal(128, v);
    }

    [Fact]
    public void Tag_Field1Len_ThenVersionString()
    {
        // a hand-built message: field1 len 4 "Test"
        var msg = new byte[] { 0x0A, 0x04, 0x54, 0x65, 0x73, 0x74 };
        var fields = PbReader.ReadMessage(msg);
        Assert.Single(fields);
        Assert.Equal(1, fields[0].Field);
        Assert.Equal(2, fields[0].WireType);
        Assert.Equal(4, fields[0].Length);
        Assert.Equal("Test", Encoding.UTF8.GetString(fields[0].Bytes!));
    }

    [Fact]
    public void Parse_RegionField9_WithWeatherString()
    {
        // build: field9(LEN) payload = message { field1(LEN) "weatherState" }
        var innerBytes = Encoding.UTF8.GetBytes("weatherState");
        var payload = new List<byte> { 0x0A, (byte)innerBytes.Length }; // field1 LEN tag + len
        payload.AddRange(innerBytes);
        var outer = new List<byte> { 0x4A, (byte)payload.Count }; // field9 LEN tag (0x4a) + len
        outer.AddRange(payload);
        var fields = PbReader.ReadMessage(outer.ToArray(), maxDepth: 2);
        var f9 = fields.Find(f => f.Field == 9 && f.WireType == 2);
        Assert.NotNull(f9);
        Assert.Equal(payload.Count, f9!.Length);
        Assert.NotNull(f9.Children);
        var inner = f9.Children!.Find(c => c.Field == 1 && c.WireType == 2);
        Assert.NotNull(inner);
        Assert.Equal("weatherState", Encoding.UTF8.GetString(inner!.Bytes!));
    }

    // --- P0-1: lengths/bounds from an untrusted blob must not crash the parser ---

    [Fact]
    public void LenDelim_LongMaxLength_StopsInsteadOfThrowing()
    {
        // 9 bytes of 0xFF give 63 significant bits, the 10th byte terminates => ln = long.MaxValue.
        // This used to overflow i + ln, the guard was skipped, (int)ln became -1 and new byte[-1] threw.
        var msg = new List<byte> { 0x0A };
        msg.AddRange(Enumerable.Repeat((byte)0xFF, 9));
        msg.Add(0x00);

        Assert.Equal(long.MaxValue, PbReader.ReadVarint(msg.ToArray(), 1).value);
        Assert.Null(Record.Exception(() => PbReader.ReadMessage(msg.ToArray())));
        Assert.Empty(PbReader.ReadMessage(msg.ToArray()));
    }

    [Fact]
    public void LenDelim_OverlongVarint_StopsInsteadOfThrowing()
    {
        // 20 bytes of 0xFF without a terminator: a varint longer than 10 bytes is garbage, so the length ends up negative.
        var msg = new List<byte> { 0x0A };
        msg.AddRange(Enumerable.Repeat((byte)0xFF, 20));

        Assert.Null(Record.Exception(() => PbReader.ReadMessage(msg.ToArray())));
        Assert.Empty(PbReader.ReadMessage(msg.ToArray()));
    }

    [Fact]
    public void LenDelim_LengthBeyondBuffer_Stops()
    {
        // field1 LEN: 100 bytes declared, 2 in the buffer.
        Assert.Empty(PbReader.ReadMessage(new byte[] { 0x0A, 0x64, 0x01, 0x02 }));
    }

    [Fact]
    public void TruncatedFixed32AndFixed64_StopWithoutThrowing()
    {
        Assert.Empty(PbReader.ReadMessage(new byte[] { 0x0D, 0x01, 0x02 }));     // field1 fixed32, only 2 bytes available
        Assert.Empty(PbReader.ReadMessage(new byte[] { 0x09, 1, 2, 3, 4, 5 })); // field1 fixed64, only 5 bytes available
    }
}
