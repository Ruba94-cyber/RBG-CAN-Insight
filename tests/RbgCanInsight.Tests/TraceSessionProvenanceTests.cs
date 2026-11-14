using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Multi-source provenance must remain deterministic even with identical CAN IDs,
/// equal timestamps, out-of-order source frames and distinct merge strategies.</summary>
public sealed class TraceSessionProvenanceTests
{
    [Theory]
    [InlineData(TraceMergeMode.ByTime)]
    [InlineData(TraceMergeMode.Sequential)]
    [InlineData(TraceMergeMode.SeparateChannels)]
    public async Task MergePreservesPhysicalSourceAndPayload(TraceMergeMode mode)
    {
        var a = Path.Combine(Path.GetTempPath(), $"rbg-session-a-{Guid.NewGuid():N}.asc");
        var b = Path.Combine(Path.GetTempPath(), $"rbg-session-b-{Guid.NewGuid():N}.asc");
        try
        {
            // Source A deliberately arrives out of order; source B has a colliding ID.
            await TraceExporter.WriteAscAsync(a, new[]
            {
                new CanFrame(0, 0x700, [0xA2], 2_000_000),
                new CanFrame(0, 0x700, [0xA1], 1_000_000)
            });
            await TraceExporter.WriteAscAsync(b, new[]
            {
                new CanFrame(0, 0x700, [0xB1], 1_000_000),
                new CanFrame(0, 0x700, [0xB2], 3_000_000)
            });

            var output = new List<CanFrame>();
            var health = new TraceImportHealth();
            var result = await TraceSession.ImportIntoAsync(
                [a, b], mode, health, output.Add);

            Assert.Equal(4, output.Count);
            Assert.Equal(4, result.SourceIndex.Count);
            Assert.Equal(2, result.SourceIndex.SourceCount);
            Assert.Equal(2, result.Sources.Count);

            var seen = new HashSet<string>();
            for (var row = 0; row < output.Count; row++)
            {
                var location = result.SourceIndex.Locate(row);
                Assert.Equal(location.SourceIndex, result.SourceIndex.SourceAt(row));
                Assert.True(location.SourceIndex is 0 or 1);
                Assert.Equal(location.SourceIndex == 0 ? a : b, location.Path);
                Assert.True(location.SourceFrameIndex is 0 or 1);
                Assert.Equal(row, output[row].Sequence);
                Assert.True(row == 0 ||
                    output[row - 1].TimestampNanoseconds <= output[row].TimestampNanoseconds);

                var payload = output[row].GetPayload();
                Assert.Single(payload);
                var label = Convert.ToHexString(payload);
                Assert.True(seen.Add(label), $"Duplicate source frame {label}");
                Assert.Equal(location.SourceIndex == 0 ? 0xA0 : 0xB0, payload[0] & 0xF0);
                if (mode == TraceMergeMode.SeparateChannels)
                    Assert.Equal(location.SourceIndex, output[row].Channel);
                else
                    Assert.Equal(0, output[row].Channel);
            }
            Assert.Equal(4, seen.Count);
            Assert.False(result.SourceIndex.TryLocate(-1, out _));
            Assert.False(result.SourceIndex.TryLocate(4, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => result.SourceIndex.Locate(4));
            Assert.Throws<ArgumentOutOfRangeException>(() => result.SourceIndex.SourceAt(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => result.SourceIndex.SourceAt(4));
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    [Fact]
    public async Task CancelledImportDoesNotLeakTemporarySourceFiles()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var health = new TraceImportHealth();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TraceSession.ImportIntoAsync(
                ["unopened.asc"], TraceMergeMode.ByTime, health, _ => { }, cts.Token));
    }

    [Fact]
    public async Task EmptySourceListIsRejectedBeforeStartingImport()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            TraceSession.ImportIntoAsync(
                [], TraceMergeMode.ByTime, new TraceImportHealth(), _ => { }));
    }
}
