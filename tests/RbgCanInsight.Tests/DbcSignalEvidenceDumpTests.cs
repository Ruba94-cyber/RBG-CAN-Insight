using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceVerify;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DbcSignalEvidenceDumpTests
{
    private static DbcDatabase Database() =>
        new DbcParser().Parse("reference.dbc", new StringReader(
            """
            VERSION ""
            BO_ 256 EngineData: 8 ECM
             SG_ EngineSpeed : 0|16@1+ (0.125,0) [0|8031.875] "rpm" ECM
             SG_ CoolantTemp : 16|8@1+ (1,-40) [-40|215] "degC" ECM
            """));

    [Fact]
    public void WritesOriginalFrameIdentityRawBitsAndPhysicalUnits()
    {
        var database = Database();
        var dump = new DbcSignalEvidenceDump(new DbcDecoder(database), new string('A', 64));
        using var output = new StringWriter();
        var frame = new CanFrame(2, 256, [0x40, 0x1F, 130, 0, 0, 0, 0, 0],
            123_456_789);

        Assert.Equal(2, dump.WriteFrame(output, 7, frame));
        var rows = output.ToString().Trim().Split('\n');
        Assert.Equal(2, rows.Length);
        var speed = Assert.Single(rows.Where(row => row.Contains(",EngineSpeed,")));
        var fields = speed.TrimEnd('\r').Split(',');
        Assert.Equal("7", fields[0]);           // immutable source frame index
        Assert.Equal("123456789", fields[1]);
        Assert.Equal("2", fields[2]);           // zero-based physical CAN bus
        Assert.Equal("256", fields[3]);
        Assert.Equal("0", fields[4]);           // 11-bit arbitration format
        Assert.Equal(new string('A', 64), fields[5]);
        Assert.Equal("reference.dbc", fields[6]);
        Assert.Equal("0", fields[9]);           // original bit position
        Assert.Equal("16", fields[10]);
        Assert.Equal("Intel", fields[11]);
        Assert.Equal("0x1F40", fields[13]);      // exact 16-bit coded payload
        Assert.Equal("8000", fields[14]);
        Assert.Equal("1", fields[15]);
        Assert.Equal("1000", fields[16]);
        Assert.Equal("rpm", fields[17]);
    }

    [Fact]
    public void RemoteAndErrorFramesNeverCreateFakeSignalEvidence()
    {
        var dump = new DbcSignalEvidenceDump(
            new DbcDecoder(Database()), new string('A', 64));
        using var output = new StringWriter();
        Assert.Equal(0, dump.WriteFrame(output, 0,
            new CanFrame(0, 256, [], 0, CanFrameFlags.Remote,
                declaredDataLength: 8)));
        Assert.Equal(0, dump.WriteFrame(output, 1,
            new CanFrame(0, 0, [], 0, CanFrameFlags.Error)));
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void FullWidthUnsignedRawRemainsExactEvenWhenPhysicalDoubleIsRounded()
    {
        var signal = new DbcSignal("Counter64", 0, 64, DbcByteOrder.Intel,
            false, 1, 0, 0, double.MaxValue, "", "ECM");
        var message = new DbcMessage(300, "Wide", 8, "ECM", false, [signal]);
        var database = new DbcDatabase("wide.dbc",
            new Dictionary<uint, DbcMessage> { [300] = message });
        var dump = new DbcSignalEvidenceDump(
            new DbcDecoder(database), new string('B', 64));
        using var output = new StringWriter();

        Assert.Equal(1, dump.WriteFrame(output, 0,
            new CanFrame(0, 300, new byte[] { 255, 255, 255, 255,
                255, 255, 255, 255 }, 10)));
        var fields = output.ToString().Trim().Split(',');
        Assert.Equal("0xFFFFFFFFFFFFFFFF", fields[13]);
        Assert.Equal("18446744073709551615", fields[14]);
        Assert.Equal("0", fields[15]); // physical double is NOT exact integer evidence
    }

    [Fact]
    public void ChannelAssignmentAndAbsentSignalsRemainSourceFaithful()
    {
        var decoder = new DbcDecoder(Database(), new DbcDecoderOptions(Channel: 1));
        var dump = new DbcSignalEvidenceDump(decoder, new string('C', 64));
        using var output = new StringWriter();
        Assert.Equal(0, dump.WriteFrame(output, 0,
            new CanFrame(0, 256, [1, 0, 0], 100)));
        Assert.Equal(2, dump.WriteFrame(output, 1,
            new CanFrame(1, 256, [1, 0, 0], 200)));
        Assert.DoesNotContain("\n0,100,", output.ToString());
        Assert.Contains("1,200,1,256,0", output.ToString());
    }

    [Fact]
    public void RejectsInvalidDatabaseIdentityAndNegativeFrameReference()
    {
        var decoder = new DbcDecoder(Database());
        Assert.Throws<ArgumentException>(() => new DbcSignalEvidenceDump(decoder, "NOT_SHA"));
        var dump = new DbcSignalEvidenceDump(decoder, new string('A', 64));
        using var output = new StringWriter();
        Assert.Throws<ArgumentOutOfRangeException>(() => dump.WriteFrame(output, -1,
            new CanFrame(0, 256, [1, 0, 0], 0)));
    }
}
