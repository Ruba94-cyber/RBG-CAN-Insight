using RbgCanInsight.Analysis.Xcp;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class XcpAnalyzerTests
{
    private const uint Cmd = 0x7E0;
    private const uint Res = 0x7E1;
    private const uint Daq = 0x7E2;

    private static CanFrame F(uint id, long ms, params byte[] d) => new(0, id, d, ms * 1_000_000);

    private static readonly XcpEndpoint Ep = new(XcpProtocol.Xcp, Cmd, Res);

    // CONNECT response: resources CAL+DAQ, Intel, MAX_CTO 8, MAX_DTO 8 (LE), versions 1/1
    private static CanFrame ConnectRes(long ms) => F(Res, ms, 0xFF, 0x05, 0x00, 0x08, 0x08, 0x00, 0x01, 0x01);

    [Fact]
    public void ConnectIsPairedAndDecoded()
    {
        var frames = new List<CanFrame> { F(Cmd, 0, 0xFF, 0x00), ConnectRes(2) };
        var a = XcpAnalyzer.Analyze(frames, Ep);
        var t = Assert.Single(a.Transactions);
        Assert.Equal("CONNECT", t.CommandName);
        Assert.Equal(XcpOutcome.Positive, t.Outcome);
        Assert.Equal(2.0, t.LatencyMilliseconds!.Value, 3);
        Assert.Equal(new[] { 0, 1 }, t.FrameIndices);
        Assert.Contains("CAL/PAG+DAQ", t.ResponseDecoded);
        Assert.Contains("byte order Intel", t.ResponseDecoded);
        Assert.Contains("MAX_CTO 8", t.ResponseDecoded);
        Assert.Contains("MAX_DTO 8", t.ResponseDecoded);
    }

    [Fact]
    public void ShortUploadAddressUsesByteOrderFromConnect()
    {
        var frames = new List<CanFrame>
        {
            F(Cmd, 0, 0xFF, 0x00), ConnectRes(1),
            F(Cmd, 10, 0xF4, 0x04, 0x00, 0x00, 0x78, 0x56, 0x34, 0x12), F(Res, 11, 0xFF, 0xDE, 0xAD, 0xBE, 0xEF),
        };
        var a = XcpAnalyzer.Analyze(frames, Ep);
        var up = a.Transactions[1];
        Assert.Equal("SHORT_UPLOAD", up.CommandName);
        Assert.Contains("4 element(s)", up.RequestDecoded);
        Assert.Contains("address 0x12345678", up.RequestDecoded);
        Assert.Contains("data DE AD BE EF", up.ResponseDecoded);
    }

    [Fact]
    public void MotorolaConnectChangesAddressInterpretation()
    {
        var frames = new List<CanFrame>
        {
            F(Cmd, 0, 0xFF, 0x00), F(Res, 1, 0xFF, 0x01, 0x01, 0x08, 0x00, 0x08, 0x01, 0x01),
            F(Cmd, 5, 0xF6, 0, 0, 0, 0x12, 0x34, 0x56, 0x78), F(Res, 6, 0xFF),
        };
        var t = XcpAnalyzer.Analyze(frames, Ep).Transactions[1];
        Assert.Contains("address 0x12345678", t.RequestDecoded);
    }

    [Fact]
    public void AddressIsNotInterpretedWithoutConnectInTrace()
    {
        var frames = new List<CanFrame> { F(Cmd, 0, 0xF6, 0, 0, 0, 0x12, 0x34, 0x56, 0x78), F(Res, 1, 0xFF) };
        var t = Assert.Single(XcpAnalyzer.Analyze(frames, Ep).Transactions);
        Assert.Contains("address raw 12 34 56 78", t.RequestDecoded);
    }

    [Fact]
    public void ErrorResponseNamesTheCodeAndIsSummarised()
    {
        var frames = new List<CanFrame>
        {
            F(Cmd, 0, 0xF5, 0x02), F(Res, 1, 0xFE, 0x24),
            F(Cmd, 2, 0xF5, 0x02), F(Res, 3, 0xFE, 0x24),
            F(Cmd, 4, 0xEB, 0x83, 0x00, 0x01), F(Res, 5, 0xFE, 0x26),
        };
        var a = XcpAnalyzer.Analyze(frames, Ep);
        Assert.All(a.Transactions, t => Assert.Equal(XcpOutcome.Negative, t.Outcome));
        Assert.Equal("ERR_ACCESS_DENIED", a.Transactions[0].ErrorName);
        Assert.Equal(2, a.ErrorCounts["ERR_ACCESS_DENIED (0x24)"]);
        Assert.Equal(1, a.ErrorCounts["ERR_PAGE_NOT_VALID (0x26)"]);
    }

    [Fact]
    public void UnknownErrorCodeIsReportedWithoutGuessing()
    {
        var frames = new List<CanFrame> { F(Cmd, 0, 0xFD), F(Res, 1, 0xFE, 0x77) };
        var t = Assert.Single(XcpAnalyzer.Analyze(frames, Ep).Transactions);
        Assert.Null(t.ErrorName);
        Assert.Equal(0x77, t.ErrorCode);
        Assert.Contains(t.Notes, n => n.Contains("not defined"));
    }

    [Fact]
    public void UnansweredCommandsTimeOutOrAreSuperseded()
    {
        var frames = new List<CanFrame>
        {
            F(Cmd, 0, 0xFD),
            F(Cmd, 50, 0xFB), // supersedes the first
            F(Res, 52, 0xFF, 0, 0, 0, 0, 0, 0, 0x40),
            F(Cmd, 100, 0xFC), // never answered, trace continues past the timeout
            F(Res, 2000, 0xFF), // arrives after the timeout
        };
        var a = XcpAnalyzer.Analyze(frames, Ep);
        Assert.Contains(a.Transactions, t => t.CommandName == "SYNCH" && t.Notes.Any(n => n.Contains("no response within")));
        Assert.Single(a.Events);
        Assert.Equal(1, a.Transactions.Count(t => t.Outcome == XcpOutcome.Positive));
        Assert.Equal(2, a.UnansweredCount);
        Assert.Contains(a.Transactions, t => t.CommandName == "GET_STATUS" && t.Notes.Any(n => n.Contains("superseded")));
    }

    [Fact]
    public void PendingEventExtendsTheWait()
    {
        var frames = new List<CanFrame>
        {
            F(Cmd, 0, 0xF0, 0x01, 0xAA), F(Res, 900, 0xFD, 0x05), F(Res, 1800, 0xFF),
        };
        var t = Assert.Single(XcpAnalyzer.Analyze(frames, Ep).Transactions);
        Assert.Equal(XcpOutcome.Positive, t.Outcome);
        Assert.Contains(t.Notes, n => n.Contains("EV_CMD_PENDING"));
    }

    [Fact]
    public void DaqPacketsEventsAndServiceRequestsAreSeparated()
    {
        var ep = Ep with { DaqIds = new uint[] { Daq } };
        var frames = new List<CanFrame>
        {
            F(Cmd, 0, 0xDE, 0x01, 0x00, 0x00), F(Res, 1, 0xFF, 0x00),
            F(Daq, 10, 0x00, 1, 2, 3, 4, 5, 6, 7), F(Daq, 20, 0x00, 9, 9, 9, 9, 9, 9, 9), F(Daq, 20, 0x01, 1),
            F(Res, 30, 0xFD, 0x06),
            F(Res, 31, 0xFC, 0x00, (byte)'H', (byte)'i', 0),
        };
        var a = XcpAnalyzer.Analyze(frames, ep);
        Assert.Equal(3, a.DaqPackets.Count);
        Assert.Equal(2, a.DaqPackets.Count(p => p.Pid == 0));
        Assert.Contains("first PID 0", a.Transactions[0].ResponseDecoded);
        Assert.Equal("EV_DAQ_OVERLOAD", a.Events[0].Name);
        Assert.Equal("SERV_TEXT", a.Events[1].Name);
        Assert.Equal("Hi", a.Events[1].Decoded);
    }

    [Fact]
    public void CanFdPayloadIsDecoded()
    {
        var data = new byte[24];
        data[0] = 0xF0; data[1] = 20;
        for (var i = 2; i < 22; i++) data[i] = (byte)i;
        var frames = new List<CanFrame>
        {
            new(0, Cmd, data, 0, CanFrameFlags.FlexibleDataRate | CanFrameFlags.BitRateSwitch),
            new(0, Res, new byte[] { 0xFF }, 1_000_000, CanFrameFlags.FlexibleDataRate),
        };
        var t = Assert.Single(XcpAnalyzer.Analyze(frames, Ep).Transactions);
        Assert.Equal("DOWNLOAD", t.CommandName);
        Assert.Contains("20 element(s)", t.RequestDecoded);
        Assert.Equal(24, t.Request.Length);
    }

    [Fact]
    public void ExtendedIdsAndChannelFilterAreRespected()
    {
        var ep = new XcpEndpoint(XcpProtocol.Xcp, 0x18DA00F1, 0x18DAF100, true, true, 1);
        var frames = new List<CanFrame>
        {
            new(0, 0x18DA00F1, new byte[] { 0xFF, 0 }, 0, CanFrameFlags.Extended),  // wrong channel
            new(1, 0x18DA00F1, new byte[] { 0xFF, 0 }, 1_000_000, CanFrameFlags.Extended),
            new(1, 0x18DAF100, new byte[] { 0xFF, 0x05, 0, 8, 8, 0, 1, 1 }, 2_000_000, CanFrameFlags.Extended),
            new(1, 0x000, new byte[] { 0xFF }, 3_000_000), // standard id, ignored
        };
        var a = XcpAnalyzer.Analyze(frames, ep);
        var t = Assert.Single(a.Transactions);
        Assert.Equal(1, t.Channel);
        Assert.Equal(XcpOutcome.Positive, t.Outcome);
    }

    [Fact]
    public void UnknownCommandCodeIsKeptRaw()
    {
        var frames = new List<CanFrame> { F(Cmd, 0, 0xC1, 1, 2), F(Res, 1, 0xFF) };
        var t = Assert.Single(XcpAnalyzer.Analyze(frames, Ep).Transactions);
        Assert.Equal("UNKNOWN_0xC1", t.CommandName);
        Assert.Contains(t.Notes, n => n.Contains("not defined"));
    }

    [Fact]
    public void GarbageAndMalformedFramesNeverThrow()
    {
        var frames = new List<CanFrame>
        {
            F(Res, 0, 0xFF),                       // response with no command
            F(Res, 1, 0xFE),                       // truncated ERR
            F(Res, 2, 0xFD),                       // truncated EV
            F(Cmd, 3),                             // empty
            new(0, Cmd, ReadOnlySpan<byte>.Empty, 4_000_000, CanFrameFlags.Remote, 0, 4),
            new(0, 0x7FF, new byte[] { 1 }, 5_000_000, CanFrameFlags.Error),
            F(Cmd, 6, 0x00, 0x11),                 // master DTO (STIM)
        };
        var a = XcpAnalyzer.Analyze(frames, Ep);
        Assert.Empty(a.Transactions);
        Assert.Equal(3, a.Events.Count);
        Assert.Single(a.DaqPackets);
        Assert.True(a.DaqPackets[0].IsStimulation);
    }

    [Fact]
    public void IdenticalCommandAndResponseIdIsRejected() =>
        Assert.Throws<ArgumentException>(() => XcpAnalyzer.Analyze([], new XcpEndpoint(XcpProtocol.Xcp, 0x100, 0x100)));

    [Fact]
    public void EmptyTraceYieldsEmptyResult()
    {
        var a = XcpAnalyzer.Analyze([], Ep);
        Assert.Empty(a.Transactions);
        Assert.Equal(0, a.UnansweredCount);
    }

    [Fact]
    public void GetStatusAndDisconnectAreDecoded()
    {
        var frames = new List<CanFrame>
        {
            F(Cmd, 0, 0xFD), F(Res, 1, 0xFF, 0x40, 0x00, 0x00, 0x34, 0x12, 0, 0),
            F(Cmd, 2, 0xFE), F(Res, 3, 0xFF),
        };
        var a = XcpAnalyzer.Analyze(frames, Ep);
        Assert.Contains("DAQ_RUNNING", a.Transactions[0].ResponseDecoded);
        Assert.Equal("DISCONNECT", a.Transactions[1].CommandName);
    }
}
