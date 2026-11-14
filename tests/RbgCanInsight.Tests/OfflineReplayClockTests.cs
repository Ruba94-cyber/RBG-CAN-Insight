using RbgCanInsight.Analysis;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class OfflineReplayClockTests
{
    [Theory]
    [InlineData(1.0, 180_000_000L)]
    [InlineData(2.0, 360_000_000L)]
    [InlineData(0.1, 18_000_000L)]
    [InlineData(10.0, 1_800_000_000L)]
    public void ReplayAdvancesByActualElapsedWallTime(double speed, long expected)
    {
        Assert.Equal(expected, OfflineReplayClock.Advance(0, 5_000_000_000L,
            TimeSpan.FromMilliseconds(180), speed));
    }

    [Fact]
    public void SparseSamplesDoNotCauseUnrequestedFastForward()
    {
        // A signal observed only every ten seconds still holds its last value at 0.18s.
        Assert.Equal(180_000_000L, OfflineReplayClock.Advance(0, 10_000_000_000L,
            TimeSpan.FromMilliseconds(180), 1));
    }

    [Fact]
    public void ReplayNeverMovesBeyondEndOrWrapsOnHugeElapsedTime()
    {
        Assert.Equal(1_000_000_000L, OfflineReplayClock.Advance(900_000_000L,
            1_000_000_000L, TimeSpan.FromSeconds(1), 100));
        Assert.Equal(long.MaxValue, OfflineReplayClock.Advance(long.MaxValue - 5,
            long.MaxValue, TimeSpan.FromDays(999), 100));
    }

    [Fact]
    public void PauseAndInvalidSpeedsDoNotMoveCursor()
    {
        Assert.Equal(123, OfflineReplayClock.Advance(123, 456, TimeSpan.Zero, 1));
        Assert.Equal(123, OfflineReplayClock.Advance(123, 456, TimeSpan.FromSeconds(1),
            double.NaN));
    }
}
