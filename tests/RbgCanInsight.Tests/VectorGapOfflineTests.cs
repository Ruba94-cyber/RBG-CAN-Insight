using System.Runtime.CompilerServices;
using RbgCanInsight.Analysis;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Vector gap analysis items: richer search grammar, replay break conditions, row colour rules,
/// cursor statistics, keyboard cursor math, frame difference and Tx/Rx direction.</summary>
public sealed class VectorGapOfflineTests
{
    private static CanFrame F(uint id, byte[] data, double seconds = 0, CanFrameFlags flags = CanFrameFlags.None, int channel = 0) =>
        new(channel, id, data, (long)(seconds * 1e9), flags);

    // A stand-in for the host's basic terms (ID / channel / name): matches hex IDs and "ch<n>".
    private static Func<string, Func<CanFrame, bool>> Basic => term =>
    {
        if (term.StartsWith("ch", StringComparison.OrdinalIgnoreCase) && int.TryParse(term[2..], out var ch))
            return f => f.Channel == ch - 1;
        if (uint.TryParse(term, System.Globalization.NumberStyles.HexNumber, null, out var id))
            return f => f.Identifier == id;
        return _ => false;
    };

    private static Func<CanFrame, bool> Q(string query, long origin = 0) => FrameQuery.Compile(query, origin, Basic);

    // ---- #2 search grammar --------------------------------------------------------------

    [Fact]
    public void EmptyQueryMatchesEverything() => Assert.True(Q("")(F(0x100, [1])));

    [Fact]
    public void OrSeparatesAlternativeGroupsAndAndBindsTighter()
    {
        var q = Q("100 ch2 OR 200");
        Assert.True(q(F(0x100, [], channel: 1)));      // 100 and ch2
        Assert.False(q(F(0x100, [], channel: 0)));     // 100 but wrong channel
        Assert.True(q(F(0x200, [], channel: 0)));      // second alternative
        Assert.False(q(F(0x300, [], channel: 1)));
    }

    [Fact]
    public void PipeInsideATermOffersAlternativesAndNegationAppliesToAll()
    {
        Assert.True(Q("100|200")(F(0x200, [])));
        Assert.False(Q("100|200")(F(0x300, [])));
        Assert.False(Q("-100|200")(F(0x200, [])));
        Assert.True(Q("-100|200")(F(0x300, [])));
    }

    [Fact]
    public void IdRangeAndRelations()
    {
        Assert.True(Q("id:100..1FF")(F(0x1FF, [])));
        Assert.False(Q("id:100..1FF")(F(0x200, [])));
        Assert.True(Q("id:1FF..100")(F(0x150, [])));   // reversed bounds are normalised
        Assert.True(Q("id>=100")(F(0x100, [])));
        Assert.False(Q("id>100")(F(0x100, [])));
        Assert.True(Q("id<7FF")(F(0x7FE, [])));
        Assert.True(Q("id!=100")(F(0x101, [])));
        Assert.True(Q("id:123")(F(0x123, [])));
    }

    [Theory]
    [InlineData("d0=5A", true)]
    [InlineData("d0==5a", true)]
    [InlineData("d0!=5A", false)]
    [InlineData("d1>=10", true)]
    [InlineData("d1>10", false)]
    [InlineData("d1<11", true)]
    [InlineData("d2=FF", false)]          // byte 2 is not in a 2-byte payload
    [InlineData("d2!=FF", false)]         // a missing byte never satisfies any relation
    public void ByteRelations(string term, bool expected) =>
        Assert.Equal(expected, Q(term)(F(0x100, [0x5A, 0x10])));

    [Fact]
    public void MasksAndBitPatterns()
    {
        var frame = F(0x100, [0b1010_0101]);
        Assert.True(Q("d0&0F=05")(frame));
        Assert.False(Q("d0&0F=0A")(frame));
        Assert.True(Q("d0&F0!=50")(frame));
        Assert.True(Q("d0=b1xxxx1x1")(frame));
        Assert.False(Q("d0=b0xxxxxxx")(frame));
        Assert.True(Q("d0!=b0xxxxxxx")(frame));
        Assert.True(Q("d0=bxxxxxxxx")(frame));        // all don't-care matches any present byte
    }

    [Fact]
    public void LengthRelations()
    {
        Assert.True(Q("dlc=3")(F(1, [1, 2, 3])));
        Assert.True(Q("dlc>=3")(F(1, [1, 2, 3])));
        Assert.False(Q("dlc>3")(F(1, [1, 2, 3])));
        Assert.True(Q("dlc<8")(F(1, [1, 2, 3])));
    }

    [Fact]
    public void TimeWindowIsRelativeToTheOrigin()
    {
        var origin = 100_000_000_000L; // trace starts at 100 s
        var early = new CanFrame(0, 1, [], origin + 1_000_000_000L);
        var inside = new CanFrame(0, 1, [], origin + 2_500_000_000L);
        var late = new CanFrame(0, 1, [], origin + 9_000_000_000L);
        var window = Q("t:2..3", origin);
        Assert.False(window(early));
        Assert.True(window(inside));
        Assert.False(window(late));
        Assert.True(Q("t>=2", origin)(late));
        Assert.True(Q("t<=2", origin)(early));
        Assert.True(Q("t:..5", origin)(inside));
        Assert.False(Q("t:..5", origin)(late));
        Assert.True(Q("t:5..", origin)(late));
    }

    [Fact]
    public void AdvancedTermsCombineWithAndOrAndNegation()
    {
        var q = Q("id:100..1FF d0=01 OR dlc=0 -ch2");
        Assert.True(q(F(0x150, [1])));
        Assert.False(q(F(0x150, [2])));
        Assert.True(q(F(0x700, [], channel: 0)));
        Assert.False(q(F(0x700, [], channel: 1)));    // -ch2 applies to the second group only
    }

    [Fact]
    public void MalformedAdvancedTermsMatchNothingAndReportAnError()
    {
        Assert.False(Q("d0=ZZ")(F(0x100, [0])));
        Assert.False(Q("id:ZZ..10")(F(0x100, [0])));
        Assert.False(Q("d99=00")(F(0x100, [0])));
        Assert.False(FrameQuery.TryParseAdvancedTerm("d0=ZZ", 0, out _, out var error));
        Assert.NotNull(error);
        Assert.False(FrameQuery.TryParseAdvancedTerm("100", 0, out _, out var none));
        Assert.Null(none);
    }

    [Fact]
    public void DirectionTermUsesTheTransmitFlag()
    {
        var tx = F(1, [1], flags: CanFrameFlags.Transmit);
        var rx = F(1, [1]);
        Assert.True(Q("dir:tx")(tx));
        Assert.False(Q("dir:tx")(rx));
        Assert.True(Q("dir:rx")(rx));
        Assert.True(Q("-dir:tx")(rx));
    }

    [Fact]
    public void PlainLegacyTermsStillGoThroughTheBasicFactory()
    {
        Assert.True(Q("ch1 100")(F(0x100, [])));
        Assert.False(Q("ch2")(F(0x100, [])));
    }

    // ---- #2 query builder ------------------------------------------------------------------

    [Fact]
    public void BuilderComposesAdvancedFieldsAndOr()
    {
        var result = TraceQueryBuilder.Compose(
        [
            new(QueryField.IdRange, "100..1FF"),
            new(QueryField.ByteRelation, "0&0F=05"),
            new(QueryField.TimeWindow, "1.5..3", OrWithPrevious: true),
            new(QueryField.DataLength, ">=4", Exclude: true),
            new(QueryField.IdRange, "<=7FF")
        ]);
        Assert.True(result.IsValid, string.Join(';', result.Errors));
        Assert.Equal("id:100..1FF d0&0F=05 OR t:1.5..3 -dlc>=4 id<=7FF", result.Query);
    }

    [Theory]
    [InlineData(QueryField.IdRange, "zz")]
    [InlineData(QueryField.IdRange, "")]
    [InlineData(QueryField.ByteRelation, "0=GG")]
    [InlineData(QueryField.ByteRelation, "70=00")]
    [InlineData(QueryField.TimeWindow, "abc")]
    [InlineData(QueryField.DataLength, "x")]
    public void BuilderRejectsInvalidAdvancedValues(QueryField field, string value)
    {
        var result = TraceQueryBuilder.Compose([new QueryCondition(field, value)]);
        Assert.False(result.IsValid);
        Assert.Equal(string.Empty, result.Query);
    }

    [Fact]
    public void BuilderRoundTripsAdvancedTermsAndOr()
    {
        const string query = "id:100..1FF d0&0F=05 OR t:1.5..3 -dlc>=4 dir:tx";
        var conditions = TraceQueryBuilder.Parse(query, out var unrecognized);
        Assert.Empty(unrecognized);
        Assert.Equal(5, conditions.Count);
        Assert.True(conditions[2].OrWithPrevious);
        Assert.Equal(query, TraceQueryBuilder.Compose(conditions).Query);
        Assert.Contains(" OR ", TraceQueryBuilder.Describe(conditions));
    }

    [Fact]
    public void BuilderLeavesPerTermAlternativesToTextEditing()
    {
        TraceQueryBuilder.Parse("100|200 ch1", out var unrecognized);
        Assert.Equal(["100|200"], unrecognized);
    }

    // ---- #1 replay / break conditions --------------------------------------------------------

    private static List<CanFrame> Frames(int count) =>
        Enumerable.Range(0, count).Select(i => F(0x100, [(byte)(i % 256)], i * 0.01)).ToList();

    [Fact]
    public void StepClampsToTheTrace()
    {
        Assert.Equal(1, ReplayNavigator.Step(0, 1, 10));
        Assert.Equal(0, ReplayNavigator.Step(0, -1, 10));
        Assert.Equal(9, ReplayNavigator.Step(9, 1, 10));
        Assert.Equal(9, ReplayNavigator.Step(5, int.MaxValue, 10));
        Assert.Equal(-1, ReplayNavigator.Step(0, 1, 0));
    }

    [Fact]
    public void FindMatchSearchesStrictlyAfterOrBeforeAndReportsMisses()
    {
        var frames = Frames(100);
        var condition = Q("d0=32");                // payload byte 0x32 = frame 50
        Assert.Equal(50, ReplayNavigator.FindMatch(frames, 0, 1, condition));
        Assert.Equal(-1, ReplayNavigator.FindMatch(frames, 50, 1, condition));   // strictly after
        Assert.Equal(50, ReplayNavigator.FindMatch(frames, 99, -1, condition));
        Assert.Equal(-1, ReplayNavigator.FindMatch(frames, 50, -1, condition));
        Assert.Equal(-1, ReplayNavigator.FindMatch([], 0, 1, condition));
    }

    [Fact]
    public void FindMatchHonoursCancellation()
    {
        var frames = Frames(200_000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => ReplayNavigator.FindMatch(frames, -1, 1, _ => false, cts.Token));
    }

    [Fact]
    public void AdvanceUntilMatchStopsOnTheFirstHitOrAtTheStep()
    {
        var frames = Frames(100);
        var idx = ReplayNavigator.AdvanceUntilMatch(frames, 10, 30, Q("d0=1E"), out var hit); // 0x1E = 30
        Assert.True(hit);
        Assert.Equal(30, idx);

        idx = ReplayNavigator.AdvanceUntilMatch(frames, 10, 5, Q("d0=1E"), out hit);
        Assert.False(hit);
        Assert.Equal(15, idx);

        idx = ReplayNavigator.AdvanceUntilMatch(frames, 95, 50, null, out hit);
        Assert.False(hit);
        Assert.Equal(99, idx);
    }

    [Fact]
    public void RunToMatchScansAMillionFramesQuickly()
    {
        var frames = new List<CanFrame>(1_000_000);
        for (var i = 0; i < 1_000_000; i++) frames.Add(new CanFrame(0, 0x100, [(byte)i], i * 1000L));
        var condition = Q("d0&FF=AA id:100..1FF dlc=1 t>=0.9");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var found = ReplayNavigator.FindMatch(frames, 0, 1, condition);
        watch.Stop();
        Assert.True(found > 900_000 - 300);
        Assert.True(watch.ElapsedMilliseconds < 3000, $"scan took {watch.ElapsedMilliseconds} ms");
    }

    // ---- #6 row colours --------------------------------------------------------------------

    [Fact]
    public void RowColourRulesFirstMatchWinsAndInvalidRulesAreIgnored()
    {
        var rules = new[]
        {
            new RowColourRule("", "#55FF0000"),                 // empty query: ignored
            new RowColourRule("100", "not-a-colour"),           // invalid colour: ignored
            new RowColourRule("id:100..1FF d0=01", "#55E53935"),
            new RowColourRule("id:100..1FF", "#551E88E5")
        };
        var colour = RowColourRules.Compile(rules, 0, Basic);
        Assert.Equal("#55E53935", colour(F(0x150, [1])));
        Assert.Equal("#551E88E5", colour(F(0x150, [2])));
        Assert.Null(colour(F(0x300, [1])));
        Assert.Null(RowColourRules.Compile([], 0, Basic)(F(1, [])));
    }

    [Fact]
    public void ColourPaletteRoundTripsAndValidates()
    {
        foreach (var (name, hex) in RowColourRules.Palette)
        {
            Assert.True(RowColourRules.IsValidColour(hex));
            Assert.Equal(hex, RowColourRules.HexForName(name));
            Assert.Equal(name, RowColourRules.NameForHex(hex));
        }

        Assert.Equal(RowColourRules.NoColourName, RowColourRules.NameForHex(null));
        Assert.Null(RowColourRules.HexForName(RowColourRules.NoColourName));
        Assert.True(RowColourRules.IsValidColour("#FF0000"));
        Assert.False(RowColourRules.IsValidColour("FF0000"));
        Assert.False(RowColourRules.IsValidColour("#GG0000"));
    }

    // ---- #9 statistics between cursors -------------------------------------------------------

    private static SignalSample[] Samples(params (double t, double v)[] items) =>
        items.Select((item, i) => new SignalSample((long)(item.t * 1e9), item.v, i)).ToArray();

    [Fact]
    public void RangeStatisticsUseSampleAndHoldWeighting()
    {
        // 0 s: 10, 1 s: 20, 3 s: 40. Window 0.5 .. 4 s holds 10 for 0.5 s, 20 for 2 s, 40 for 1 s.
        var samples = Samples((0, 10), (1, 20), (3, 40));
        var stats = SignalRangeStatistics.Compute(samples, (long)(4e9), (long)(0.5e9));   // cursors in either order
        Assert.NotNull(stats);
        var s = stats.Value;
        Assert.Equal(2, s.SampleCount);
        Assert.Equal(10, s.Minimum);               // value held at 0.5 s counts
        Assert.Equal(40, s.Maximum);
        Assert.Equal(30, s.Mean, 9);               // mean of the recorded samples inside: (20 + 40) / 2
        Assert.Equal((10 * 0.5 + 20 * 2 + 40 * 1) / 3.5, s.TimeWeightedMean, 9);
        Assert.Equal(10, s.HeldAtStart);
        Assert.Equal(3.5, s.SpanSeconds, 9);
    }

    [Fact]
    public void RangeStatisticsDoNotInventValuesBeforeTheFirstSample()
    {
        var samples = Samples((2, 10), (3, 30));
        var s = SignalRangeStatistics.Compute(samples, 0, (long)4e9)!.Value;
        Assert.Null(s.HeldAtStart);
        Assert.Equal(2, s.SampleCount);
        Assert.Equal((10 * 1 + 30 * 1) / 2.0, s.TimeWeightedMean, 9);   // only the known 2 s are weighted
        Assert.Null(SignalRangeStatistics.Compute(samples, 0, (long)1e9));  // nothing known yet
        Assert.Null(SignalRangeStatistics.Compute([], 0, (long)1e9));
    }

    [Fact]
    public void RangeStatisticsForAWindowBetweenTwoSamplesHoldsTheLastValue()
    {
        var samples = Samples((0, 5), (10, 9));
        var s = SignalRangeStatistics.Compute(samples, (long)2e9, (long)4e9)!.Value;
        Assert.Equal(0, s.SampleCount);
        Assert.Equal(5, s.Minimum);
        Assert.Equal(5, s.Maximum);
        Assert.Equal(5, s.TimeWeightedMean, 9);
        Assert.True(double.IsNaN(s.Mean));
    }

    [Fact]
    public void RangeStatisticsIgnoreNonFiniteSamples()
    {
        var samples = Samples((0, 1), (1, double.NaN), (2, 3));
        var s = SignalRangeStatistics.Compute(samples, 0, (long)2e9)!.Value;
        Assert.Equal(2, s.SampleCount);
        Assert.Equal(1, s.Minimum);
        Assert.Equal(3, s.Maximum);
    }

    // ---- #10 keyboard cursors and line styles ------------------------------------------------

    [Fact]
    public void LineStylesCycleThroughEveryStyle()
    {
        var seen = new List<PlotLineStyle>();
        var style = PlotLineStyle.Auto;
        for (var i = 0; i < 4; i++) { seen.Add(style); style = PlotCursorMath.NextStyle(style); }
        Assert.Equal(PlotLineStyle.Auto, style);
        Assert.Equal(4, seen.Distinct().Count());
        Assert.All(Enum.GetValues<PlotLineStyle>(), s => Assert.False(string.IsNullOrWhiteSpace(PlotCursorMath.StyleLabel(s))));
    }

    [Fact]
    public void CursorMovesByPixelsAndClampsToTheView()
    {
        // 1000 ns across 100 px = 10 ns per pixel.
        Assert.Equal(550, PlotCursorMath.MoveByPixels(500, 0, 1000, 100, 5));
        Assert.Equal(450, PlotCursorMath.MoveByPixels(500, 0, 1000, 100, -5));
        Assert.Equal(1000, PlotCursorMath.MoveByPixels(990, 0, 1000, 100, 50));
        Assert.Equal(0, PlotCursorMath.MoveByPixels(5, 0, 1000, 100, -50));
        Assert.Equal(510, PlotCursorMath.MoveByPixels(-1, 0, 1000, 100, 1));   // unset cursor starts at the centre
        Assert.Equal(0, PlotCursorMath.MoveByPixels(5, 0, 0, 100, 1));
    }

    [Fact]
    public void CursorJumpsToNeighbouringSamples()
    {
        var samples = Samples((1, 0), (2, 0), (3, 0));
        long Sec(double s) => (long)(s * 1e9);
        Assert.Equal(Sec(2), PlotCursorMath.NextSampleTime(samples, Sec(1), 1));
        Assert.Equal(Sec(3), PlotCursorMath.NextSampleTime(samples, Sec(2.5), 1));
        Assert.Null(PlotCursorMath.NextSampleTime(samples, Sec(3), 1));
        Assert.Equal(Sec(1), PlotCursorMath.NextSampleTime(samples, Sec(2), -1));
        Assert.Equal(Sec(2), PlotCursorMath.NextSampleTime(samples, Sec(2.5), -1));
        Assert.Null(PlotCursorMath.NextSampleTime(samples, Sec(1), -1));
        Assert.Null(PlotCursorMath.NextSampleTime([], 0, 1));
    }

    // ---- #7 frame difference -----------------------------------------------------------------

    [Fact]
    public void DifferenceFlagsChangedBytesBitsAndProperties()
    {
        var frames = new (int, CanFrame)[]
        {
            (0, F(0x100, [0x10, 0xFF, 0x00], 1.0)),
            (5, F(0x100, [0x10, 0xFE, 0x01], 2.0)),
            (9, F(0x101, [0x10, 0xFF], 3.0, channel: 1))
        };
        var result = FrameDifference.Compute(frames, null, 0);
        Assert.Equal(["#1", "#6", "#10"], result.FrameHeaders);
        Assert.False(result.Properties.Single(p => p.Label == "Time [s]").Differs);
        Assert.True(result.Properties.Single(p => p.Label == "ID").Differs);
        Assert.True(result.Properties.Single(p => p.Label == "Channel").Differs);
        Assert.True(result.Properties.Single(p => p.Label == "Length").Differs);

        Assert.Equal(3, result.Bytes.Count);
        Assert.False(result.Bytes[0].Differs);
        Assert.True(result.Bytes[1].Differs);
        Assert.Contains("differing bits: 0", result.Bytes[1].Summary);
        Assert.Equal(["00", "01", "--"], result.Bytes[2].Cells);
        Assert.Equal(2, result.DifferingBytes);
        Assert.Empty(result.Signals);
    }

    [Fact]
    public void DifferenceSummarisesDecodedSignalsOverTheChosenFrames()
    {
        var frames = new (int, CanFrame)[]
        {
            (0, F(0x100, [10], 0)), (1, F(0x100, [20], 1)), (2, F(0x100, [60], 2))
        };
        IReadOnlyList<SignalValue> Decode(CanFrame f) =>
            [new SignalValue("db", "Engine", "Speed", f.GetByte(0), f.GetByte(0), "km/h", f.TimestampNanoseconds, 0, f.Identifier)];
        var result = FrameDifference.Compute(frames, Decode, 0);
        var speed = Assert.Single(result.Signals);
        Assert.Equal("Engine.Speed", speed.Label);
        Assert.True(speed.Differs);
        Assert.Contains("min 10", speed.Summary);
        Assert.Contains("max 60", speed.Summary);
        Assert.Contains("mean 30", speed.Summary);
        Assert.Contains("n=3", speed.Summary);
    }

    [Fact]
    public void DifferenceIsCappedAtTheMaximumFrameCount()
    {
        var frames = Enumerable.Range(0, 40).Select(i => (i, F(0x100, [(byte)i]))).ToArray();
        Assert.Equal(FrameDifference.MaxFrames, FrameDifference.Compute(frames, null, 0).FrameHeaders.Count);
    }

    // ---- #3 direction ------------------------------------------------------------------------

    [Fact]
    public void TransmitFlagDoesNotChangeFrameSizeOrUnrelatedEquality()
    {
        // Spare bit of the existing flag byte: the struct layout is untouched.
        Assert.Equal(1, Unsafe.SizeOf<CanFrameFlags>());
        Assert.True(Unsafe.SizeOf<CanFrame>() <= 96);
        Assert.Equal(F(0x100, [1, 2], 1), F(0x100, [1, 2], 1));
        Assert.False(F(0x100, [1, 2], 1).IsTransmit);
        Assert.True(F(0x100, [1, 2], 1, CanFrameFlags.Transmit | CanFrameFlags.Extended).IsTransmit);
    }

    private static async Task<List<CanFrame>> RoundTrip(string extension, IReadOnlyList<CanFrame> frames)
    {
        var path = Path.Combine(Path.GetTempPath(), "rbg-dir-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            switch (extension)
            {
                case ".asc": await TraceExporter.WriteAscAsync(path, frames); break;
                case ".trc": await TraceExporter.WriteTrcAsync(path, frames); break;
                default: await TraceExporter.WriteBlfAsync(path, frames); break;
            }

            var read = new List<CanFrame>();
            await foreach (var frame in TraceImporter.ReadAsync(path)) read.Add(frame);
            return read;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(".asc")]
    [InlineData(".trc")]
    [InlineData(".blf")]
    public async Task DirectionRoundTripsThroughWriterAndImporter(string extension)
    {
        var frames = new[]
        {
            F(0x100, [1, 2], 1.0, CanFrameFlags.Transmit),
            F(0x101, [3], 2.0),
            F(0x18FEF100, [4, 5, 6], 3.0, CanFrameFlags.Extended | CanFrameFlags.Transmit)
        };
        var read = await RoundTrip(extension, frames);
        Assert.Equal(3, read.Count);
        Assert.Equal([true, false, true], read.Select(f => f.IsTransmit));
        Assert.Equal(frames.Select(f => f.Identifier), read.Select(f => f.Identifier));
    }

    [Fact]
    public async Task AscImporterReadsTxAndRxFromHandWrittenLines()
    {
        var path = Path.Combine(Path.GetTempPath(), "rbg-dir-" + Guid.NewGuid().ToString("N") + ".asc");
        await File.WriteAllLinesAsync(path,
        [
            "date Tue Jan 02 10:00:00.000 2024",
            "base hex  timestamps absolute",
            "   0.010000 1  123             Rx   d 2 01 02  Length = 0 BitCount = 0 ID = 291",
            "   0.020000 1  124             Tx   d 1 AA  Length = 0 BitCount = 0 ID = 292",
            "   0.030000 1  125             TxRq d 1 BB",
            "   0.040000 CANFD 2 Tx 126 BRS 1 ESI 0 9 12 00 01 02 03 04 05 06 07 08 09 0A 0B 0 0 0 0 0"
        ]);
        try
        {
            var read = new List<CanFrame>();
            await foreach (var frame in TraceImporter.ReadAsync(path)) read.Add(frame);
            var byId = read.ToDictionary(f => f.Identifier);
            Assert.False(byId[0x123].IsTransmit);
            Assert.True(byId[0x124].IsTransmit);
            Assert.False(byId[0x125].IsTransmit);      // a transmit REQUEST is not a transmitted frame
            if (byId.TryGetValue(0x126, out var fd)) Assert.True(fd.IsTransmit);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
