using RbgCanInsight.Analysis.Heuristics;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class AnalysisHeuristicsTests
{
    private static DbcDatabase Db(string name, params DbcMessage[] messages) =>
        new(name, messages.GroupBy(m => m.Identifier).ToDictionary(g => g.Key, g => g.First()),
            null, messages.ToDictionary(m => new DbcMessageIdentity(m.Identifier, m.IsExtended)));

    private static DbcMessage Msg(uint id, bool ext, int len = 8, double max = 255) =>
        new(id, $"M{id:X}{(ext ? "x" : "")}", len, "ECU", ext,
            [new DbcSignal("S", 0, 8, DbcByteOrder.Intel, false, 1, 0, 0, max, "", "")]);

    private static CanFrame Frame(int ch, uint id, bool ext, byte[] data, long t = 0) =>
        new(ch, id, data, t, ext ? CanFrameFlags.Extended : CanFrameFlags.None);

    [Fact]
    public void DbcRankingDistinguishesFrameFormatAndReportsBreakdown()
    {
        var good = Db("good", Msg(0x123, false), Msg(0x200, false));
        var confused = Db("confused", Msg(0x123, true), Msg(0x200, true)); // same numbers, 29-bit
        var frames = new List<CanFrame>();
        for (var i = 0; i < 10; i++)
        {
            frames.Add(Frame(0, 0x123, false, [5, 0, 0, 0, 0, 0, 0, 0], i));
            frames.Add(Frame(0, 0x200, false, [7, 0, 0, 0, 0, 0, 0, 0], i));
        }
        frames.Add(Frame(0, 0x7FF, false, [1], 99));

        var result = DbcSuggester.Rank(frames,
        [
            new DbcSuggestionCandidate("confused", confused),
            new DbcSuggestionCandidate("good", good)
        ]);

        var channel = Assert.Single(result);
        Assert.Equal("good", channel.Ranking[0].DatabaseName);
        var b = channel.Ranking[0].Breakdown;
        Assert.Equal(3, b.DistinctIdentifiers);
        Assert.Equal(2, b.MatchedIdentifiers);
        Assert.Equal(20, b.MatchedFrames);
        Assert.Equal(21, b.Frames);
        Assert.Equal(1.0, b.DlcShare);
        Assert.Equal(1.0, b.SignalRangeShare);
        Assert.Equal(0.4 * 2 / 3 + 0.3 * 20 / 21 + 0.15 + 0.15, b.CompositeScore, 9);
        var unmatched = Assert.Single(channel.Ranking[0].UnmatchedIdentifiers);
        Assert.Equal(0x7FFu, unmatched.Identifier);
        Assert.Equal(0, channel.Ranking[1].Breakdown.MatchedIdentifiers);
        Assert.Equal(3, channel.Ranking[1].UnmatchedIdentifiers.Count);
    }

    [Fact]
    public void DbcRankingIsPerChannelAndChecksDlcAndRange()
    {
        var dbA = Db("A", Msg(0x100, false, len: 8, max: 100));
        var frames = new List<CanFrame>
        {
            Frame(0, 0x100, false, [50, 0, 0, 0, 0, 0, 0, 0]),
            Frame(0, 0x100, false, [200, 0, 0, 0, 0, 0, 0, 0]), // out of 0..100
            Frame(0, 0x100, false, [50, 0, 0, 0]),              // shorter than DBC DLC 8
            Frame(1, 0x555, false, [1, 2])
        };
        var result = DbcSuggester.Rank(frames, [new DbcSuggestionCandidate("A", dbA)]);
        Assert.Equal([0, 1], result.Select(c => c.Channel).ToArray());
        var b0 = result[0].Ranking[0].Breakdown;
        Assert.Equal(3, b0.MatchedFrames);
        Assert.Equal(2, b0.DlcAgreeingFrames);
        Assert.Equal(3, b0.SignalSamplesChecked);
        Assert.Equal(2, b0.SignalSamplesInRange);
        Assert.Equal(0, result[1].Ranking[0].Breakdown.CompositeScore);
    }

    [Fact]
    public void J1939DatabaseMatchesByPgnOnlyWhenRequested()
    {
        var j1939 = Db("j1939", Msg(0x18FEF100, true));
        var frames = new[] { Frame(0, 0x18FEF117, true, [1, 2, 3, 4, 5, 6, 7, 8]) };
        var byPgn = DbcSuggester.Rank(frames, [new DbcSuggestionCandidate("j", j1939, MatchByPgn: true)]);
        var exact = DbcSuggester.Rank(frames, [new DbcSuggestionCandidate("j", j1939)]);
        Assert.Equal(1, byPgn[0].Ranking[0].Breakdown.MatchedIdentifiers);
        Assert.Equal(0, exact[0].Ranking[0].Breakdown.MatchedIdentifiers);
    }

    [Fact]
    public void TieBreakIsDeterministicByNameThenInputOrder()
    {
        var frames = new[] { Frame(0, 0x10, false, [1]) };
        var empty = Db("x");
        var r = DbcSuggester.Rank(frames,
        [
            new DbcSuggestionCandidate("b", empty),
            new DbcSuggestionCandidate("a", empty),
            new DbcSuggestionCandidate("a", empty)
        ]);
        Assert.Equal(["a", "a", "b"], r[0].Ranking.Select(s => s.DatabaseName).ToArray());
        Assert.Equal([1, 2, 0], r[0].Ranking.Select(s => s.CandidateIndex).ToArray());
    }

    private static List<byte[]> Series(int count, Func<int, byte[], byte[]> finish)
    {
        var rng = new Random(1234);
        var list = new List<byte[]>();
        for (var i = 0; i < count; i++)
        {
            var p = new byte[8];
            rng.NextBytes(p);
            list.Add(finish(i, p));
        }
        return list;
    }

    [Fact]
    public void DetectsFourBitCounterInByte6LowNibble()
    {
        var series = Series(100, (i, p) =>
        {
            p[6] = (byte)((p[6] & 0xF0) | (i & 0xF));
            return p;
        });
        var a = ByteSignalHeuristics.Analyze(0, 0x321, false, series);
        var counter = Assert.Single(a.Counters, c => c.StartBit == 48);
        Assert.Equal(4, counter.BitLength);
        Assert.Equal(16, counter.Modulus);
        Assert.Equal(99, counter.TotalTransitions);
        Assert.Equal(99, counter.ConsistentTransitions);
        Assert.Equal(1.0, counter.Confidence);
        Assert.Equal("candidate", CounterCandidate.Label);
        Assert.DoesNotContain(a.Counters, c => c.StartBit == 48 && c.BitLength < 4);
    }

    [Fact]
    public void DetectsXorChecksumInLastByte()
    {
        var series = Series(100, (i, p) =>
        {
            p[6] = (byte)((p[6] & 0xF0) | (i & 0xF));
            p[7] = (byte)(p[0] ^ p[1] ^ p[2] ^ p[3] ^ p[4] ^ p[5] ^ p[6]);
            return p;
        });
        var a = ByteSignalHeuristics.Analyze(0, 1, false, series);
        var hit = Assert.Single(a.Checksums, c => c.Algorithm == "Xor" && c.ByteIndex == 7);
        Assert.Equal(100, hit.ConsistentFrames);
        Assert.Equal(100, hit.TotalFrames);
        Assert.DoesNotContain(a.Checksums, c => c.Algorithm.StartsWith("Crc8") && c.ByteIndex == 7);
    }

    [Fact]
    public void DetectsSumAndTwosComplementChecksums()
    {
        var sum = Series(60, (_, p) => { p[7] = (byte)p.Take(7).Sum(x => x); return p; });
        var twos = Series(60, (_, p) => { p[7] = (byte)(-p.Take(7).Sum(x => x)); return p; });
        Assert.Contains(ByteSignalHeuristics.Analyze(0, 1, false, sum).Checksums, c => c.Algorithm == "Sum8");
        Assert.Contains(ByteSignalHeuristics.Analyze(0, 1, false, twos).Checksums,
            c => c.Algorithm == "TwosComplementSum8");
    }

    [Fact]
    public void DetectsCrc8J1850Checksum()
    {
        var series = Series(100, (i, p) =>
        {
            p[7] = ByteSignalHeuristics.Crc8(0x1D, 0xFF, 0xFF, p.AsSpan(0, 7));
            return p;
        });
        var a = ByteSignalHeuristics.Analyze(0, 1, false, series);
        var hit = Assert.Single(a.Checksums, c => c.Algorithm == "Crc8_J1850_1D");
        Assert.Equal(7, hit.ByteIndex);
        Assert.Equal(100, hit.ConsistentFrames);
        Assert.DoesNotContain(a.Checksums, c => c.Algorithm == "Xor");
    }

    [Fact]
    public void Crc8MatchesPublishedCheckValues()
    {
        var data = "123456789"u8;
        Assert.Equal(0x4B, ByteSignalHeuristics.Crc8(0x1D, 0xFF, 0xFF, data));
        Assert.Equal(0xDF, ByteSignalHeuristics.Crc8(0x2F, 0xFF, 0xFF, data));
        Assert.Equal(0xF4, ByteSignalHeuristics.Crc8(0x07, 0x00, 0x00, data));
    }

    [Fact]
    public void ConstantMaskToggleRatesAndEntropy()
    {
        var series = Series(64, (i, p) =>
        {
            p[0] = 0xA5;                                  // fully constant byte
            p[1] = (byte)((p[1] & 0x0F) | 0x30);          // high nibble constant 0x3
            p[2] = (byte)(i % 2 == 0 ? 0x00 : 0x01);     // bit 0 toggles every frame
            p[3] = 0x00;
            return p;
        });
        var a = ByteSignalHeuristics.Analyze(0, 1, false, series);
        Assert.Equal(0.0, a.ByteEntropyBits[0]);
        Assert.Equal(0.0, a.ByteEntropyBits[3]);
        Assert.True(a.ByteEntropyBits[4] > 4);
        Assert.Equal(0xFF, a.ConstantBitMask[0]);
        Assert.Equal(0xA5, a.ConstantBitValues[0]);
        Assert.Equal(0xF0, a.ConstantBitMask[1] & 0xF0);
        Assert.Equal(0x30, a.ConstantBitValues[1] & 0xF0);
        Assert.Equal(1.0, a.BitToggleRates[16]);
        Assert.Equal(0.0, a.BitToggleRates[0]);
        Assert.Equal(1.0, a.ByteEntropyBits[2]);
        Assert.True(a.ConstantBitCount >= 8 + 4 + 7 + 8);
    }

    [Fact]
    public void AnalyzeFramesGroupsByChannelIdAndFormat()
    {
        var frames = new List<CanFrame>();
        for (var i = 0; i < 20; i++)
        {
            frames.Add(Frame(0, 0x10, false, [(byte)i, 0]));
            frames.Add(Frame(0, 0x10, true, [0, 0]));
        }
        var all = ByteSignalHeuristics.AnalyzeFrames(frames);
        Assert.Equal(2, all.Count);
        Assert.False(all[0].IsExtended);
        Assert.True(all[1].IsExtended);
        Assert.Equal(20, all[0].AnalyzedFrames);
        var only = ByteSignalHeuristics.AnalyzeFrames(frames, (_, _, ext) => ext);
        Assert.Single(only);
    }
}
