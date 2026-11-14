using RbgCanInsight.Analysis.Xcp;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class XcpCcpAndDetectionTests
{
    private const uint Cro = 0x200;
    private const uint Dto = 0x201;

    private static CanFrame F(uint id, long ms, params byte[] d) => new(0, id, d, ms * 1_000_000);

    private static readonly XcpEndpoint Ccp = new(XcpProtocol.Ccp, Cro, Dto);

    [Fact]
    public void CcpSessionIsPairedByCounter()
    {
        var frames = new List<CanFrame>
        {
            F(Cro, 0, 0x01, 0x01, 0x34, 0x12, 0, 0, 0, 0), F(Dto, 1, 0xFF, 0x00, 0x01, 0, 0, 0, 0, 0),
            F(Cro, 5, 0x1B, 0x02, 0x02, 0x01, 0, 0, 0, 0), F(Dto, 6, 0xFF, 0x00, 0x02, 0x02, 0x01, 0, 0, 0),
            F(Cro, 10, 0x02, 0x03, 0x00, 0x00, 0xAA, 0xBB, 0xCC, 0xDD), F(Dto, 11, 0xFF, 0x00, 0x03, 0, 0, 0, 0, 0),
            F(Cro, 12, 0x04, 0x04, 0x04, 0, 0, 0, 0, 0), F(Dto, 13, 0xFF, 0x00, 0x04, 1, 2, 3, 4, 0),
            F(Cro, 20, 0x07, 0x05, 0x01, 0x00, 0x34, 0x12, 0, 0), F(Dto, 21, 0xFF, 0x00, 0x05, 0, 0, 0, 0, 0),
        };
        var a = XcpAnalyzer.Analyze(frames, Ccp);
        Assert.Equal(5, a.Transactions.Count);
        Assert.All(a.Transactions, t => Assert.Equal(XcpOutcome.Positive, t.Outcome));
        Assert.Equal(new[] { "CONNECT", "GET_CCP_VERSION", "SET_MTA", "UPLOAD", "DISCONNECT" }, a.Transactions.Select(t => t.CommandName));
        Assert.Contains("station address 0x1234", a.Transactions[0].RequestDecoded);
        Assert.Contains("slave CCP version 2.1", a.Transactions[1].ResponseDecoded);
        Assert.Contains("address raw AA BB CC DD", a.Transactions[2].RequestDecoded);
        Assert.Contains("data 01 02 03 04", a.Transactions[3].ResponseDecoded);
        Assert.Contains("end of session", a.Transactions[4].RequestDecoded);
        Assert.Equal(3, a.Transactions[2].Counter);
    }

    [Fact]
    public void CcpErrorCodeAndCounterMismatch()
    {
        var frames = new List<CanFrame>
        {
            F(Cro, 0, 0x04, 0x07, 0x04, 0, 0, 0, 0, 0), F(Dto, 1, 0xFF, 0x33, 0x07, 0, 0, 0, 0, 0),
            F(Cro, 2, 0x04, 0x08, 0x04, 0, 0, 0, 0, 0), F(Dto, 3, 0xFF, 0x00, 0x99, 0, 0, 0, 0, 0), // wrong CTR
        };
        var a = XcpAnalyzer.Analyze(frames, Ccp);
        Assert.Equal("Access denied", a.Transactions[0].ErrorName);
        Assert.Equal(XcpOutcome.Unanswered, a.Transactions[1].Outcome);
        Assert.Equal("Orphan CRM", Assert.Single(a.Events).Kind);
        Assert.Equal(1, a.ErrorCounts["Access denied (0x33)"]);
    }

    [Fact]
    public void CcpDaqAndEventMessages()
    {
        var frames = new List<CanFrame>
        {
            F(Dto, 0, 0x00, 1, 2, 3, 4, 5, 6, 7),
            F(Dto, 1, 0x05, 9, 9),
            F(Dto, 2, 0xFE, 0x01, 0x00, 0, 0, 0, 0, 0),
            F(Dto, 3, 0xFF), // short CRM, malformed
        };
        var a = XcpAnalyzer.Analyze(frames, Ccp);
        Assert.Equal(new[] { 0, 5 }, a.DaqPackets.Select(p => p.Pid));
        Assert.Equal("DAQ processor overload", a.Events[0].Name);
        Assert.Equal("Malformed DTO", a.Events[1].Kind);
    }

    [Fact]
    public void RetransmittedCcpCommandMarksFirstUnanswered()
    {
        var frames = new List<CanFrame>
        {
            F(Cro, 0, 0x01, 0x01, 0, 0, 0, 0, 0, 0), F(Cro, 100, 0x01, 0x01, 0, 0, 0, 0, 0, 0), F(Dto, 101, 0xFF, 0x00, 0x01, 0, 0, 0, 0, 0),
        };
        var a = XcpAnalyzer.Analyze(frames, Ccp);
        Assert.Equal(1, a.UnansweredCount);
        Assert.Equal(1, a.Transactions.Count(t => t.Outcome == XcpOutcome.Positive));
    }

    private static List<CanFrame> XcpSession(uint cmd, uint res, long t0) =>
    [
        F(cmd, t0, 0xFF, 0x00), F(res, t0 + 3, 0xFF, 0x15, 0x00, 0x08, 0x08, 0x00, 0x01, 0x01),
        F(cmd, t0 + 10, 0xFD), F(res, t0 + 12, 0xFF, 0, 0, 0, 0, 0, 0, 0),
        F(cmd, t0 + 20, 0xF4, 0x04, 0, 0, 1, 2, 3, 4), F(res, t0 + 22, 0xFF, 1, 2, 3, 4),
    ];

    [Fact]
    public void AutoDetectFindsXcpPairWithEvidence()
    {
        var frames = XcpSession(0x300, 0x301, 100);
        frames.Add(F(0x123, 105, 1, 2, 3, 4, 5, 6, 7, 8));
        frames.Sort((a, b) => a.TimestampNanoseconds.CompareTo(b.TimestampNanoseconds));
        var c = Assert.Single(XcpDetector.Detect(frames));
        Assert.Equal(XcpProtocol.Xcp, c.Protocol);
        Assert.Equal(0x300u, c.CommandId);
        Assert.Equal(0x301u, c.ResponseId);
        Assert.Equal(0.75, c.Confidence, 2); // 0.40 + 0.20 layout + 0.15 follow-up
        Assert.Equal(2, c.EvidenceFrameIndices.Count);
        Assert.NotEmpty(c.Reasons);
    }

    [Fact]
    public void AutoDetectFindsCcpPairAndSecondHandshakeRaisesConfidence()
    {
        var frames = new List<CanFrame>
        {
            F(Cro, 0, 0x01, 0x01, 0, 0, 0, 0, 0, 0), F(Dto, 2, 0xFF, 0x00, 0x01, 0, 0, 0, 0, 0),
            F(Cro, 10, 0x04, 0x02, 0x04, 0, 0, 0, 0, 0), F(Dto, 12, 0xFF, 0x00, 0x02, 1, 2, 3, 4, 0),
            F(Cro, 1000, 0x01, 0x09, 0, 0, 0, 0, 0, 0), F(Dto, 1002, 0xFF, 0x00, 0x09, 0, 0, 0, 0, 0),
        };
        var c = Assert.Single(XcpDetector.Detect(frames));
        Assert.Equal(XcpProtocol.Ccp, c.Protocol);
        Assert.Equal(2, c.Handshakes);
        Assert.Equal(0.90, c.Confidence, 2);
    }

    [Fact]
    public void AutoDetectRejectsGarbageAndSlowOrMismatchedResponses()
    {
        var frames = new List<CanFrame>
        {
            F(0x10, 0, 0xFF, 0x00), F(0x11, 500, 0xFF, 0x15, 0, 8, 8, 0, 1, 1),   // too late
            F(0x20, 1000, 0xFF, 0x00), F(0x21, 1002, 0x12, 0x15, 0, 8, 8, 0, 1, 1), // response not 0xFF
            F(0x30, 2000, 0x01, 0x05, 0, 0, 0, 0, 0, 0), F(0x31, 2002, 0xFF, 0x00, 0x77, 0, 0, 0, 0, 0), // CTR mismatch
            F(0x40, 3000, 0xFF, 0x00), F(0x40, 3002, 0xFF, 0x15, 0, 8, 8, 0, 1, 1), // same id
            F(0x50, 4000, 0xFF, 0x00), F(0x51, 4002, 0xFF),                           // short response
            F(0x60, 5000, 0xFF, 0x02), F(0x61, 5001, 0xFF, 0x15, 0, 8, 8, 0, 1, 1),  // mode not 0/1
        };
        Assert.Empty(XcpDetector.Detect(frames));
        Assert.Empty(XcpDetector.Detect([]));
    }

    [Fact]
    public void ImplausibleConnectLayoutScoresLow()
    {
        var frames = new List<CanFrame> { F(0x70, 0, 0xFF, 0x00), F(0x71, 1, 0xFF, 0xFF, 0xFF, 0x00, 0, 0, 0, 0) };
        var c = Assert.Single(XcpDetector.Detect(frames));
        Assert.Equal(0.40, c.Confidence, 2);
    }

    [Fact]
    public void ReportAutoDetectsAndDecodesWithDetectionRows()
    {
        var frames = XcpSession(0x300, 0x301, 0);
        var table = XcpReportRows.Build(frames);
        Assert.Contains(table.Rows, r => r[3] == "Detection" && r[7] == "Decoded below" && r[9].StartsWith("confidence"));
        Assert.Equal(3, table.Rows.Count(r => r[3] == "Command" && r[7] == "Positive"));
        Assert.Contains(table.Rows, r => r[3] == "Summary");
        Assert.All(table.Rows, r => Assert.Equal(XcpReportRows.Headers.Length, r.Length));
    }

    [Fact]
    public void ReportOnTraceWithoutXcpSaysSoAndManualOverrideWorks()
    {
        var plain = new List<CanFrame> { F(0x100, 0, 1, 2, 3), F(0x101, 1, 4, 5, 6) };
        var t = XcpReportRows.Build(plain);
        Assert.Single(t.Rows);
        Assert.Contains("No XCP/CCP handshake", t.Rows[0][4]);

        var frames = new List<CanFrame> { F(0x300, 0, 0xFD) };
        var manual = XcpReportRows.Build(frames, new XcpEndpoint(XcpProtocol.Xcp, 0x300, 0x301));
        Assert.Contains(manual.Rows, r => r[3] == "Command" && r[7] == "Unanswered");
        Assert.DoesNotContain(manual.Rows, r => r[3] == "Detection");
    }
}
