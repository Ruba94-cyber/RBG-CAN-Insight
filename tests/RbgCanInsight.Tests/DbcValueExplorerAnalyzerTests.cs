using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DbcValueExplorerAnalyzerTests
{
    [Fact]
    public void ObservedUnknownCodesAreNotMistakenForDBCDeclaredStates()
    {
        var (frames, timeline) = Create(
            new DbcSignal("Mode", 0, 8, DbcByteOrder.Intel,
                false, 1, 0, 0, 1, "", "ECM",
                ValueDescriptions: new Dictionary<long, string>
                {
                    [0] = "Off",
                    [1] = "On",
                    [3] = "Declared but never seen"
                }),
            [new byte[] { 0 }, new byte[] { 1 },
             new byte[] { 1 }, new byte[] { 2 }]);
        var report = DbcValueExplorerAnalyzer.Analyze(timeline, frames);
        Assert.Equal(4, report.ObservedSamples);
        Assert.Equal(1, report.ObservedUnknownRawCodes);
        Assert.Equal(1, report.ObservedOutsideDeclaredPhysicalRange);
        Assert.Equal(3, report.RawCodes.Count);
        Assert.Equal(3, report.DeclaredNamedCodes);
        var on = report.RawCodes.Single(row => row.ExactRaw == "1");
        Assert.Equal("On", on.DeclaredMeaning);
        Assert.Equal(2, on.ObservedCount);
        Assert.Equal(1, on.FirstSourceFrameIndex);
        Assert.Equal(2, on.LastSourceFrameIndex);
        Assert.Equal("Unknown / not declared",
            report.RawCodes.Single(row => row.ExactRaw == "2").Meaning);
        Assert.DoesNotContain(report.RawCodes, row => row.DeclaredMeaning ==
            "Declared but never seen");
    }

    [Fact]
    public void Unsigned64BitDistinctRawValuesBeyondDoublePrecisionRemainDifferent()
    {
        const ulong code1 = 9_007_199_254_740_992UL;
        const ulong code2 = 9_007_199_254_740_993UL;
        var signal = new DbcSignal("Big", 0, 64, DbcByteOrder.Intel, false,
            1, 0, 0, double.MaxValue, "", "ECM");
        var (frames, timeline) = Create(signal,
            [BitConverter.GetBytes(code1), BitConverter.GetBytes(code2)]);
        var report = DbcValueExplorerAnalyzer.Analyze(timeline, frames);
        Assert.Equal(2, report.ObservedSamples);
        Assert.Equal(2, report.RawCodes.Count);
        Assert.Equal(2, report.ObservedWithoutExactDoubleRawRepresentation);
        Assert.Contains(report.RawCodes, row => row.ExactRaw ==
            "9007199254740992" && row.HexRaw == "0x0020000000000000");
        Assert.Contains(report.RawCodes, row => row.ExactRaw ==
            "9007199254740993" && row.HexRaw == "0x0020000000000001");
        Assert.Contains("18446744073709551615", report.RawDomain);
    }

    [Fact]
    public void Unsigned64BitAboveSignedLongMaximumIsRecordedAsUnsignedNotNegative()
    {
        const ulong big = 0x8000_0000_0000_0001UL;
        var signal = new DbcSignal("Big", 0, 64, DbcByteOrder.Intel,
            false, 1, 0, 0, double.MaxValue, "", "ECM",
            ValueDescriptions: new Dictionary<long, string> { [1] = "One" });
        var (frames, timeline) = Create(signal, [BitConverter.GetBytes(big)]);
        var report = DbcValueExplorerAnalyzer.Analyze(timeline, frames);
        Assert.Equal("9223372036854775809", Assert.Single(report.RawCodes).ExactRaw);
        Assert.Equal("0x8000000000000001", report.RawCodes[0].HexRaw);
        Assert.Null(report.RawCodes[0].DeclaredMeaning);
        Assert.Equal(1, report.ObservedUnknownRawCodes);
    }

    [Fact]
    public void SignedTwoComplement64BitRawCodeIsPreservedExactly()
    {
        var signal = new DbcSignal("Signed", 0, 64, DbcByteOrder.Intel,
            true, 1, 0, double.MinValue, double.MaxValue, "", "ECM",
            ValueDescriptions: new Dictionary<long, string>
            {
                [-1] = "Negative one"
            });
        var (frames, timeline) = Create(signal, [BitConverter.GetBytes(ulong.MaxValue)]);
        var report = DbcValueExplorerAnalyzer.Analyze(timeline, frames);
        var raw = Assert.Single(report.RawCodes);
        Assert.Equal("-1", raw.ExactRaw);
        Assert.Equal("Negative one", raw.DeclaredMeaning);
        Assert.Equal("0xFFFFFFFFFFFFFFFF", raw.HexRaw);
        Assert.Equal(0, report.ObservedUnknownRawCodes);
    }

    [Fact]
    public void MotorolaBitOrderUsesExactRecordedBigEndianValue()
    {
        var signal = new DbcSignal("BE", 7, 16, DbcByteOrder.Motorola,
            false, 1, 0, 0, 65535, "", "ECM");
        var (frames, timeline) = Create(signal, [new byte[] { 0x12, 0x34 }]);
        var report = DbcValueExplorerAnalyzer.Analyze(timeline, frames);
        var raw = Assert.Single(report.RawCodes);
        Assert.Equal("4660", raw.ExactRaw);
        Assert.Equal("0x1234", raw.HexRaw);
    }

    [Fact]
    public void DistinctLimitNeverImpliesMissingCodesWereCountedInTrackedHistogram()
    {
        var signal = new DbcSignal("Code", 0, 8, DbcByteOrder.Intel,
            false, 1, 0, 0, 255, "", "ECM");
        var (frames, timeline) = Create(signal,
            [new byte[] { 0 }, new byte[] { 1 }, new byte[] { 2 },
             new byte[] { 0 }, new byte[] { 3 }, new byte[] { 1 }]);
        var report = DbcValueExplorerAnalyzer.Analyze(timeline, frames,
            maxDistinctCodes: 2);
        Assert.Equal(6, report.ObservedSamples);
        Assert.Equal(2, report.RawCodes.Count);
        Assert.Equal(2, report.UntrackedSamplesDueToDistinctLimit);
        Assert.Equal(2, report.RawCodes.Single(row => row.ExactRaw == "0").ObservedCount);
        Assert.Equal(2, report.RawCodes.Single(row => row.ExactRaw == "1").ObservedCount);
        Assert.Contains("not a complete histogram", report.Warning);
    }

    [Fact]
    public void CannotCertifyRawCodeWhenSourceFrameIsUnavailable()
    {
        var (frames, timeline) = Create(
            new DbcSignal("Mode", 0, 8, DbcByteOrder.Intel,
                false, 1, 0, 0, 10, "", "ECM"),
            [new byte[] { 1 }]);
        var report = DbcValueExplorerAnalyzer.Analyze(timeline, []);
        Assert.Equal(1, report.UnavailableOriginalFrames);
        Assert.Empty(report.RawCodes);
        Assert.Equal(1, report.ObservedSamples);
    }

    [Fact]
    public void CancellationAndInvalidCodeCapsAreRejectedBeforeAnalysis()
    {
        var (frames, timeline) = Create(
            new DbcSignal("Mode", 0, 8, DbcByteOrder.Intel,
                false, 1, 0, 0, 10, "", "ECM"),
            [new byte[] { 1 }]);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DbcValueExplorerAnalyzer.Analyze(timeline, frames,
                maxDistinctCodes: 4097));
        using var tokenSource = new CancellationTokenSource();
        tokenSource.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            DbcValueExplorerAnalyzer.Analyze(timeline, frames,
                cancellationToken: tokenSource.Token));
    }

    private static (CanFrame[] Frames, SignalTimeline Timeline) Create(
        DbcSignal signal, byte[][] payloads)
    {
        var message = new DbcMessage(0x123, "Status", 8, "ECM", false, [signal]);
        var database = new DbcDatabase("Primary",
            new Dictionary<uint, DbcMessage> { [0x123] = message });
        var frames = payloads.Select((data, i) =>
            new CanFrame(0, 0x123, data, i * 1000L, sequence: i + 1)).ToArray();
        return (frames, Assert.Single(SignalTimelineBuilder.Build(frames,
            [database])));
    }
}
