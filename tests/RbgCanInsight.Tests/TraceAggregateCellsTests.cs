using System.Text.Json;
using RbgCanInsight.Analysis;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class TraceAggregateCellsTests
{
    private static CanFrame Make(int channel, uint id, long ns,
        CanFrameFlags flags = CanFrameFlags.None) =>
        new(channel, id, new byte[] { 0x10, 0x20 }, ns, flags);

    [Fact]
    public void SameCanIdFromDifferentOriginalLoggersIsNeverSilentlyCombined()
    {
        var frames = new[]
        {
            Make(0, 0x100, 1_000_000_000),
            Make(0, 0x100, 2_000_000_000),
            Make(0, 0x100, 1_500_000_000),
            Make(0, 0x100, 3_000_000_000)
        };
        var sources = new[] { 0, 1, 0, 1 };
        var report = TraceAggregateCells.Calculate(frames, frames.Length, i => i,
            TraceAggregateKind.ByIdentifier, i => sources[i], new[] { "first.asc", "second.asc" });
        Assert.Equal(4, report.ExaminedFrames);
        Assert.Equal(2, report.UniqueGroups);
        Assert.All(report.Rows, row => Assert.Equal(2, row.Frames));
        Assert.Equal(new[] { "first.asc", "second.asc" }, report.Rows.Select(row => row.SourceName));
        Assert.Equal(new[] { 0, 1 }, report.Rows.Select(row => row.SourceIndex));
        Assert.NotEqual(report.Rows[0].FirstFrameIndex, report.Rows[1].FirstFrameIndex);
    }

    [Fact]
    public void VisibleSelectionDoesNotCountHiddenFrames()
    {
        var frames = new[]
        {
            Make(0, 0x100, 100),
            Make(0, 0x200, 200),
            Make(0, 0x100, 300),
            Make(1, 0x100, 400)
        };
        var visible = new[] { 0, 2 };
        var report = TraceAggregateCells.Calculate(frames, visible.Length,
            position => visible[position], TraceAggregateKind.ByIdentifier);
        var row = Assert.Single(report.Rows);
        Assert.Equal(2, row.Frames);
        Assert.Equal((uint)0x100, row.Identifier);
        Assert.Equal(0, row.Channel);
        Assert.Equal(0, row.FirstFrameIndex);
        Assert.Equal(2, row.LastFrameIndex);
        Assert.False(report.DisplayTruncated);
    }

    [Fact]
    public void SourceSummaryIgnoresNumericIdAndChannelButTracksRealTimeExtremes()
    {
        var frames = new[]
        {
            Make(0, 0x101, 900, CanFrameFlags.Error),
            Make(1, 0x102, 500, CanFrameFlags.FlexibleDataRate),
            Make(2, 0x103, 700),
            Make(0, 0x104, 300)
        };
        var sources = new[] { 0, 0, 1, 0 };
        var report = TraceAggregateCells.Calculate(frames, 4, i => i,
            TraceAggregateKind.BySource, i => sources[i], new[] { "a.asc", "b.asc" });
        Assert.Equal(2, report.UniqueGroups);
        var a = Assert.Single(report.Rows.Where(row => row.SourceIndex == 0));
        Assert.Equal(3, a.Frames);
        Assert.Equal(1, a.ErrorFrames);
        Assert.Equal(1, a.CanFdFrames);
        Assert.Equal(300, a.FirstTimestampNanoseconds);
        Assert.Equal(900, a.LastTimestampNanoseconds);
        Assert.Equal(3, a.FirstFrameIndex);
        Assert.Equal(0, a.LastFrameIndex);
    }

    [Fact]
    public void EqualIdButDifferentExtendedFormatOrChannelStaySeparate()
    {
        var frames = new[]
        {
            Make(0, 0x123, 10),
            Make(0, 0x123, 20, CanFrameFlags.Extended),
            Make(1, 0x123, 30)
        };
        var report = TraceAggregateCells.Calculate(frames, 3, i => i,
            TraceAggregateKind.ByIdentifier);
        Assert.Equal(3, report.UniqueGroups);
        Assert.Contains(report.Rows, r => r.IsExtended && r.Channel == 0);
        Assert.Contains(report.Rows, r => !r.IsExtended && r.Channel == 1);
        Assert.Contains(report.Rows, r => r.IdentifierText == "123");
    }

    [Fact]
    public void ResultsAreDeterministicTopNAndExplainTruncation()
    {
        var frames = Enumerable.Range(0, 6)
            .Select(i => Make(0, (uint)(i + 1), i * 1000)).ToArray();
        var report = TraceAggregateCells.Calculate(frames, frames.Length,
            i => i, TraceAggregateKind.ByIdentifier, maxRows: 2);
        Assert.Equal(6, report.UniqueGroups);
        Assert.Equal(2, report.Rows.Count);
        Assert.True(report.DisplayTruncated);
        Assert.Equal((uint)1, report.Rows[0].Identifier);
        Assert.Contains("not all groups", report.Description);
    }

    [Fact]
    public void MultipleRecordedSourcesCannotBeSilentlySummedWithoutFrameSourceIndex()
    {
        var frames = new[] { Make(0, 0x123, 1) };
        Assert.Throws<ArgumentException>(() =>
            TraceAggregateCells.Calculate(frames, 1, i => i,
                TraceAggregateKind.ByIdentifier,
                sourcePaths: new[] { "first.asc", "second.asc" }));
    }

    [Fact]
    public void MissingSourceOrOutOfRangeFrameCannotProduceFalseResults()
    {
        var frames = new[] { Make(0, 0x123, 1) };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TraceAggregateCells.Calculate(frames, 1, _ => 7, TraceAggregateKind.ByIdentifier));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TraceAggregateCells.Calculate(frames, 1, i => i, TraceAggregateKind.ByIdentifier,
                _ => 1, new[] { "only.asc" }));
    }

    [Fact]
    public void CancellationStopsAggregationBeforeReturningStaleData()
    {
        var frames = new[] { Make(0, 0x100, 100) };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            TraceAggregateCells.Calculate(frames, 1, i => i, TraceAggregateKind.ByIdentifier,
                cancellationToken: cancellation.Token));
    }


    [Fact]
    public void SecondBucketsPreserveOriginalLoggerAndBusEvenWhenClocksOverlap()
    {
        var frames = new[]
        {
            Make(0, 0x100, 0),
            Make(0, 0x101, 999_999_999),
            Make(0, 0x100, 1_000_000_000),
            Make(0, 0x100, 999_999_999),
            Make(1, 0x100, 999_999_999)
        };
        var sources = new[] { 0, 0, 0, 1, 0 };
        var result = TraceAggregateCells.Calculate(frames, frames.Length, i => i,
            TraceAggregateKind.BySecond, i => sources[i], new[] { "a.asc", "b.asc" });
        Assert.Equal(4, result.UniqueGroups);
        var busiest = result.Rows[0];
        Assert.Equal(0, busiest.SourceIndex);
        Assert.Equal(0, busiest.Channel);
        Assert.Equal(0L, busiest.BucketSecond);
        Assert.Equal(2, busiest.Frames);
        Assert.Equal(0, busiest.FirstFrameIndex);
        Assert.Equal(1, busiest.LastFrameIndex);
        Assert.Contains(result.Rows, row => row.SourceIndex == 1 &&
            row.Channel == 0 && row.BucketSecond == 0 && row.Frames == 1);
        Assert.Contains(result.Rows, row => row.SourceIndex == 0 &&
            row.Channel == 1 && row.BucketSecond == 0 && row.Frames == 1);
        Assert.Contains(result.Rows, row => row.SourceIndex == 0 &&
            row.Channel == 0 && row.BucketSecond == 1 && row.Frames == 1);
        Assert.Contains("not bus load", result.Description);
    }

    [Fact]
    public void NegativeSourceClockTimestampsFloorToActualSecondNotZero()
    {
        var frames = new[]
        {
            Make(0, 0x100, -1),
            Make(0, 0x101, -1_000_000_000),
            Make(0, 0x102, -1_000_000_001),
            Make(0, 0x103, long.MinValue)
        };
        var result = TraceAggregateCells.Calculate(frames, frames.Length, i => i,
            TraceAggregateKind.BySecond);
        Assert.Equal(3, result.UniqueGroups);
        var minusOne = Assert.Single(result.Rows.Where(row => row.BucketSecond == -1));
        Assert.Equal(2, minusOne.Frames);
        Assert.Contains(result.Rows, row => row.BucketSecond == -2);
        Assert.Contains(result.Rows, row => row.BucketSecond == -9_223_372_037L);
    }

    [Fact]
    public void TrafficBurstCsvShowsExactSourceClockBucketAndProvenance()
    {
        var frames = new[]
        {
            Make(0, 0x123, 2_100_000_000),
            Make(0, 0x124, 2_800_000_000)
        };
        var result = TraceAggregateCells.Calculate(frames, frames.Length, i => i,
            TraceAggregateKind.BySecond, sourcePaths: new[] { "logger.asc" });
        var row = Assert.Single(result.Rows);
        Assert.Equal(2L, row.BucketSecond);
        Assert.Equal(2, row.Frames);
        var exported = TraceAggregateCsvExport.Create(result);
        Assert.Contains("One-second bucket (source timestamp s)", exported);
        Assert.Contains(",2" + Environment.NewLine, exported);
        Assert.Contains("First merged frame (1-based)", exported);
    }

    [Fact]
    public void EmptyFilteredViewHasNoSyntheticRows()
    {
        var report = TraceAggregateCells.Calculate(Array.Empty<CanFrame>(),
            0, i => i, TraceAggregateKind.ByIdentifier);
        Assert.Empty(report.Rows);
        Assert.Equal(0, report.UniqueGroups);
    }

    [Fact]
    public void CsvContainsEvidenceFieldsAndMarksTruncatedResults()
    {
        var frames = new[]
        {
            Make(0, 0x100, 10),
            Make(0, 0x101, 20),
            Make(0, 0x102, 30)
        };
        var result = TraceAggregateCells.Calculate(frames, frames.Length, i => i,
            TraceAggregateKind.ByIdentifier, sourcePaths: new[] { "logger.asc" },
            maxRows: 1);
        var csv = TraceAggregateCsvExport.Create(result);
        Assert.Contains("First merged frame (1-based)", csv);
        Assert.Contains("Total groups,Truncated results", csv);
        Assert.Contains("\"logger.asc\"", csv);
        Assert.Contains(",3,true", csv);
        Assert.Equal(2, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void CsvEscapesUntrustedLoggerNamesAndPreventsExcelFormulaInjection()
    {
        var frames = new[] { Make(0, 0x7E0, 100) };
        var result = TraceAggregateCells.Calculate(frames, 1, i => i,
            TraceAggregateKind.ByIdentifier,
            sourcePaths: new[] { "=SUM(1,1),\"quoted\".asc" });
        var csv = TraceAggregateCsvExport.Create(result);
        Assert.Contains("\"'=SUM(1,1),\"\"quoted\"\".asc\"", csv);
        Assert.Contains("7E0", csv);
    }
}
