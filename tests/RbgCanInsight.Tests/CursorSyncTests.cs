using RbgCanInsight.Analysis;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class CursorSyncTests
{
    [Fact]
    public void NearestPositionHandlesEmptyAndSingleLists()
    {
        Assert.Equal(-1, CursorSync.NearestPosition([], 5));
        Assert.Equal(0, CursorSync.NearestPosition([10], -100));
        Assert.Equal(0, CursorSync.NearestPosition([10], 100));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 0)]
    [InlineData(14, 0)]
    [InlineData(15, 0)]   // exact tie: the earlier event wins
    [InlineData(16, 1)]
    [InlineData(29, 2)]
    [InlineData(30, 2)]
    [InlineData(1000, 3)]
    [InlineData(-5, 0)]
    public void NearestPositionPicksTheClosestKey(long target, int expected)
    {
        long[] keys = [10, 20, 30, 100];
        Assert.Equal(expected, CursorSync.NearestPosition(keys, target));
    }

    [Fact]
    public void NearestPositionMatchesALinearScanOnManyTargets()
    {
        var random = new Random(3);
        var keys = Enumerable.Range(0, 200).Select(_ => (long)random.Next(0, 5000)).Order().ToArray();
        for (var i = 0; i < 500; i++)
        {
            var target = random.Next(-100, 5200);
            var fast = keys[CursorSync.NearestPosition(keys, target)];
            var best = keys.Min(key => Math.Abs(key - target));
            Assert.Equal(best, Math.Abs(fast - target));
        }
    }

    [Fact]
    public void NearestItemWorksOnUnsortedItemsAndPrefersTheEarlierOnTies()
    {
        (string Name, long Time)[] items = [("c", 300), ("a", 100), ("b", 200)];
        Assert.Equal(1, CursorSync.NearestItem(items, item => item.Time, 120));
        Assert.Equal(1, CursorSync.NearestItem(items, item => item.Time, 150)); // tie 100 vs 200 -> 100
        Assert.Equal(0, CursorSync.NearestItem(items, item => item.Time, 9999));
        Assert.Equal(-1, CursorSync.NearestItem(Array.Empty<(string, long)>(), item => item.Item2, 1));
    }
}
