using RbgCanInsight.Analysis;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class SignalChangeIndexTests
{
    private static readonly SignalSample[] Samples =
    [
        new(0, 0, 20),
        new(100, 0, 21), // repeated value is NOT a transition
        new(200, 1, 22),
        new(300, 1, 23),
        new(400, 3, 24),
        new(400, 4, 25), // same timestamp but distinct frame and new state
        new(600, 4, 26)
    ];

    [Fact]
    public void NavigationReturnsSourceBackedTransitionsAndSkipsConstantSamples()
    {
        var changes = new SignalChangeIndex(Samples);
        Assert.Equal(3, changes.Count);
        Assert.Equal(22, changes.Next(0)?.FrameIndex);
        Assert.Equal(24, changes.Next(200)?.FrameIndex);
        Assert.Equal(24, changes.Next(350)?.FrameIndex); // first event at time 400
        Assert.Equal(25, changes.Previous(500)?.FrameIndex);
        Assert.Equal(22, changes.Previous(400)?.FrameIndex);
        Assert.Null(changes.Previous(200));
        Assert.Null(changes.Next(400));
    }

    [Fact]
    public void ConstantAndEmptySignalsHaveNoTransition()
    {
        var empty = new SignalChangeIndex(Array.Empty<SignalSample>());
        Assert.Equal(0, empty.Count);
        Assert.Null(empty.Next(0));
        Assert.Null(empty.Previous(0));

        var constant = new SignalChangeIndex([new(0, 7, 1), new(100, 7, 2)]);
        Assert.Equal(0, constant.Count);
        Assert.Null(constant.Next(0));
        Assert.Null(constant.Previous(200));
    }

    [Fact]
    public void WorksWithRepeatedPhysicalStatesAndNegativeTimestamps()
    {
        var index = new SignalChangeIndex(
        [
            new(-300, 1, 1), new(-100, 2, 2),
            new(0, 1, 3), new(100, 2, 4)
        ]);
        Assert.Equal(2, index.Next(-101)?.FrameIndex);
        Assert.Equal(4, index.Next(0)?.FrameIndex);
        Assert.Equal(2, index.Previous(0)?.FrameIndex);
    }
}
