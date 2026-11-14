using RbgCanInsight.Diagnostics.Calibration;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class XcpCcpDecoderTests
{
    private const uint Master = 0x600, Slave = 0x601;
    private static CanFrame F(uint id, long ms, params byte[] b) => new(0, id, b, ms * 1_000_000L);
    private static readonly CalibrationEndpoint Xcp = new(CalibrationProtocol.Xcp, 0, Master, false, Slave, false);
    private static readonly CalibrationEndpoint Ccp = new(CalibrationProtocol.Ccp, 0, Master, false, Slave, false);

    private static CanFrame[] Connect(bool big) =>
    [
        F(Master, 0, 0xFF, 0x00),
        F(Slave, 2, 0xFF, 0x15, (byte)(big ? 1 : 0), 8, big ? (byte)0x00 : (byte)0x08, big ? (byte)0x08 : (byte)0x00, 1, 1)
    ];

    [Fact]
    public void XcpConnectResponseIsDecoded()
    {
        var a = XcpCcpDecoder.Decode(Connect(false), Xcp);
        var t = Assert.Single(a.Transactions);
        Assert.Equal("CONNECT", t.CommandName);
        Assert.Equal(CalibrationResult.Positive, t.Result);
        Assert.Equal(2.0, t.LatencyMilliseconds);
        Assert.Contains(t.ResponseFields, x => x.Name == "resource" && x.Value == "CAL_PAG|DAQ|PGM");
        Assert.Contains(t.ResponseFields, x => x.Name == "maxDto" && x.Value == "8");
        Assert.Contains(t.ResponseFields, x => x.Name == "maxCto" && x.Value == "8");
        Assert.Contains(t.ResponseFields, x => x.Name == "byteOrder" && x.Value == "little");
    }

    [Fact]
    public void XcpErrorOutOfRangeIsNamed()
    {
        var frames = Connect(false).Concat([F(Master, 10, 0xF4, 4, 0, 0, 0, 0, 0, 0), F(Slave, 11, 0xFE, 0x22)]).ToArray();
        var a = XcpCcpDecoder.Decode(frames, Xcp);
        var t = a.Transactions[1];
        Assert.Equal(CalibrationResult.Error, t.Result);
        Assert.Equal("ERR_OUT_OF_RANGE", t.ErrorName);
        Assert.Equal(1, a.Summary.ErrorCounts["ERR_OUT_OF_RANGE"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void XcpSetMtaAndShortUploadHonourByteOrder(bool big)
    {
        byte[] addr = big ? [0x12, 0x34, 0x56, 0x78] : [0x78, 0x56, 0x34, 0x12];
        var frames = Connect(big).Concat([
            F(Master, 10, [0xF6, 0, 0, 0x01, .. addr]), F(Slave, 11, 0xFF),
            F(Master, 12, [0xF4, 2, 0, 0x01, .. addr]), F(Slave, 13, 0xFF, 0xAA, 0xBB)]).ToArray();
        var a = XcpCcpDecoder.Decode(frames, Xcp);
        Assert.Equal(big, a.BigEndian);
        var mta = a.Transactions.Single(t => t.CommandName == "SET_MTA");
        Assert.Contains(mta.RequestFields, x => x.Name == "address" && x.Value == "0x12345678");
        Assert.Contains(mta.RequestFields, x => x.Name == "addressExtension" && x.Value == "0x01");
        var up = a.Transactions.Single(t => t.CommandName == "SHORT_UPLOAD");
        Assert.Contains(up.RequestFields, x => x.Name == "size" && x.Value == "2");
        Assert.Contains(up.RequestFields, x => x.Name == "address" && x.Value == "0x12345678");
    }

    [Fact]
    public void XcpDaqPacketsAreCountedPerPid()
    {
        var frames = new[]
        {
            F(Slave, 1, 0x00, 1, 2), F(Slave, 2, 0x01, 1), F(Slave, 3, 0x00, 3, 4), F(Slave, 4, 0xFD, 0x00)
        };
        var a = XcpCcpDecoder.Decode(frames, Xcp);
        Assert.Equal(2, a.Summary.DaqPacketsPerPid[0]);
        Assert.Equal(1, a.Summary.DaqPacketsPerPid[1]);
        Assert.Equal(3, a.DaqSamples.Count);
        Assert.Equal(1, a.Summary.EventCount);
    }

    [Fact]
    public void XcpUnansweredCommandTimesOut()
    {
        var frames = new[] { F(Master, 0, 0xFD), F(Master, 700, 0xFE) };
        var a = XcpCcpDecoder.Decode(frames, Xcp);
        Assert.All(a.Transactions, t => Assert.Equal(CalibrationResult.Timeout, t.Result));
        Assert.Equal(2, a.Summary.Unanswered);
        Assert.Null(a.Transactions[0].LatencyMilliseconds);
    }

    [Fact]
    public void CcpConnectAndCounterPairing()
    {
        var frames = new[]
        {
            F(Master, 0, 0x01, 5, 0x34, 0x12, 0, 0, 0, 0),
            F(Master, 1, 0x0D, 6, 0, 0, 0, 0, 0, 0),
            F(Slave, 3, 0xFF, 0x00, 6, 0, 0, 0, 0, 0),
            F(Slave, 4, 0xFF, 0x00, 5, 0, 0, 0, 0, 0)
        };
        var a = XcpCcpDecoder.Decode(frames, Ccp);
        var c = a.Transactions.Single(t => t.CommandName == "CONNECT");
        Assert.Equal(3, c.ResponseFrameIndex);
        Assert.Contains(c.RequestFields, x => x.Name == "stationAddress" && x.Value == "0x1234");
        var s = a.Transactions.Single(t => t.CommandName == "GET_S_STATUS");
        Assert.Equal(2, s.ResponseFrameIndex);
    }

    [Fact]
    public void CcpDnloadErrorCode()
    {
        var frames = new[] { F(Master, 0, 0x03, 9, 4, 1, 2, 3, 4, 0), F(Slave, 1, 0xFF, 0x33, 9, 0, 0, 0, 0, 0) };
        var t = Assert.Single(XcpCcpDecoder.Decode(frames, Ccp).Transactions);
        Assert.Equal("DNLOAD", t.CommandName);
        Assert.Equal(CalibrationResult.Error, t.Result);
        Assert.Equal((byte?)0x33, t.ErrorCode);
        Assert.Equal("access denied", t.ErrorName);
    }

    [Fact]
    public void SuggestsXcpAndCcpEndpoints()
    {
        var xcp = XcpCcpDecoder.SuggestEndpoints(Connect(false));
        var x = Assert.Single(xcp);
        Assert.Equal(CalibrationProtocol.Xcp, x.Endpoint.Protocol);
        Assert.Equal(Slave, x.Endpoint.SlaveId);
        Assert.Equal(1, x.Evidence);

        var ccp = XcpCcpDecoder.SuggestEndpoints([
            F(0x200, 0, 1, 7, 0, 0, 0, 0, 0, 0), F(0x201, 5, 0xFF, 0, 7, 0, 0, 0, 0, 0),
            F(0x200, 100, 1, 8, 0, 0, 0, 0, 0, 0), F(0x201, 105, 0xFF, 0, 8, 0, 0, 0, 0, 0)]);
        var c = Assert.Single(ccp);
        Assert.Equal(CalibrationProtocol.Ccp, c.Endpoint.Protocol);
        Assert.Equal(2, c.Evidence);
    }

    [Fact]
    public void UdsTrafficAndLateAnswersAreNotSuggested()
    {
        var uds = new[]
        {
            F(0x7E0, 0, 0x02, 0x10, 0x03), F(0x7E8, 1, 0x02, 0x50, 0x03),
            F(0x7E0, 2, 0x03, 0x22, 0xF1, 0x90), F(0x7E8, 3, 0x10, 0x14, 0x62, 0xF1, 0x90, 1, 2, 3),
            F(0x7E0, 4, 0x01, 0x00, 0, 0, 0, 0, 0, 0), F(0x7E8, 5, 0x7F, 0x01, 0x11)
        };
        Assert.Empty(XcpCcpDecoder.SuggestEndpoints(uds));
        Assert.Empty(XcpCcpDecoder.SuggestEndpoints([F(Master, 0, 0xFF, 0), F(Slave, 900, 0xFF, 0, 0, 8, 8, 0, 1, 1)]));
    }
}
