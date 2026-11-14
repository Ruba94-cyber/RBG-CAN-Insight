using RbgCanInsight.Analysis;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DiagnosticTimingAnalyzerTests
{
    private static CanFrame F(uint id, long milliseconds, params byte[] payload)
    {
        var data = new byte[8];
        payload.CopyTo(data, 0);
        return new CanFrame(0, id, data, milliseconds * 1_000_000L);
    }

    private static DiagnosticTimingReport Run(CanFrame[] frames, DiagnosticTimingOptions? options = null)
    {
        var transactions = UdsTransactionAnalyzer.Analyze(frames, []);
        return DiagnosticTimingAnalyzer.Analyze(transactions, frames, options);
    }

    [Fact]
    public void SlowFirstResponseIsReportedWithBothSourceFrames()
    {
        var report = Run([F(0x7E0, 0, 3, 0x22, 0xF1, 0x90), F(0x7E8, 80, 5, 0x62, 0xF1, 0x90, 0xAA, 0xBB)]);
        var finding = Assert.Single(report.Findings, f => f.RuleId == "TIM-001");
        Assert.Equal(80d, finding.MeasuredMilliseconds!.Value, 3);
        Assert.Equal(50d, finding.LimitMilliseconds);
        Assert.Equal(new[] { 0, 1 }, finding.FrameIndices);
        Assert.Equal(DiagnosticTimingSeverity.Warning, finding.Severity);
    }

    [Fact]
    public void ResponseWithinP2ProducesNoFinding()
    {
        var report = Run([F(0x7E0, 0, 3, 0x22, 0xF1, 0x90), F(0x7E8, 30, 5, 0x62, 0xF1, 0x90, 0xAA, 0xBB)]);
        Assert.DoesNotContain(report.Findings, f => f.RuleId == "TIM-001");
    }

    [Fact]
    public void VeryLateResponseEscalatesToError()
    {
        var report = Run([F(0x7E0, 0, 3, 0x22, 0xF1, 0x90), F(0x7E8, 140, 5, 0x62, 0xF1, 0x90, 0xAA, 0xBB)]);
        Assert.Equal(DiagnosticTimingSeverity.Error, Assert.Single(report.Findings, f => f.RuleId == "TIM-001").Severity);
    }

    [Fact]
    public void P2AnnouncedBySessionControlReplacesTheDefault()
    {
        var frames = new[]
        {
            F(0x7E0, 0, 2, 0x10, 0x03),
            F(0x7E8, 10, 6, 0x50, 0x03, 0x00, 0xC8, 0x01, 0xF4), // P2 200 ms, P2* 5000 ms
            F(0x7E0, 1000, 3, 0x22, 0xF1, 0x90),
            F(0x7E8, 1120, 5, 0x62, 0xF1, 0x90, 0xAA, 0xBB) // 120 ms: late by default, fine with 200 ms
        };
        Assert.DoesNotContain(Run(frames).Findings, f => f.RuleId == "TIM-001");
        Assert.Contains(Run(frames, new DiagnosticTimingOptions { UseAnnouncedSessionTiming = false }).Findings,
            f => f.RuleId == "TIM-001");
    }

    [Fact]
    public void GapAfterResponsePendingBeyondP2StarIsReported()
    {
        var report = Run(
        [
            F(0x7E0, 0, 3, 0x22, 0xF1, 0x90),
            F(0x7E8, 20, 3, 0x7F, 0x22, 0x78),
            F(0x7E8, 6020, 5, 0x62, 0xF1, 0x90, 0xAA, 0xBB)
        ]);
        var finding = Assert.Single(report.Findings, f => f.RuleId == "TIM-002");
        Assert.Equal(6000d, finding.MeasuredMilliseconds!.Value, 3);
        Assert.Contains(1, finding.FrameIndices);
        Assert.Contains(2, finding.FrameIndices);
    }

    [Fact]
    public void LongPendingChainIsReportedAndShortOneIsNot()
    {
        var frames = new List<CanFrame> { F(0x7E0, 0, 3, 0x22, 0xF1, 0x90) };
        for (var i = 1; i <= 6; i++) frames.Add(F(0x7E8, i * 100, 3, 0x7F, 0x22, 0x78));
        frames.Add(F(0x7E8, 800, 5, 0x62, 0xF1, 0x90, 0xAA, 0xBB));
        var finding = Assert.Single(Run(frames.ToArray()).Findings, f => f.RuleId == "TIM-003");
        Assert.Equal(DiagnosticTimingSeverity.Information, finding.Severity);
        Assert.Contains("6 NRC 0x78", finding.Message);
        Assert.Equal(8, finding.FrameIndices.Count);

        var few = Run([F(0x7E0, 0, 3, 0x22, 0xF1, 0x90), F(0x7E8, 20, 3, 0x7F, 0x22, 0x78), F(0x7E8, 90, 5, 0x62, 0xF1, 0x90, 0xAA, 0xBB)]);
        Assert.DoesNotContain(few.Findings, f => f.RuleId == "TIM-003");
    }

    [Fact]
    public void PendingChainWithoutFinalResponseIsFlagged()
    {
        var report = Run([F(0x7E0, 0, 3, 0x22, 0xF1, 0x90), F(0x7E8, 20, 3, 0x7F, 0x22, 0x78), F(0x7E8, 40, 3, 0x7F, 0x22, 0x78)]);
        var finding = Assert.Single(report.Findings, f => f.RuleId == "TIM-004");
        Assert.Equal(new[] { 0, 1, 2 }, finding.FrameIndices);
    }

    [Fact]
    public void S3GapIsReportedOnlyAfterAnObservedNonDefaultSession()
    {
        var defaultOnly = Run(
        [
            F(0x7E0, 0, 3, 0x22, 0xF1, 0x90), F(0x7E8, 10, 5, 0x62, 0xF1, 0x90, 1, 2),
            F(0x7E0, 9000, 3, 0x22, 0xF1, 0x90), F(0x7E8, 9010, 5, 0x62, 0xF1, 0x90, 1, 2)
        ]);
        Assert.DoesNotContain(defaultOnly.Findings, f => f.RuleId is "TIM-005" or "TIM-006");
        Assert.Contains(defaultOnly.Notes, n => n.Contains("not evaluated"));

        var extended = Run(
        [
            F(0x7E0, 0, 2, 0x10, 0x03), F(0x7E8, 10, 6, 0x50, 0x03, 0x00, 0x32, 0x01, 0xF4),
            F(0x7E0, 9000, 3, 0x22, 0xF1, 0x90), F(0x7E8, 9010, 5, 0x62, 0xF1, 0x90, 1, 2)
        ]);
        var finding = Assert.Single(extended.Findings, f => f.RuleId == "TIM-005");
        Assert.True(finding.MeasuredMilliseconds > 8900);
        Assert.Equal(new[] { 0, 1, 2 }, finding.FrameIndices);
    }

    [Fact]
    public void InterruptedTesterPresentStreamIsDistinguishedFromAPlainS3Gap()
    {
        var frames = new List<CanFrame> { F(0x7E0, 0, 2, 0x10, 0x03), F(0x7E8, 10, 6, 0x50, 0x03, 0x00, 0x32, 0x01, 0xF4) };
        for (var i = 1; i <= 3; i++)
        {
            frames.Add(F(0x7E0, i * 2000L, 2, 0x3E, 0x00));
            frames.Add(F(0x7E8, i * 2000L + 5, 2, 0x7E, 0x00));
        }
        frames.Add(F(0x7E0, 6000 + 7000, 2, 0x3E, 0x00));
        frames.Add(F(0x7E8, 6000 + 7005, 2, 0x7E, 0x00));
        var report = Run(frames.ToArray());
        var finding = Assert.Single(report.Findings, f => f.RuleId == "TIM-006");
        Assert.Contains("3 requests", finding.Message);
        Assert.DoesNotContain(report.Findings, f => f.RuleId == "TIM-005");
    }

    [Fact]
    public void SlowFlowControlIsReportedAsNBs()
    {
        var frames = new[]
        {
            F(0x7E0, 0, 3, 0x22, 0xF1, 0x90),
            F(0x7E8, 10, 0x10, 0x0B, 0x62, 0xF1, 0x90, 0x10, 0x11, 0x12),
            F(0x7E0, 1500, 0x30, 0x00, 0x00),
            F(0x7E8, 1501, 0x21, 0x13, 0x14, 0x15, 0x16, 0x17)
        };
        var finding = Assert.Single(Run(frames).Findings, f => f.RuleId == "TIM-007");
        Assert.Equal(1490d, finding.MeasuredMilliseconds!.Value, 3);
        Assert.Equal(new[] { 1, 2 }, finding.FrameIndices);
    }

    [Fact]
    public void SlowConsecutiveFrameAfterFlowControlIsReportedAsNCr()
    {
        var frames = new[]
        {
            F(0x7E0, 0, 3, 0x22, 0xF1, 0x90),
            F(0x7E8, 10, 0x10, 0x0B, 0x62, 0xF1, 0x90, 0x10, 0x11, 0x12),
            F(0x7E0, 12, 0x30, 0x00, 0x00),
            F(0x7E8, 1600, 0x21, 0x13, 0x14, 0x15, 0x16, 0x17)
        };
        var finding = Assert.Single(Run(frames).Findings, f => f.RuleId == "TIM-008");
        Assert.Equal(1588d, finding.MeasuredMilliseconds!.Value, 3);
        Assert.Equal(new[] { 2, 3 }, finding.FrameIndices);
    }

    [Fact]
    public void ConsecutiveFramesCloserThanRequestedStMinAreReported()
    {
        var frames = new[]
        {
            F(0x7E0, 0, 3, 0x22, 0xF1, 0x90),
            F(0x7E8, 10, 0x10, 0x14, 0x62, 0xF1, 0x90, 0x10, 0x11, 0x12),
            F(0x7E0, 12, 0x30, 0x00, 0x0A), // STmin 10 ms
            F(0x7E8, 13, 0x21, 1, 2, 3, 4, 5, 6, 7),
            F(0x7E8, 15, 0x22, 8, 9, 10, 11, 12, 13, 14) // 2 ms after the previous CF
        };
        var finding = Assert.Single(Run(frames).Findings, f => f.RuleId == "TIM-009");
        Assert.Contains("1 consecutive-frame spacing", finding.Message);
        Assert.Equal(new[] { 3, 4 }, finding.FrameIndices);
    }

    [Fact]
    public void RespectedStMinAndMicrosecondEncodingProduceNoFinding()
    {
        var frames = new[]
        {
            F(0x7E0, 0, 3, 0x22, 0xF1, 0x90),
            F(0x7E8, 10, 0x10, 0x14, 0x62, 0xF1, 0x90, 0x10, 0x11, 0x12),
            F(0x7E0, 12, 0x30, 0x00, 0x0A),
            F(0x7E8, 13, 0x21, 1, 2, 3, 4, 5, 6, 7),
            F(0x7E8, 24, 0x22, 8, 9, 10, 11, 12, 13, 14)
        };
        Assert.DoesNotContain(Run(frames).Findings, f => f.RuleId.StartsWith("TIM-00", StringComparison.Ordinal) && f.RuleId is "TIM-007" or "TIM-008" or "TIM-009");
        Assert.Equal(0.3, DiagnosticTimingAnalyzer.DecodeStMinMilliseconds(0xF3), 6);
        Assert.Equal(127, DiagnosticTimingAnalyzer.DecodeStMinMilliseconds(0x80));
        Assert.Equal(20, DiagnosticTimingAnalyzer.DecodeStMinMilliseconds(0x14));
    }

    [Fact]
    public void FlowControlFromAnotherEcuOnTheSameChannelIsIgnored()
    {
        var frames = new[]
        {
            F(0x7E0, 0, 3, 0x22, 0xF1, 0x90),
            F(0x7E8, 10, 0x10, 0x0B, 0x62, 0xF1, 0x90, 0x10, 0x11, 0x12),
            F(0x7E1, 1500, 0x30, 0x00, 0x00), // FC of a different tester/ECU pair
            F(0x7E0, 20, 0x30, 0x00, 0x00),
            F(0x7E8, 21, 0x21, 0x13, 0x14, 0x15, 0x16, 0x17)
        };
        Assert.DoesNotContain(Run(frames).Findings, f => f.RuleId == "TIM-007");
    }

    [Fact]
    public void OverflowFlowControlIsReported()
    {
        var frames = new[]
        {
            F(0x7E0, 0, 3, 0x22, 0xF1, 0x90),
            F(0x7E8, 10, 0x10, 0x0B, 0x62, 0xF1, 0x90, 0x10, 0x11, 0x12),
            F(0x7E0, 12, 0x32, 0x00, 0x00)
        };
        Assert.Contains(Run(frames).Findings, f => f.RuleId == "TIM-010" && f.FrameIndices.SequenceEqual(new[] { 1, 2 }));
    }

    [Fact]
    public void WithoutFramesOnlyTransactionRulesRunAndTheReportSaysSo()
    {
        var frames = new[] { F(0x7E0, 0, 3, 0x22, 0xF1, 0x90), F(0x7E8, 80, 5, 0x62, 0xF1, 0x90, 0xAA, 0xBB) };
        var report = DiagnosticTimingAnalyzer.Analyze(UdsTransactionAnalyzer.Analyze(frames, []), null);
        Assert.Contains(report.Findings, f => f.RuleId == "TIM-001");
        Assert.DoesNotContain(report.RulesEvaluated, r => r.Id == "TIM-007");
        Assert.Contains(report.Notes, n => n.Contains("N_Bs"));
        Assert.Contains(report.Notes, n => n.Contains("N_As"));
    }

    [Fact]
    public void FindingsFlowIntoTheOfflineFindingsWithSourceFrames()
    {
        var frames = new[] { F(0x7E0, 0, 3, 0x22, 0xF1, 0x90), F(0x7E8, 80, 5, 0x62, 0xF1, 0x90, 0xAA, 0xBB) };
        var insight = DiagnosticInsightAnalyzer.Analyze(UdsTransactionAnalyzer.Analyze(frames, []), [], frames);
        var offline = OfflineAnalysisAnalyzer.Analyze([], [], insight, []);
        var finding = Assert.Single(offline.Findings, f => f.Title.StartsWith("TIM-001", StringComparison.Ordinal));
        Assert.Equal(OfflineFindingCategory.Timing, finding.Category);
        Assert.Equal(new[] { 0, 1 }, finding.FrameIndices);
    }

    [Fact]
    public void CsvExportListsRuleSeverityAndFrames()
    {
        var report = Run([F(0x7E0, 0, 3, 0x22, 0xF1, 0x90), F(0x7E8, 80, 5, 0x62, 0xF1, 0x90, 0xAA, 0xBB)]);
        var csv = report.ToCsv();
        Assert.StartsWith("Rule,Title,Severity,ECU,Time (s)", csv);
        Assert.Contains("TIM-001,P2 response time exceeded,Warning", csv);
        Assert.Contains("0 1", csv);
        Assert.Contains("1 warning(s)", report.ToSummaryText());
    }
}
