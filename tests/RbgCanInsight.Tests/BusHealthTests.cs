using Xunit;
using RbgCanInsight.Analysis.BusHealth;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Tests;

public sealed class BusHealthTests
{
    private static CanFrame F(int ch, uint id, int len, long tsNs, CanFrameFlags flags = CanFrameFlags.None) =>
        new(ch, id, new byte[len], tsNs, flags);

    private static CanFrame Remote(int ch, uint id, long tsNs) =>
        new(ch, id, ReadOnlySpan<byte>.Empty, tsNs, CanFrameFlags.Remote, 0, 4);

    private static readonly CanFrameFlags FdBrs =
        CanFrameFlags.FlexibleDataRate | CanFrameFlags.BitRateSwitch;

    [Fact]
    public void ClassicStandardFrame8Bytes_HandComputedBits()
    {
        var frame = F(0, 0x123, 8, 0);
        // 47 + 8*8 = 111 unstuffed; stuffed region g = 34 + 64 = 98; worst extra = floor(97/4) = 24.
        Assert.Equal(new FrameBits(111, 0), CanFrameBitModel.Estimate(frame, StuffingModel.None));
        Assert.Equal(new FrameBits(135, 0), CanFrameBitModel.Estimate(frame, StuffingModel.WorstCase));
    }

    [Fact]
    public void ClassicExtendedFrame0Bytes_HandComputedBits()
    {
        var frame = F(0, 0x1234567, 0, 0, CanFrameFlags.Extended);
        // 67 unstuffed; g = 54; worst extra = floor(53/4) = 13.
        Assert.Equal(67, CanFrameBitModel.Estimate(frame, StuffingModel.None).Total);
        Assert.Equal(80, CanFrameBitModel.Estimate(frame, StuffingModel.WorstCase).Total);
    }

    [Fact]
    public void FdStandardFrame12BytesWithBrs_HandComputedBits()
    {
        var frame = F(0, 0x100, 12, 0, FdBrs);
        // Nominal: 17 + 12 = 29 (+ floor(16/4) = 4 worst case stuffing -> 33).
        // Data: ESI1 + DLC4 + 96 + SC4 + CRC17 + CRCdel1 = 123, + 6 fixed CRC stuff bits = 129,
        //       + worst dynamic floor((5+96-1)/4) = 25 -> 154.
        Assert.Equal(new FrameBits(29, 129), CanFrameBitModel.Estimate(frame, StuffingModel.None));
        Assert.Equal(new FrameBits(33, 154), CanFrameBitModel.Estimate(frame, StuffingModel.WorstCase));
    }

    [Fact]
    public void FdFrameWithoutBrs_AllBitsAtNominalRate()
    {
        var frame = F(0, 0x100, 12, 0, CanFrameFlags.FlexibleDataRate);
        Assert.Equal(new FrameBits(33 + 154, 0), CanFrameBitModel.Estimate(frame, StuffingModel.WorstCase));
    }

    [Fact]
    public void FdLengthIsRoundedUpAndLongFramesUseCrc21()
    {
        Assert.Equal(12, CanFrameBitModel.RoundUpToFdLength(9));
        // 64 bytes, std, no stuffing: nominal 29; data 5 + 512 + 4 + 21 + 1 + ceil(25/4)=7 -> 550.
        var frame = F(0, 0x1, 64, 0, FdBrs);
        Assert.Equal(new FrameBits(29, 550), CanFrameBitModel.Estimate(frame, StuffingModel.None));
    }

    [Fact]
    public void BusLoad_UsesSeparateDataPhaseBitrate()
    {
        var options = new BusHealthOptions(1_000_000, // 1 ms buckets
            new Dictionary<int, ChannelBitrate> { [0] = new(500_000, 2_000_000) });
        var frames = new[] { F(0, 0x100, 12, 0, FdBrs), F(0, 0x123, 8, 1_500_000) };
        var result = BusHealthTimeline.Compute(frames, options);
        var buckets = result.Channels.Single().Buckets;
        Assert.Equal(2, buckets.Count);
        // 33 bits @500k + 154 bits @2M = 66 us + 77 us = 143 us of 1 ms.
        Assert.Equal(14.3, buckets[0].BusLoadPercent, 6);
        // 135 bits @500k = 270 us.
        Assert.Equal(27.0, buckets[1].BusLoadPercent, 6);
    }

    [Fact]
    public void Buckets_CountErrorRemoteDistinctAndTopIds()
    {
        var options = new BusHealthOptions(1_000_000_000,
            new Dictionary<int, ChannelBitrate> { [0] = new(500_000), [1] = new(250_000) },
            StuffingModel.None, TopIdCount: 2);
        var frames = new List<CanFrame>
        {
            F(0, 0x10, 1, 0), F(0, 0x10, 1, 100), F(0, 0x20, 1, 200),
            Remote(0, 0x30, 300),
            new(0, 0, ReadOnlySpan<byte>.Empty, 400, CanFrameFlags.Error),
            F(0, 0x10, 1, 2_500_000_000), // bucket 2, bucket 1 stays empty
            F(1, 0x99, 2, 50),
        };
        var result = BusHealthTimeline.Compute(frames, options);
        var ch0 = result.Channels.Single(c => c.Channel == 0);
        Assert.Equal(3, ch0.Buckets.Count);
        Assert.Equal(5, ch0.Buckets[0].FrameCount);
        Assert.Equal(1, ch0.Buckets[0].ErrorFrames);
        Assert.Equal(1, ch0.Buckets[0].RemoteFrames);
        Assert.Equal(3, ch0.Buckets[0].DistinctIds);
        Assert.Equal(0, ch0.Buckets[1].FrameCount);
        Assert.Equal(1, ch0.Buckets[2].DistinctIds);
        Assert.Equal(2, ch0.TopIds.Count);
        Assert.Equal(0x10u, ch0.TopIds[0].Identifier);
        Assert.Equal([2, 0, 1], ch0.TopIds[0].FramesPerBucket);
        Assert.Equal(1, result.Channels.Single(c => c.Channel == 1).Buckets.Count);
    }

    [Fact]
    public void MissingBitrates_AreNeverGuessed()
    {
        var frames = new[] { F(3, 0x1, 1, 0) };
        Assert.Throws<ArgumentException>(() => BusHealthTimeline.Compute(frames,
            new BusHealthOptions(1000, new Dictionary<int, ChannelBitrate> { [0] = new(500_000) })));

        var fd = new[] { F(0, 0x1, 12, 0, FdBrs) };
        Assert.Throws<ArgumentException>(() => BusHealthTimeline.Compute(fd,
            new BusHealthOptions(1000, new Dictionary<int, ChannelBitrate> { [0] = new(500_000) })));
    }

    [Fact]
    public void Latency_Distribution_PercentilesAndHistogram()
    {
        var d = RequestResponseLatency.FromMilliseconds([40, 10, 30, 20, 100], [25, 50])!;
        Assert.Equal(5, d.Count);
        Assert.Equal(10, d.MinMs);
        Assert.Equal(30, d.MedianMs);
        Assert.Equal(88, d.P95Ms, 9); // pos 3.8 -> 40 + 0.8 * 60
        Assert.Equal(100, d.MaxMs);
        Assert.Equal([2, 2, 1], d.Histogram.Select(h => h.Count));
        Assert.Null(d.Histogram[^1].UpperMs);
    }

    [Fact]
    public void Latency_FromPairs_ConvertsNanosecondsAndSkipsNegative()
    {
        var d = RequestResponseLatency.FromPairs([(0L, 5_000_000L), (10L, 2_000_010L), (100L, 50L)])!;
        Assert.Equal(2, d.Count);
        Assert.Equal(2, d.MinMs);
        Assert.Equal(5, d.MaxMs);
        Assert.Null(RequestResponseLatency.FromMilliseconds([]));
    }
}
