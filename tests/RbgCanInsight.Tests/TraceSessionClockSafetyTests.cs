using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Keep clocks and physical CAN channel provenance honest when several
/// independent loggers are opened as one offline dataset.</summary>
public sealed class TraceSessionClockSafetyTests
{
    private static CanFrame F(int channel, int milliseconds, byte value) =>
        new(channel, 0x123, [value], milliseconds * 1_000_000L);

    private static async Task<string> Save(params CanFrame[] frames)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rbg-clock-{Guid.NewGuid():N}.asc");
        await TraceExporter.WriteAscAsync(path, frames);
        return path;
    }

    [Fact]
    public async Task OverlappingFileSpansDoNotTriggerFalseChannelWarnings()
    {
        // File-wide ranges overlap (1..9 and 3..4 ms), but neither of the
        // individual physical-channel ranges overlaps.
        var a = await Save(F(0, 1, 1), F(0, 2, 2), F(1, 8, 3), F(1, 9, 4));
        var b = await Save(F(0, 3, 5), F(1, 3, 6), F(0, 4, 7), F(1, 4, 8));
        try
        {
            var health = new TraceImportHealth();
            var result = await TraceSession.ImportIntoAsync(
                [a, b], TraceMergeMode.ByTime, health, _ => { });
            Assert.Equal(2, result.Sources.Count);
            Assert.Equal(4, result.Sources[0].Frames);
            Assert.Equal(0.008, result.Sources[0].DurationSeconds!.Value, 6);
            Assert.DoesNotContain(health.Snapshot().IssueExamples,
                issue => issue.Code == "SESSION_OVERLAPPING_CHANNELS");
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public async Task OverlappingSameChannelProducesActionableWarningOnlyInTimeMerge()
    {
        var a = await Save(F(0, 1, 1), F(0, 3, 2));
        var b = await Save(F(0, 2, 3), F(0, 4, 4));
        try
        {
            var mergedHealth = new TraceImportHealth();
            var result = await TraceSession.ImportIntoAsync(
                [a, b], TraceMergeMode.ByTime, mergedHealth, _ => { });
            var warning = Assert.Single(mergedHealth.Snapshot().IssueExamples
                .Where(issue => issue.Code == "SESSION_OVERLAPPING_CHANNELS"));
            Assert.Equal(TraceImportIssueSeverity.Warning, warning.Severity);
            Assert.Contains("CAN 1", warning.Explanation);
            Assert.Contains("Separate channels", warning.Explanation);
            Assert.Equal(4, result.SourceIndex.Count);

            var isolatedHealth = new TraceImportHealth();
            await TraceSession.ImportIntoAsync(
                [a, b], TraceMergeMode.SeparateChannels, isolatedHealth, _ => { });
            Assert.DoesNotContain(isolatedHealth.Snapshot().IssueExamples,
                issue => issue.Code == "SESSION_OVERLAPPING_CHANNELS");
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void ChannelHealthUsesObservedMinAndMaxEvenForUnsortedCapture()
    {
        var health = new TraceImportHealth();
        health.Start(".asc");
        health.RecordFrame(F(1, 20, 1));
        health.RecordFrame(F(0, 10, 2));
        health.RecordFrame(F(1, 5, 3));
        health.RecordFrame(F(1, 15, 4));
        health.Complete();
        var channels = health.Snapshot().Channels;
        var ch1 = Assert.Single(channels.Where(c => c.Channel == 1));
        Assert.Equal(5_000_000L, ch1.FirstTimestampNanoseconds);
        Assert.Equal(20_000_000L, ch1.LastTimestampNanoseconds);
        var ch0 = Assert.Single(channels.Where(c => c.Channel == 0));
        Assert.Equal(10_000_000L, ch0.FirstTimestampNanoseconds);
        Assert.Equal(10_000_000L, ch0.LastTimestampNanoseconds);
    }
}
