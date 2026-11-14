using RbgCanInsight.Analysis;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class UdsSourceSummaryTests
{
    private static CanFrame Frame(uint id, long time, params byte[] payload) =>
        new(0, id, payload, time);

    private static (CanFrame[] Frames, int[] Sources) TwoLoggers() =>
    (
        [
            Frame(0x7E0, 0, 0x03, 0x22, 0xF1, 0x90),
            Frame(0x7E0, 1_000, 0x03, 0x22, 0xF1, 0x90),
            Frame(0x7E8, 2_000, 0x04, 0x62, 0xF1, 0x90, 0xA0),
            Frame(0x7E8, 3_000, 0x03, 0x7F, 0x22, 0x31)
        ],
        [0, 1, 0, 1]
    );

    [Fact]
    public void EachPhysicalLoggerKeepsItsOwnSidAndOutcomeCounts()
    {
        var (frames, sources) = TwoLoggers();
        var transactions = UdsTransactionAnalyzer.Analyze(
            frames, null, null, CancellationToken.None, i => sources[i]);
        Assert.Equal(2, transactions.Count);
        var report = UdsSourceSummary.Calculate(
            transactions, frames.Length, new[] { "first.asc", "second.asc" },
            i => sources[i]);
        Assert.Equal(2, report.ObservedTransactions);
        Assert.Equal(2, report.SourceServiceGroups);
        Assert.False(report.Truncated);

        var first = Assert.Single(report.Rows.Where(row => row.SourceIndex == 0));
        Assert.Equal((byte)0x22, first.ServiceId);
        Assert.Equal("first.asc", first.SourceName);
        Assert.Equal(1, first.Positive);
        Assert.Equal(0, first.Negative);
        Assert.Equal(0, first.NoResponse);
        Assert.Equal(0, first.WitnessFrameIndex);

        var second = Assert.Single(report.Rows.Where(row => row.SourceIndex == 1));
        Assert.Equal((byte)0x22, second.ServiceId);
        Assert.Equal("second.asc", second.SourceName);
        Assert.Equal(1, second.Negative);
        Assert.Equal(0, second.Positive);
        Assert.Equal(1, second.WitnessFrameIndex);
    }

    [Fact]
    public void AForeignLoggerFrameIsNeverAcceptedAsTransactionEvidence()
    {
        var (frames, sources) = TwoLoggers();
        var transactions = UdsTransactionAnalyzer.Analyze(
            frames, null, null, CancellationToken.None, i => sources[i]);
        var first = Assert.Single(transactions.Where(transaction =>
            transaction.FrameIndices.Contains(0)));
        var corrupted = first with { FrameIndices = new[] { 0, 3 } };

        var error = Assert.Throws<InvalidOperationException>(() =>
            UdsSourceSummary.Calculate(new[] { corrupted }, frames.Length,
                new[] { "first.asc", "second.asc" }, i => sources[i]));
        Assert.Contains("different logger files", error.Message);
    }

    [Fact]
    public void InvalidFrameReferencesOrSourceNumbersAreRejected()
    {
        var (frames, sources) = TwoLoggers();
        var transactions = UdsTransactionAnalyzer.Analyze(
            frames, null, null, CancellationToken.None, i => sources[i]);
        var first = transactions[0];
        Assert.Throws<InvalidOperationException>(() =>
            UdsSourceSummary.Calculate(
                new[] { first with { FrameIndices = new[] { 99 } } },
                frames.Length, new[] { "first.asc", "second.asc" },
                i => sources[i]));
        Assert.Throws<InvalidOperationException>(() =>
            UdsSourceSummary.Calculate(new[] { first }, frames.Length,
                new[] { "first.asc", "second.asc" }, _ => 9));
    }

    [Fact]
    public void CancellationAndZeroTransactionsReturnOnlyGroundedResults()
    {
        var noData = UdsSourceSummary.Calculate(
            Array.Empty<DiagnosticTransaction>(), 0, new[] { "first.asc" });
        Assert.Equal(0, noData.ObservedTransactions);
        Assert.Empty(noData.Rows);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => UdsSourceSummary.Calculate(
            Array.Empty<DiagnosticTransaction>(), 0, new[] { "first.asc" },
            cancellationToken: cancel.Token));
    }

    [Fact]
    public void MultiLoggerSummaryRejectsMissingPhysicalSourceIndex()
    {
        var (frames, sources) = TwoLoggers();
        var transactions = UdsTransactionAnalyzer.Analyze(
            frames, null, null, CancellationToken.None, i => sources[i]);
        Assert.Throws<ArgumentException>(() =>
            UdsSourceSummary.Calculate(transactions, frames.Length,
                new[] { "first.asc", "second.asc" }));
    }

    [Fact]
    public void TruncationNeverHidesRealTotalGroupCount()
    {
        var (frames, sources) = TwoLoggers();
        var transactions = UdsTransactionAnalyzer.Analyze(
            frames, null, null, CancellationToken.None, i => sources[i]);
        var report = UdsSourceSummary.Calculate(transactions,
            frames.Length, new[] { "first.asc", "second.asc" },
            i => sources[i], maxRows: 1);
        Assert.Equal(2, report.SourceServiceGroups);
        Assert.Single(report.Rows);
        Assert.True(report.Truncated);
        Assert.Contains("top 1 only", report.Description);
    }
}
