using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class LoggedStatsAndSessionTests
{
    private static string Tmp(string ext) => Path.Combine(Path.GetTempPath(), $"rbg-sess-{Guid.NewGuid():N}{ext}");

    private static string Asc(double t0, string extra = "") =>
        "date Mon Nov 9 10:00:00.000 am 2026\nbase hex  timestamps absolute\ninternal events logged\n" +
        extra +
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"   {t0 + 0.1:F6} 1  100             Rx   d 2 01 02\n") +
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"   {t0 + 0.2:F6} 2  200             Rx   d 1 AA\n");

    [Fact]
    public void ParsesStatisticAndStatusLines()
    {
        Assert.True(LoggedBusMetadata.TryParseStatistic(
            "   1.015991 1  Statistic: D 10 R 1 XD 2 XR 3 E 4 O 5 B 12.50%", out var s));
        Assert.Equal(1_015_991_000L, s.TimestampNanoseconds);
        Assert.Equal(0, s.Channel);
        Assert.Equal((10, 1, 2, 3, 4, 5), ((int)s.DataFrames, (int)s.RemoteFrames, (int)s.ExtendedDataFrames,
            (int)s.ExtendedRemoteFrames, (int)s.ErrorFrames, (int)s.OverloadFrames));
        Assert.Equal(12.5, s.BusLoadPercent);

        Assert.True(LoggedBusMetadata.TryParseStatus(
            "   0.015991 CAN 2 Status:chip status error passive - TxErr: 132 RxErr: 7", out var st));
        Assert.Equal(1, st.Channel);
        Assert.Equal("error passive", st.State);
        Assert.Equal(132, st.TxErrorCounter);
        Assert.Equal(7, st.RxErrorCounter);

        Assert.False(LoggedBusMetadata.TryParseStatistic("   1.0 1 100 Rx d 1 00", out _));
    }

    [Fact]
    public async Task AscImportCollectsLoggedMetadataWithoutCountingItAsRejected()
    {
        var path = Tmp(".asc");
        await File.WriteAllTextAsync(path, Asc(0,
            "   0.050000 CAN 1 Status:chip status error active\n" +
            "   1.000000 1  Statistic: D 5 R 0 XD 0 XR 0 E 1 O 0 B 33.00%\n" +
            "   1.000000 2  Statistic: D 1 R 0 XD 0 XR 0 E 0 O 0 B 4.00%\n"));
        try
        {
            var health = new TraceImportHealth();
            var n = 0;
            await foreach (var _ in TraceImporter.ReadAsync(path, diagnostics: health)) n++;
            var snap = health.Snapshot();
            Assert.Equal(2, n);
            Assert.Equal(0, snap.RejectedRecords);
            Assert.Equal(2, snap.LoggedBusStatistics!.Count);
            Assert.Equal(33.0, LoggedBusMetadata.LoggedMeanLoad(snap.LoggedBusStatistics, 0));
            Assert.Equal(33.0, LoggedBusMetadata.LoggedLoadInWindow(snap.LoggedBusStatistics, 0, 1_000_000_000L, 1_000_000_000L));
            Assert.Null(LoggedBusMetadata.LoggedLoadInWindow(snap.LoggedBusStatistics, 0, 5_000_000_000L, 1_000_000_000L));
            Assert.Equal("error active", Assert.Single(snap.LoggedControllerStatuses!).State);
        }
        finally { File.Delete(path); }
    }

    private static CanFrame F(int ch, long ts) => new(ch, 0x100, [1], ts, CanFrameFlags.None);

    [Fact]
    public void MergeModesByTimeSequentialAndSeparateChannels()
    {
        IReadOnlyList<CanFrame> a = [F(0, 10), F(1, 30)];
        IReadOnlyList<CanFrame> b = [F(0, 5), F(0, 20)];

        var byTime = TraceTools.MergeSession([a, b], TraceMergeMode.ByTime);
        Assert.Equal([5L, 10, 20, 30], byTime.Frames.Select(f => f.TimestampNanoseconds));
        Assert.Equal([0, 0, 0, 1], byTime.Frames.Select(f => f.Channel));

        var seq = TraceTools.MergeSession([a, b], TraceMergeMode.Sequential);
        Assert.Equal([10L, 30, 30, 45], seq.Frames.Select(f => f.TimestampNanoseconds));
        Assert.Equal(25, seq.TimeShiftsNs[1]);

        var sep = TraceTools.MergeSession([a, b], TraceMergeMode.SeparateChannels);
        Assert.Equal([0, 2], sep.ChannelOffsets);
        Assert.Equal([2, 0, 2, 1], sep.Frames.Select(f => f.Channel));
    }

    [Fact]
    public async Task BoundedMultiFileImport_ReportsExactSourceRowsAndOriginalChannels()
    {
        var first = Tmp(".asc");
        var second = Tmp(".asc");
        await File.WriteAllTextAsync(first, Asc(0));
        await File.WriteAllTextAsync(second, Asc(0,
            "   0.150000 1  300             Rx   d 1 55\n"));
        try
        {
            var health = new TraceImportHealth();
            var collected = new List<CanFrame>();
            var result = await TraceSession.ImportIntoAsync([first, second],
                TraceMergeMode.SeparateChannels, health, collected.Add);
            Assert.Equal(5, collected.Count);
            Assert.Equal(collected.Count, result.SourceIndex.Count);
            Assert.Equal(2, result.SourceIndex.SourceCount);
            Assert.Equal(new[] { first, second }, result.SourceIndex.Paths);
            Assert.Equal(5, health.Snapshot().Frames);
            Assert.Equal(new[] { 0, 1, 1, 0, 1 },
                Enumerable.Range(0, 5).Select(i => result.SourceIndex.Locate(i).SourceIndex).ToArray());
            Assert.Equal(1, result.SourceIndex.Locate(2).SourceFrameIndex);

            var report = TraceSourceCoverage.Analyze(collected, result.SourceIndex,
                TraceMergeMode.SeparateChannels);
            var shared = Assert.Single(report.Rows.Where(r => r.Identifier == 0x100));
            Assert.Equal(0, shared.OriginalChannel);
            Assert.Equal(new long[] { 1, 1 }, shared.Counts);
            Assert.Equal(2, shared.PresentSources);
            var oneFileOnly = Assert.Single(report.Rows.Where(r => r.Identifier == 0x300));
            Assert.Equal(new long[] { 0, 1 }, oneFileOnly.Counts);
            Assert.Equal("Single source", oneFileOnly.Finding);
            Assert.Equal(1, report.SingleSourceIdentifiers);
            Assert.False(report.IsTruncated);
        }
        finally { File.Delete(first); File.Delete(second); }
    }

    [Theory]
    [InlineData(TraceMergeMode.ByTime)]
    [InlineData(TraceMergeMode.Sequential)]
    [InlineData(TraceMergeMode.SeparateChannels)]
    public async Task BoundedImport_MatchesReferenceMergeMode(TraceMergeMode mode)
    {
        var first = Tmp(".asc");
        var second = Tmp(".asc");
        await File.WriteAllTextAsync(first, Asc(2));
        await File.WriteAllTextAsync(second, Asc(0));
        try
        {
            var a = new List<CanFrame>();
            var b = new List<CanFrame>();
            await foreach (var frame in TraceImporter.ReadAsync(first)) a.Add(frame);
            await foreach (var frame in TraceImporter.ReadAsync(second)) b.Add(frame);
            var sorted = new[] { a, b }.Select(source =>
                (IReadOnlyList<CanFrame>)source.Select((f, i) => (Frame: f, Position: i))
                    .OrderBy(x => x.Frame.TimestampNanoseconds).ThenBy(x => x.Position)
                    .Select(x => x.Frame).ToList()).ToArray();
            var expected = TraceTools.MergeSession(sorted, mode).Frames;
            var actual = new List<CanFrame>();
            var health = new TraceImportHealth();
            await TraceSession.ImportIntoAsync([first, second], mode, health, actual.Add);
            Assert.Equal(expected, actual);
        }
        finally { File.Delete(first); File.Delete(second); }
    }

    [Fact]
    public async Task SessionImportMergesFilesAndShiftsLoggedStatistics()
    {
        var p1 = Tmp(".asc");
        var p2 = Tmp(".asc");
        await File.WriteAllTextAsync(p1, Asc(0, "   0.900000 1  Statistic: D 5 R 0 XD 0 XR 0 E 0 O 0 B 10.00%\n"));
        await File.WriteAllTextAsync(p2, Asc(0, "   0.500000 1  Statistic: D 5 R 0 XD 0 XR 0 E 0 O 0 B 20.00%\n"));
        try
        {
            var health = new TraceImportHealth();
            var frames = await TraceSession.ImportAsync([p1, p2], TraceMergeMode.SeparateChannels, health);
            Assert.Equal(4, frames.Count);
            Assert.Equal(frames.OrderBy(f => f.TimestampNanoseconds).Select(f => f.TimestampNanoseconds),
                frames.Select(f => f.TimestampNanoseconds));
            var snap = health.Snapshot();
            Assert.Equal(4, snap.Frames);
            Assert.Equal(2, snap.LoggedBusStatistics!.Count);
            // second file's channel 0 became channel 2 (first file used channels 0 and 1)
            Assert.Contains(snap.LoggedBusStatistics, s => s.Channel == 2 && s.BusLoadPercent == 20.0);
        }
        finally { File.Delete(p1); File.Delete(p2); }
    }
}
