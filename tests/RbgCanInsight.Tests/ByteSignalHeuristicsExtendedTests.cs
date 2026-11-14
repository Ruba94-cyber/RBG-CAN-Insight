using System.Text;
using RbgCanInsight.Analysis.Heuristics;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class ByteSignalHeuristicsExtendedTests
{
    private static List<byte[]> Series(int count, Func<int, byte[], byte[]> finish, int seed = 99, int len = 8)
    {
        var rng = new Random(seed);
        var list = new List<byte[]>();
        for (var i = 0; i < count; i++)
        {
            var p = new byte[len];
            rng.NextBytes(p);
            list.Add(finish(i, p));
        }
        return list;
    }

    [Fact]
    public void Crc16CheckValues()
    {
        var data = Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0x29B1, ByteSignalHeuristics.Crc16CcittFalse(data));
        Assert.Equal(0xBB3D, ByteSignalHeuristics.Crc16Ibm(data));
    }

    [Fact]
    public void DetectsMotorolaCounterSpanningTwoBytes()
    {
        // DBC @0 start bit 9 (byte 1 bit 1), 10 bits: byte1 bits 1..0 then byte2 bits 7..0.
        var series = Series(700, (i, p) =>
        {
            var v = i & 0x3FF;
            p[1] = (byte)((p[1] & ~3) | (v >> 8));
            p[2] = (byte)(v & 0xFF);
            return p;
        });
        var a = ByteSignalHeuristics.Analyze(0, 0x100, false, series);
        var m = Assert.Single(a.Counters, c => c.MotorolaOrder);
        Assert.Equal(9, m.StartBit);
        Assert.Equal(10, m.BitLength);
        Assert.Equal(1, m.Step);
        Assert.Equal(1.0, m.Confidence);
        Assert.Equal("candidate", CounterCandidate.Label);
    }

    [Fact]
    public void PaddingBitsDoNotCreateMotorolaCounters()
    {
        // Plain 8-bit counter in byte 3 with constant neighbours: no byte-spanning big-endian field may appear.
        var series = Series(400, (i, p) => { p[3] = (byte)i; p[2] = 0; p[4] = 0; return p; });
        var a = ByteSignalHeuristics.Analyze(0, 1, false, series);
        Assert.Contains(a.Counters, c => !c.MotorolaOrder && c.StartBit == 24 && c.BitLength == 8 && c.Step == 1);
        Assert.DoesNotContain(a.Counters, c => c.MotorolaOrder);
    }

    [Fact]
    public void DetectsStepTwoCounter()
    {
        var series = Series(300, (i, p) => { p[3] = (byte)(2 * i); return p; });
        var a = ByteSignalHeuristics.Analyze(0, 1, false, series);
        var s = Assert.Single(a.Counters, c => c.Step == 2);
        Assert.Equal(24, s.StartBit);
        Assert.Equal(8, s.BitLength);
        Assert.Equal(1.0, s.Confidence);
    }

    [Fact]
    public void IrregularStepIsNotAccepted()
    {
        // increments alternate +1/+2: never 95% consistent for any single step
        var v = 0;
        var series = Series(300, (i, p) => { v += i % 2 == 0 ? 1 : 2; p[3] = (byte)v; return p; });
        var a = ByteSignalHeuristics.Analyze(0, 1, false, series);
        Assert.DoesNotContain(a.Counters, c => c.Step != 1);
        Assert.DoesNotContain(a.Counters, c => c.BitLength >= 4 && c.StartBit == 24);
    }

    [Fact]
    public void DetectsChecksumOverBytePrefixInMiddle()
    {
        var series = Series(80, (i, p) =>
        {
            p[5] = (byte)(p[0] ^ p[1] ^ p[2] ^ p[3] ^ p[4]);
            return p;
        });
        var a = ByteSignalHeuristics.Analyze(0, 1, false, series);
        var hit = Assert.Single(a.Checksums, c => c.Algorithm == "Xor(bytes 0..4)");
        Assert.Equal(5, hit.ByteIndex);
        Assert.Equal(1.0, hit.Fit);
        Assert.DoesNotContain(a.Checksums, c => c.Algorithm == "Xor" && c.ByteIndex == 5);
    }

    [Fact]
    public void DetectsChecksumExcludingCounterByte()
    {
        var series = Series(120, (i, p) =>
        {
            p[6] = (byte)i;
            p[7] = (byte)(p[0] ^ p[1] ^ p[2] ^ p[3] ^ p[4] ^ p[5]);
            return p;
        });
        var a = ByteSignalHeuristics.Analyze(0, 1, false, series);
        var hit = Assert.Single(a.Checksums, c => c.ByteIndex == 7 && c.Algorithm == "Xor(all bytes except counter byte 6)");
        Assert.Equal(7, hit.ByteIndex);
        Assert.DoesNotContain(a.Checksums, c => c.Algorithm == "Xor" && c.ByteIndex == 7);
    }

    [Fact]
    public void DetectsCrc16CcittFalseBigEndian()
    {
        var series = Series(100, (i, p) =>
        {
            var crc = ByteSignalHeuristics.Crc16CcittFalse(p.AsSpan(0, 6));
            p[6] = (byte)(crc >> 8);
            p[7] = (byte)crc;
            return p;
        });
        var a = ByteSignalHeuristics.Analyze(0, 1, false, series);
        var hit = Assert.Single(a.Checksums, c => c.Algorithm.StartsWith("Crc16_CcittFalse") && c.Algorithm.Contains("big-endian"));
        Assert.Equal(6, hit.ByteIndex);
        Assert.Equal(2, hit.ByteCount);
        Assert.Equal(1.0, hit.Fit);
        Assert.DoesNotContain(a.Checksums, c => c.Algorithm.StartsWith("Crc16_Ibm"));
    }

    [Fact]
    public void DetectsCrc16IbmLittleEndianExcludingCounter()
    {
        var series = Series(100, (i, p) =>
        {
            p[3] = (byte)i;
            byte[] input = [p[0], p[1], p[2], p[4], p[5]];
            var crc = ByteSignalHeuristics.Crc16Ibm(input);
            p[6] = (byte)crc;
            p[7] = (byte)(crc >> 8);
            return p;
        });
        var a = ByteSignalHeuristics.Analyze(0, 1, false, series);
        Assert.Contains(a.Checksums, c => c.ByteIndex == 6 && c.ByteCount == 2 &&
            c.Algorithm == "Crc16_Ibm_8005_Reflected(little-endian, excluding counter byte 3)");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RandomPayloadsYieldNoCandidates(int seed)
    {
        var series = Series(600, (_, p) => p, seed);
        var a = ByteSignalHeuristics.Analyze(0, 1, false, series);
        Assert.Empty(a.Counters);
        Assert.Empty(a.Checksums);
    }

    [Fact]
    public void ConstantPayloadsYieldNoCandidates()
    {
        var series = Enumerable.Range(0, 100).Select(_ => new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).ToList();
        var a = ByteSignalHeuristics.Analyze(0, 1, false, series);
        Assert.Empty(a.Counters);
        Assert.Empty(a.Checksums);
    }

    [Fact]
    public void TooFewFramesYieldNoCrcOrRangeCandidates()
    {
        var series = Series(10, (i, p) =>
        {
            var crc = ByteSignalHeuristics.Crc16CcittFalse(p.AsSpan(0, 6));
            p[6] = (byte)(crc >> 8);
            p[7] = (byte)crc;
            return p;
        });
        var a = ByteSignalHeuristics.Analyze(0, 1, false, series);
        Assert.Empty(a.Checksums);
    }
}
