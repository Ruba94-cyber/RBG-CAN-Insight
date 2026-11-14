using Xunit;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;

namespace RbgCanInsight.Tests;

public class TraceToolsTests
{
    private static CanFrame F(long ts, int ch = 0, uint id = 0x100, byte b = 0, CanFrameFlags fl = CanFrameFlags.None, long seq = 99) =>
        new(ch, id, new byte[] { b, 2, 3 }, ts, fl, seq);

    [Fact]
    public void Merge_IsStableAndOffsetsChannels()
    {
        IReadOnlyList<CanFrame> a = [F(10, b: 1), F(30, b: 2)];
        IReadOnlyList<CanFrame> b = [F(10, b: 3), F(20, b: 4)];
        var m = TraceTools.Merge([a, b], [0, 4]);
        Assert.Equal([1, 3, 4, 2], m.Select(x => (int)x.GetByte(0)));
        Assert.Equal([0, 4, 4, 0], m.Select(x => x.Channel));
        Assert.Equal([0L, 1, 2, 3], m.Select(x => x.Sequence));
        Assert.Equal(99, a[0].Sequence); // input untouched
    }

    [Fact]
    public void Merge_RejectsUnsortedSource() =>
        Assert.Throws<ArgumentException>(() => TraceTools.Merge([new[] { F(5), F(1) }]));

    [Fact]
    public void ShiftTime_RejectsNegativeAndShifts()
    {
        var shifted = TraceTools.ShiftTime([F(100), F(200)], -100);
        Assert.Equal([0L, 100], shifted.Select(x => x.TimestampNanoseconds));
        Assert.Throws<ArgumentOutOfRangeException>(() => TraceTools.ShiftTime([F(100)], -101));
    }

    [Fact]
    public void Cut_IsInclusiveExclusive()
    {
        var c = TraceTools.Cut([F(10), F(20), F(30)], 10, 30);
        Assert.Equal([10L, 20], c.Select(x => x.TimestampNanoseconds));
    }

    [Fact]
    public void SplitAtGaps_SplitsAtThreshold()
    {
        var s = TraceTools.SplitAtGaps([F(0), F(5), F(105), F(110)], 100);
        Assert.Equal(2, s.Count);
        Assert.Equal(2, s[1].Count);
        Assert.Equal(0, s[1][0].Sequence);
        Assert.Empty(TraceTools.SplitAtGaps([], 10));
    }

    [Fact]
    public void RemapAndSelect_PreserveFlagsPayloadAndFd()
    {
        var fl = CanFrameFlags.Extended | CanFrameFlags.FlexibleDataRate | CanFrameFlags.BitRateSwitch;
        var fd = new CanFrame(1, 0x1ABCDEF, Enumerable.Range(0, 64).Select(i => (byte)i).ToArray(), 7, fl);
        var rtr = new CanFrame(1, 0x10, ReadOnlySpan<byte>.Empty, 8, CanFrameFlags.Remote, 0, 4);
        var r = TraceTools.RemapChannels([fd, rtr], new Dictionary<int, int> { [1] = 5 });
        Assert.All(r, x => Assert.Equal(5, x.Channel));
        Assert.Equal(fd.GetPayload(), r[0].GetPayload());
        Assert.Equal(fl, r[0].Flags);
        Assert.Equal(4, r[1].DeclaredDataLength);
        var sel = TraceTools.SelectIds(r, f => f.Identifier == 0x10);
        Assert.Single(sel);
        Assert.Equal(0, sel[0].Sequence);
    }
}
