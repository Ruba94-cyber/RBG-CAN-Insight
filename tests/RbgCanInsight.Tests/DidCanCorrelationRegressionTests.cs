using RbgCanInsight.Analysis;
using RbgCanInsight.Correlation;
using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DidCanCorrelationRegressionTests
{
    private static DbcDatabase Database(string name, double factor = 1) =>
        new DbcParser().Parse(name, new StringReader(
            $"BO_ 256 EngineData: 8 ECM\n SG_ EngCoolantTemp : 0|8@1+ ({factor.ToString(System.Globalization.CultureInfo.InvariantCulture)},0) [0|255] \"degC\" Vector__XXX\n"));

    private static CorrelationCandidate Candidate(DbcDatabase database) =>
        new(
            new DidParameter(0xF405, "EngineCoolantTemperature", "Coolant temperature",
                "Measured temperature", "degC", 0, 255, 1, 0,
                "A_UINT8", 8, "ECM", "test.cdd"),
            database.Messages[256], database.Messages[256].Signals[0], database.Name,
            0.88, ["unit and name metadata"]);

    private static DiagnosticDidSample Sample(int did, double value, int index) =>
        new(did, "Temperature", "Temperature", value, value.ToString(),
            "degC", index * 1_000_000_000L, index, "ECM", "Recorded");

    private static CanFrame Frame(byte value, int index) =>
        new(0, 256, [value, 0, 0, 0, 0, 0, 0, 0],
            index * 1_000_000_000L);

    private static TimeCorrelationOptions NoLag => new(
        MaxCandidates: 1, MaximumLagMilliseconds: 0,
        LagStepMilliseconds: 25, MaximumPairDistanceMilliseconds: 1);

    [Fact]
    public void CorrelationNeverUsesObservationsOfAnotherDid()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 7).Select(i => Frame((byte)i, i)).ToArray();
        var timelines = SignalTimelineBuilder.Build(frames, [database]);
        var mixed = Enumerable.Range(0, 7).SelectMany(i => new[]
        {
            Sample(0xF405, i, i),
            Sample(0xF190, 1000 + i, i)
        }).ToArray();

        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            mixed, [Candidate(database)], timelines, NoLag));

        Assert.Equal(7, result.PairCount);
        Assert.All(result.Points, point => Assert.InRange(point.DidValue, 0, 6));
    }

    [Fact]
    public void ConstantSeriesCannotBeConfirmedAsDiagnosticMapping()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 7).Select(i => Frame(50, i)).ToArray();
        var timelines = SignalTimelineBuilder.Build(frames, [database]);
        var samples = Enumerable.Range(0, 7).Select(i => Sample(0xF405, 50, i)).ToArray();

        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            samples, [Candidate(database)], timelines, NoLag));

        Assert.Equal(CorrelationVerdict.InsufficientData, result.Verdict);
        Assert.Contains("insufficient independent value variation", result.Explanation);
    }

    [Fact]
    public void RepeatingDidsAgainstOnlyTwoCanFramesCannotBeConfirmed()
    {
        var database = Database("engine");
        var frames = new[]
        {
            new CanFrame(0, 256, [10, 0, 0, 0, 0, 0, 0, 0], 0),
            new CanFrame(0, 256, [20, 0, 0, 0, 0, 0, 0, 0], 10_000_000_000L)
        };
        var timelines = SignalTimelineBuilder.Build(frames, [database]);
        var samples = Enumerable.Range(0, 8).Select(index =>
        {
            var second = index >= 4;
            var time = (second ? 10_000_000_000L : 0) + (index % 4) * 10_000_000L;
            return new DiagnosticDidSample(0xF405, "Temp", "Temp", second ? 20 : 10,
                "", "degC", time, index, "ECM", "Recorded", 0);
        }).ToArray();

        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            samples, [Candidate(database)], timelines,
            new TimeCorrelationOptions(MaxCandidates: 1, MaximumLagMilliseconds: 0,
                MaximumPairDistanceMilliseconds: 100, MinimumPairsForCandidate: 3)));

        Assert.Equal(8, result.PairCount);
        Assert.Equal(CorrelationVerdict.InsufficientData, result.Verdict);
        Assert.Contains("insufficient independent CAN samples", result.Explanation);
    }

    [Fact]
    public void DirectCorrelationRejectsMixedDiagnosticEcus()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 6).Select(i => Frame((byte)i, i)).ToArray();
        var samples = Enumerable.Range(0, 6).Select(i =>
            Sample(0xF405, i, i) with { Ecu = i % 2 == 0 ? "ECM" : "BCM", Channel = 0 }).ToArray();
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            samples, [Candidate(database)], SignalTimelineBuilder.Build(frames, [database]), NoLag));
        Assert.Equal(CorrelationVerdict.InsufficientData, result.Verdict);
        Assert.Contains("multiple ECUs", result.Explanation);
    }

    [Fact]
    public void MultiFieldDidSamplesCannotBeMergedIntoOneApparentlyConfirmedCorrelation()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 7).Select(i => Frame((byte)i, i)).ToArray();
        var mixed = Enumerable.Range(0, 7).SelectMany(i => new[]
        {
            Sample(0xF405, i, i) with { Field = "CoolingTemperature", Channel = 0 },
            Sample(0xF405, i, i) with { Field = "OilTemperature", Channel = 0 }
        }).ToArray();

        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            mixed, [Candidate(database)],
            SignalTimelineBuilder.Build(frames, [database]), NoLag));
        Assert.Equal(CorrelationVerdict.InsufficientData, result.Verdict);
        Assert.Equal(0, result.PairCount);
        Assert.Equal("Not established", result.ObservedField);
        Assert.Equal("No matching source-frame evidence.", result.SourceFrameEvidence);
        Assert.Equal(result.Explanation, result.EvidenceSummary);
        Assert.Contains("multiple fields", result.Explanation);
    }

    [Fact]
    public void WrongSourceEcuCannotBeConfirmedEvenIfDidNumberAndValuesMatch()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 8).Select(i => Frame((byte)i, i)).ToArray();
        var foreignEcu = Enumerable.Range(0, 8).Select(i =>
            Sample(0xF405, i, i) with { Ecu = "BodyController", Channel = 0 })
            .ToArray();

        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            foreignEcu, [Candidate(database)],
            SignalTimelineBuilder.Build(frames, [database]), NoLag));
        Assert.Equal(CorrelationVerdict.InsufficientData, result.Verdict);
        Assert.Equal(0, result.PairCount);
        Assert.Contains("does not match", result.Explanation);
    }

    [Fact]
    public void WideIntegerDbcTimeSeriesCannotBePromotedToConfirmedWithoutRawEvidence()
    {
        var database = Database("engine");
        var candidate = Candidate(database) with
        {
            Signal = Candidate(database).Signal with { BitLength = 64 }
        };
        var frames = Enumerable.Range(0, 8).Select(i => Frame((byte)i, i)).ToArray();
        var samples = Enumerable.Range(0, 8).Select(i => Sample(0xF405, i, i))
            .ToArray();

        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            samples, [candidate], SignalTimelineBuilder.Build(frames, [database]), NoLag));
        Assert.Equal(CorrelationVerdict.InsufficientData, result.Verdict);
        Assert.Contains("exact raw evidence", result.Explanation);
    }

    [Fact]
    public void IncompatiblePhysicalUnitsCannotBePromotedByCoincidentallyMatchingTrends()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 8).Select(i => Frame((byte)i, i)).ToArray();
        var wrongUnit = Enumerable.Range(0, 8).Select(i =>
            Sample(0xF405, i, i) with { Unit = "bar", Channel = 0 }).ToArray();

        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            wrongUnit, [Candidate(database)],
            SignalTimelineBuilder.Build(frames, [database]), NoLag));
        Assert.Equal(CorrelationVerdict.InsufficientData, result.Verdict);
        Assert.Equal(0, result.PairCount);
        Assert.Contains("incompatible physical units", result.Explanation);
    }

    [Fact]
    public void MixedUnitsInsideOneDidFieldCannotBeFittedAsOnePhysicalSeries()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 8).Select(i => Frame((byte)i, i)).ToArray();
        var mixed = Enumerable.Range(0, 8).Select(i =>
            Sample(0xF405, i, i) with
            {
                Unit = i % 2 == 0 ? "degC" : "bar",
                Channel = 0
            }).ToArray();
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            mixed, [Candidate(database)],
            SignalTimelineBuilder.Build(frames, [database]), NoLag));
        Assert.Equal(CorrelationVerdict.InsufficientData, result.Verdict);
        Assert.Contains("incompatible physical units", result.Explanation);
    }

    [Fact]
    public void EquivalentCatalogAndRecordedUnitAliasesAreNotRejected()
    {
        var database = new DbcParser().Parse("engine", new StringReader(
            """
            BO_ 256 EngineSpeed: 8 ECM
             SG_ EngSpeed : 0|8@1+ (1,0) [0|255] "rpm" ECM
            """));
        var candidate = Candidate(database) with
        {
            Did = Candidate(database).Did with { Unit = "1/min" }
        };
        var frames = Enumerable.Range(0, 8).Select(i => Frame((byte)i, i)).ToArray();
        var samples = Enumerable.Range(0, 8).Select(i =>
            Sample(0xF405, i, i) with { Unit = "r/min", Channel = 0 })
            .ToArray();
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            samples, [candidate],
            SignalTimelineBuilder.Build(frames, [database]), NoLag));
        Assert.Equal(8, result.PairCount);
        Assert.DoesNotContain("incompatible physical units", result.Explanation);
    }

    [Fact]
    public void NonfiniteDiagnosticSamplesDoNotEnterRegressionFitting()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 8).Select(i => Frame((byte)i, i)).ToArray();
        var samples = Enumerable.Range(0, 8).Select(i => Sample(0xF405, i, i))
            .Append(Sample(0xF405, double.NaN, 5))
            .Append(Sample(0xF405, double.PositiveInfinity, 6))
            .ToArray();

        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            samples, [Candidate(database)], SignalTimelineBuilder.Build(frames, [database]),
            NoLag));
        Assert.Equal(8, result.PairCount);
        Assert.All(result.Points, point =>
            Assert.True(double.IsFinite(point.DidValue) &&
                        double.IsFinite(point.SignalValue)));
    }

    [Fact]
    public void ExtremeSignedTimestampsDoNotOverflowNearestPairSearch()
    {
        var database = Database("engine");
        var frames = new[]
        {
            new CanFrame(0, 256, [5], long.MinValue + 5),
            new CanFrame(0, 256, [6], long.MinValue + 10)
        };
        var did = new[]
        {
            Sample(0xF405, 5, 0) with
            {
                TimestampNanoseconds = long.MaxValue - 10,
                Channel = 0
            },
            Sample(0xF405, 6, 1) with
            {
                TimestampNanoseconds = long.MaxValue - 5,
                Channel = 0
            }
        };
        var results = UdsCanTimeCorrelation.Correlate(did,
            [Candidate(database)],
            SignalTimelineBuilder.Build(frames, [database]), NoLag);
        var only = Assert.Single(results);
        Assert.Equal(CorrelationVerdict.InsufficientData, only.Verdict);
        Assert.Equal(0, only.PairCount);
    }

    [Fact]
    public void OverflowingTimestampPlusLagSkipsInvalidPairsInsteadOfCrashing()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 6).Select(i => new CanFrame(
            0, 256, [(byte)(i + 1)], long.MaxValue - 600_000_000L +
                i * 50_000_000L)).ToArray();
        var did = Enumerable.Range(0, 6).Select(i =>
            Sample(0xF405, i + 1, i) with
            {
                TimestampNanoseconds = long.MaxValue - 350_000_000L +
                    i * 50_000_000L,
                Channel = 0
            }).ToArray();
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [Candidate(database)],
            SignalTimelineBuilder.Build(frames, [database]),
            new TimeCorrelationOptions(MaxCandidates: 1,
                MaximumLagMilliseconds: 1000,
                LagStepMilliseconds: 250,
                MaximumPairDistanceMilliseconds: 300)));
        Assert.All(result.Points, point =>
            Assert.True(point.DidTimestampNanoseconds > 0 &&
                        point.SignalTimestampNanoseconds > 0));
    }

    [Fact]
    public void MalformedOrUnboundedLagPresetsAreSafelyRejected()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 7).Select(i => Frame((byte)i, i)).ToArray();
        var timelines = SignalTimelineBuilder.Build(frames, [database]);
        var did = Enumerable.Range(0, 7).Select(i => Sample(0xF405, i, i)).ToArray();
        var presets = new[]
        {
            new TimeCorrelationOptions(MaximumLagMilliseconds: double.NaN),
            new TimeCorrelationOptions(MaximumLagMilliseconds: double.PositiveInfinity),
            new TimeCorrelationOptions(LagStepMilliseconds: 0),
            new TimeCorrelationOptions(MaximumPairDistanceMilliseconds: -1),
            new TimeCorrelationOptions(MaximumLagMilliseconds: 60000,
                LagStepMilliseconds: 0.0001),
            new TimeCorrelationOptions(MaximumLagMilliseconds: 60001)
        };
        foreach (var preset in presets)
        {
            var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
                did, [Candidate(database)], timelines, preset));
            Assert.Equal(CorrelationVerdict.InsufficientData, result.Verdict);
            Assert.Equal(0, result.PairCount);
        }
    }

    [Fact]
    public void ContradictoryCanValuesWithIdenticalTimestampsCannotCreateFalsePairs()
    {
        var database = Database("engine");
        var frames = new[]
        {
            new CanFrame(0, 256, [10], 0),
            new CanFrame(0, 256, [200], 0),
            new CanFrame(0, 256, [20], 1_000_000_000),
            new CanFrame(0, 256, [30], 2_000_000_000)
        };
        var did = new[]
        {
            Sample(0xF405, 10, 0) with { Channel = 0 },
            Sample(0xF405, 20, 1) with { Channel = 0 },
            Sample(0xF405, 30, 2) with { Channel = 0 }
        };
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(did,
            [Candidate(database)], SignalTimelineBuilder.Build(frames, [database]),
            NoLag));
        Assert.Equal(2, result.PairCount);
        Assert.DoesNotContain(result.Points, point => point.DidTimestampNanoseconds == 0);
        Assert.Equal(CorrelationVerdict.InsufficientData, result.Verdict);
    }

    [Fact]
    public void IdenticalCanValuesWithIdenticalTimestampsCountAsOneTemporalObservation()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 7)
            .Select(index => Frame((byte)index, index)).ToList();
        frames.Insert(1, new CanFrame(0, 256, [0], 0));
        var did = Enumerable.Range(0, 7)
            .Select(index => Sample(0xF405, index, index) with { Channel = 0 })
            .ToArray();
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(did,
            [Candidate(database)], SignalTimelineBuilder.Build(frames, [database]),
            NoLag));
        Assert.Equal(7, result.PairCount);
        Assert.Equal(7, result.Points.Select(point => point.SignalFrameIndex).Distinct().Count());
        Assert.DoesNotContain(result.Points, point => point.SignalFrameIndex == 1);
    }

    [Fact]
    public void EquidistantDisagreeingCanFramesDoNotSelectAnArbitraryNearestValue()
    {
        var database = Database("engine");
        var frames = new[]
        {
            new CanFrame(0, 256, [10], 0),
            new CanFrame(0, 256, [20], 2_000_000_000)
        };
        var did = new[] { Sample(0xF405, 15, 0) with
        {
            TimestampNanoseconds = 1_000_000_000,
            Channel = 0
        } };
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(did,
            [Candidate(database)], SignalTimelineBuilder.Build(frames, [database]),
            new TimeCorrelationOptions(MaxCandidates: 1,
                MaximumLagMilliseconds: 0, LagStepMilliseconds: 25,
                MaximumPairDistanceMilliseconds: 1200)));
        Assert.Equal(0, result.PairCount);
        Assert.Equal(CorrelationVerdict.InsufficientData, result.Verdict);
    }

    [Fact]
    public void ExtremeSignedCaptureClocksCannotOverflowDistanceOrPairAcrossWraparound()
    {
        var database = Database("engine");
        var frames = new[]
        {
            new CanFrame(0, 256, [10], long.MinValue + 100),
            new CanFrame(0, 256, [20], long.MaxValue - 100)
        };
        var did = new[]
        {
            Sample(0xF405, 10, 0) with
            {
                TimestampNanoseconds = long.MinValue + 110,
                Channel = 0
            },
            Sample(0xF405, 20, 1) with
            {
                TimestampNanoseconds = long.MaxValue - 110,
                Channel = 0
            }
        };
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(did,
            [Candidate(database)], SignalTimelineBuilder.Build(frames, [database]),
            NoLag));
        Assert.Equal(2, result.PairCount);
        Assert.Equal(new[] { 0, 1 },
            result.Points.Select(point => point.SignalFrameIndex));
    }

    [Fact]
    public void InvalidAndExcessivelyDenseLagSearchOptionsFailClosed()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 6).Select(i => Frame((byte)i, i)).ToArray();
        var did = Enumerable.Range(0, 6).Select(i => Sample(0xF405, i, i)).ToArray();
        var timelines = SignalTimelineBuilder.Build(frames, [database]);
        var options = new[]
        {
            new TimeCorrelationOptions(MaximumLagMilliseconds: double.NaN),
            new TimeCorrelationOptions(MaximumPairDistanceMilliseconds: -1),
            new TimeCorrelationOptions(LagStepMilliseconds: 0),
            new TimeCorrelationOptions(MaximumLagMilliseconds: 60000,
                LagStepMilliseconds: 1),
            new TimeCorrelationOptions(MinimumPairsForCandidate: 0),
            new TimeCorrelationOptions(MaxCandidates: -1)
        };
        foreach (var option in options)
        {
            var outcome = Assert.Single(UdsCanTimeCorrelation.Correlate(
                did, [Candidate(database)], timelines, option));
            Assert.Equal(CorrelationVerdict.InsufficientData, outcome.Verdict);
            Assert.Equal(0, outcome.PairCount);
            Assert.NotEmpty(outcome.Explanation);
        }
    }

    [Fact]
    public void AContradictoryExactCanTimestampBlocksFallbackToNearbyUnrelatedReading()
    {
        var database = Database("engine");
        var frames = new[]
        {
            new CanFrame(0, 256, [10], 0),
            new CanFrame(0, 256, [200], 0),
            new CanFrame(0, 256, [20], 500_000),
            new CanFrame(0, 256, [30], 2_000_000_000),
            new CanFrame(0, 256, [40], 3_000_000_000)
        };
        var did = new[]
        {
            Sample(0xF405, 10, 0) with { Channel = 0 },
            Sample(0xF405, 30, 2) with { Channel = 0 },
            Sample(0xF405, 40, 3) with { Channel = 0 }
        };
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [Candidate(database)],
            SignalTimelineBuilder.Build(frames, [database]),
            NoLag));
        Assert.Equal(2, result.PairCount);
        Assert.DoesNotContain(result.Points, point =>
            point.DidTimestampNanoseconds == 0);
        Assert.DoesNotContain(result.Points, point =>
            point.SignalFrameIndex == 2); // +0.5 ms must not replace conflicted t=0.
    }

    [Fact]
    public void DuplicateDidValuesFromOneDiagnosticFrameDoNotCountAsIndependentEvidence()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 8)
            .Select(i => Frame((byte)i, i)).ToArray();
        var did = Enumerable.Range(0, 8)
            .Select(i => Sample(0xF405, i, i) with
            {
                FrameIndex = 12,
                Channel = 0
            }).ToArray();
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [Candidate(database)],
            SignalTimelineBuilder.Build(frames, [database]),
            NoLag));

        Assert.Equal(8, result.PairCount);
        Assert.Equal(CorrelationVerdict.InsufficientData, result.Verdict);
        Assert.Contains("insufficient independent DID source frames",
            result.Explanation);
        Assert.Contains("1 distinct DID source frames", result.Explanation);
    }

    [Fact]
    public void SubMillisecondLagGridFindsRecordedQuarterMillisecondOffset()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 7).Select(i =>
            new CanFrame(0, 256, [(byte)i], i * 1_000_000_000L + 250_000L))
            .ToArray();
        var did = Enumerable.Range(0, 7).Select(i =>
            Sample(0xF405, i, i) with { Channel = 0 })
            .ToArray();
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [Candidate(database)],
            SignalTimelineBuilder.Build(frames, [database]),
            new TimeCorrelationOptions(MaxCandidates: 1,
                MaximumLagMilliseconds: 0.30,
                LagStepMilliseconds: 0.05,
                MaximumPairDistanceMilliseconds: 0.01)));

        Assert.Equal(7, result.PairCount);
        Assert.Equal(0.25, result.LagMilliseconds, precision: 4);
        Assert.Equal(CorrelationVerdict.StrongCandidate, result.Verdict);
        Assert.True(result.StatisticalFitScore > result.AssociationConfidence);
        Assert.Equal(0.50, result.AssociationConfidence);
        Assert.Contains("unreviewed CAN identity", result.EvidenceSummary);
        Assert.Equal("Temperature", result.ObservedField);
        Assert.Contains("DID source frame indices: 0, 1, 2", result.SourceFrameEvidence);
        Assert.Contains("CAN source frame indices: 0, 1, 2", result.SourceFrameEvidence);
        Assert.Equal(7, result.Points.Select(point => point.SignalFrameIndex).Distinct().Count());
    }

    [Fact]
    public void PermissiveCorrelationPresetsCannotConfirmTwoPointRegression()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 2).Select(i =>
            Frame((byte)(10 + 5 * i), i)).ToArray();
        var did = Enumerable.Range(0, 2).Select(i =>
            Sample(0xF405, 10 + 5 * i, i) with { Channel = 0 }).ToArray();
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [Candidate(database)],
            SignalTimelineBuilder.Build(frames, [database]),
            new TimeCorrelationOptions(MaxCandidates: 1,
                MaximumLagMilliseconds: 0,
                MaximumPairDistanceMilliseconds: 1,
                MinimumPairsForCandidate: 1,
                MinimumPairsForConfirmation: 1)));

        Assert.Equal(CorrelationVerdict.InsufficientData, result.Verdict);
        Assert.Equal(2, result.PairCount);
        Assert.Equal(0d, result.ValueConfidence);
    }

    [Fact]
    public void FourPerfectPairsStillCannotBeAutomaticallyConfirmed()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 4).Select(i =>
            Frame((byte)(20 + 10 * i), i)).ToArray();
        var did = Enumerable.Range(0, 4).Select(i =>
            Sample(0xF405, 20 + 10 * i, i) with { Channel = 0 }).ToArray();
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [Candidate(database)],
            SignalTimelineBuilder.Build(frames, [database]),
            new TimeCorrelationOptions(MaxCandidates: 1,
                MaximumLagMilliseconds: 0,
                MaximumPairDistanceMilliseconds: 1,
                MinimumPairsForCandidate: 1,
                MinimumPairsForConfirmation: 1)));

        Assert.Equal(4, result.PairCount);
        Assert.NotEqual(CorrelationVerdict.StrongCandidate, result.Verdict);
        Assert.NotEqual(CorrelationVerdict.InsufficientData, result.Verdict);
    }

    [Fact]
    public void SameTimestampIdenticalDidReadingsCountOnlyOnceWithOriginalSourceProvenance()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 7).Select(i => Frame((byte)i, i)).ToArray();
        var did = Enumerable.Range(0, 7)
            .Select(i => Sample(0xF405, i, i) with { Channel = 0 })
            .Append(Sample(0xF405, 0, 99) with
            {
                TimestampNanoseconds = 0,
                Channel = 0
            }).ToArray();

        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [Candidate(database)],
            SignalTimelineBuilder.Build(frames, [database]), NoLag));
        Assert.Equal(7, result.PairCount);
        Assert.Equal(7, result.Points.Select(point => point.DidTimestampNanoseconds).Distinct().Count());
        Assert.DoesNotContain(result.Points, point => point.DidFrameIndex == 99);
        Assert.Equal(7d / 8d, result.Coverage, precision: 6);
    }

    [Fact]
    public void SimultaneousContradictoryDidValuesCannotBeFittedAsDistinctTimePoints()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 7).Select(i => Frame((byte)i, i)).ToArray();
        var did = Enumerable.Range(0, 7)
            .Select(i => Sample(0xF405, i, i) with { Channel = 0 })
            .Append(Sample(0xF405, 200, 99) with
            {
                TimestampNanoseconds = 0,
                Channel = 0
            }).ToArray();
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [Candidate(database)],
            SignalTimelineBuilder.Build(frames, [database]), NoLag));
        Assert.Equal(6, result.PairCount);
        Assert.DoesNotContain(result.Points, point => point.DidTimestampNanoseconds == 0);
        Assert.DoesNotContain(result.Points, point => point.DidFrameIndex == 99);
        Assert.Equal(6d / 8d, result.Coverage, precision: 6);
    }

    [Fact]
    public void CorrelationCarriesEveryOriginalIsoTpResponseSegmentForAudit()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 7)
            .Select(i => Frame((byte)i, i)).ToArray();
        var did = Enumerable.Range(0, 7)
            .Select(index => Sample(0xF405, index, index) with
            {
                Channel = 0,
                ResponseFrameIndices = new[] { 100 + index * 2, 101 + index * 2 }
            }).ToArray();

        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [Candidate(database)],
            SignalTimelineBuilder.Build(frames, [database]), NoLag));
        Assert.Equal(7, result.PairCount);
        Assert.Equal(new[] { 100, 101 }, result.Points[0].DidResponseFrameIndices);
        Assert.Contains("DID response transport frames (all ISO-TP segments)", 
            result.SourceFrameEvidence);
        Assert.Contains("100, 101", result.SourceFrameEvidence);
        Assert.Contains("CAN source frame indices: 0, 1, 2",
            result.SourceFrameEvidence);
    }

    [Fact]
    public void DenseDiagnosticPollingMatchesOriginalCanFrameIndicesWithSequentialCursor()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 80)
            .Select(i => Frame((byte)i, i)).ToArray();
        var did = Enumerable.Range(0, 70)
            .Select(i => Sample(0xF405, i, i) with { Channel = 0 })
            .ToArray();
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [Candidate(database)],
            SignalTimelineBuilder.Build(frames, [database]), NoLag));
        Assert.Equal(70, result.PairCount);
        Assert.Equal(Enumerable.Range(0, 70),
            result.Points.Select(point => point.SignalFrameIndex));
        Assert.Equal(Enumerable.Range(0, 70),
            result.Points.Select(point => point.DidFrameIndex));
    }

    [Fact]
    public void SparseDiagnosticPollingUsesBinarySearchOnLargeCanTimeline()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 2000)
            .Select(i => new CanFrame(0, 256,
                [(byte)(i % 251)], i * 1_000_000L))
            .ToArray();
        var selected = new[] { 100, 310, 540, 770, 1050, 1710 };
        var did = selected.Select(i => Sample(0xF405, i % 251, i) with
        {
            TimestampNanoseconds = i * 1_000_000L,
            Channel = 0
        }).ToArray();
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [Candidate(database)],
            SignalTimelineBuilder.Build(frames, [database]), NoLag));
        Assert.Equal(selected.Length, result.PairCount);
        Assert.Equal(selected,
            result.Points.Select(point => point.SignalFrameIndex));
        Assert.Equal(selected,
            result.Points.Select(point => point.DidFrameIndex));
    }

    [Fact]
    public void PeriodicSignalWithTwoPlausibleDistinctLagsCannotBeConfirmedUniquely()
    {
        var database = Database("engine");
        // A four-step waveform recurs every 400 ms. Both lag 0 and lag
        // +/-400 ms align with nearly identical goodness of fit on a long
        // recording; the trace alone cannot establish a unique delay.
        var frames = Enumerable.Range(0, 200).Select(i =>
            new CanFrame(0, 256, [(byte)(i % 4)], i * 100_000_000L))
            .ToArray();
        var did = Enumerable.Range(0, 200).Select(i =>
            Sample(0xF405, i % 4, i) with
            {
                TimestampNanoseconds = i * 100_000_000L,
                Channel = 0
            }).ToArray();
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [Candidate(database)], SignalTimelineBuilder.Build(frames, [database]),
            new TimeCorrelationOptions(MaxCandidates: 1, MaximumLagMilliseconds: 400,
                LagStepMilliseconds: 100, MaximumPairDistanceMilliseconds: 1)));

        Assert.Equal(200, result.PairCount);
        Assert.Equal(0, result.LagMilliseconds);
        Assert.Equal(CorrelationVerdict.Candidate, result.Verdict);
        Assert.True(result.AssociationConfidence <= 0.50);
        Assert.True(result.AmbiguousLag);
        Assert.Contains("multiple plausible lags", result.Explanation);
        Assert.Contains("Multiple plausible lags", result.EvidenceSummary);
        Assert.True(result.Confidence < 0.82);
    }

    [Fact]
    public void IrregularNonperiodicSignalCanEstablishUniqueStrongStatisticalCandidate()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 200).Select(i =>
            new CanFrame(0, 256,
                [(byte)((i * i + 7 * i) % 251)], i * 100_000_000L))
            .ToArray();
        var did = Enumerable.Range(0, 200).Select(i =>
            Sample(0xF405, (i * i + 7 * i) % 251, i) with
            {
                TimestampNanoseconds = i * 100_000_000L,
                Channel = 0
            }).ToArray();
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [Candidate(database)], SignalTimelineBuilder.Build(frames, [database]),
            new TimeCorrelationOptions(MaxCandidates: 1, MaximumLagMilliseconds: 400,
                LagStepMilliseconds: 100, MaximumPairDistanceMilliseconds: 1)));

        Assert.Equal(200, result.PairCount);
        Assert.Equal(CorrelationVerdict.StrongCandidate, result.Verdict);
        Assert.Equal(0.50, result.AssociationConfidence);
        Assert.True(result.StatisticalFitScore >= 0.82);
        Assert.False(result.AmbiguousLag);
        Assert.DoesNotContain("Multiple plausible lags", result.EvidenceSummary);
        Assert.DoesNotContain("multiple plausible lags", result.Explanation);
    }

    [Fact]
    public void DistinctCanSignalsWithTheSameRecordedTrendCannotBothBeConfirmed()
    {
        var database = new DbcParser().Parse("multiple-signals", new StringReader(
            """
            BO_ 256 FirstSensor: 8 ECM
             SG_ FirstReading : 0|8@1+ (1,0) [0|255] "degC" ECM
            BO_ 257 SecondSensor: 8 ECM
             SG_ SecondReading : 0|8@1+ (1,0) [0|255] "degC" ECM
            """));
        var frames = Enumerable.Range(0, 30).SelectMany(index => new[]
        {
            new CanFrame(0, 256, [(byte)index], index * 1_000_000_000L),
            new CanFrame(0, 257, [(byte)index], index * 1_000_000_000L)
        }).ToArray();
        var did = Enumerable.Range(0, 30)
            .Select(index => Sample(0xF405, index, index) with { Channel = 0 })
            .ToArray();
        var candidates = new[] { database.Messages[256], database.Messages[257] }
            .Select(message => new CorrelationCandidate(
                Candidate(database).Did, message, message.Signals[0],
                database.Name, 0.88, ["matching physical units"]))
            .ToArray();

        var results = UdsCanTimeCorrelation.Correlate(did, candidates,
            SignalTimelineBuilder.Build(frames, [database]),
            new TimeCorrelationOptions(MaxCandidates: 2,
                MaximumLagMilliseconds: 0, MaximumPairDistanceMilliseconds: 1));
        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(30, result.PairCount);
            Assert.Equal(CorrelationVerdict.Candidate, result.Verdict);
            Assert.Contains("multiple distinct CAN signals", result.Explanation);
            Assert.Contains("confirmation withheld", result.EvidenceSummary);
            Assert.Contains("Sensor.", result.Explanation);
            Assert.True(result.AmbiguousSignal);
            Assert.True(result.Confidence < 0.82);
        });
        Assert.NotEqual(results[0].Candidate.Message.Identifier,
            results[1].Candidate.Message.Identifier);
    }

    [Fact]
    public void WeakerUnrelatedCanSignalDoesNotBlockStrongStatisticalCandidate()
    {
        var database = new DbcParser().Parse("strong-and-weak", new StringReader(
            """
            BO_ 256 ActualSensor: 8 ECM
             SG_ ActualValue : 0|8@1+ (1,0) [0|255] "degC" ECM
            BO_ 257 OtherSensor: 8 ECM
             SG_ OtherValue : 0|8@1+ (1,0) [0|255] "degC" ECM
            """));
        var frames = Enumerable.Range(0, 30).SelectMany(index => new[]
        {
            new CanFrame(0, 256, [(byte)index], index * 1_000_000_000L),
            new CanFrame(0, 257,
                [(byte)((index * index + 7 * index) % 251)],
                index * 1_000_000_000L)
        }).ToArray();
        var did = Enumerable.Range(0, 30)
            .Select(index => Sample(0xF405, index, index) with { Channel = 0 })
            .ToArray();
        var candidates = new[] { database.Messages[256], database.Messages[257] }
            .Select(message => new CorrelationCandidate(
                Candidate(database).Did, message, message.Signals[0],
                database.Name, 0.88, ["matching physical units"]))
            .ToArray();
        var results = UdsCanTimeCorrelation.Correlate(did, candidates,
            SignalTimelineBuilder.Build(frames, [database]),
            new TimeCorrelationOptions(MaxCandidates: 2,
                MaximumLagMilliseconds: 0, MaximumPairDistanceMilliseconds: 1));
        var actual = Assert.Single(results.Where(result =>
            result.Candidate.Message.Identifier == 256));
        Assert.Equal(CorrelationVerdict.StrongCandidate, actual.Verdict);
        Assert.False(actual.AmbiguousSignal);
        Assert.DoesNotContain("multiple distinct CAN signals", actual.Explanation);
    }

    [Fact]
    public void NonDivisibleLagStepAlwaysTestsZeroAsAnIndependentHypothesis()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 8)
            .Select(index => Frame((byte)index, index)).ToArray();
        var did = Enumerable.Range(0, 8)
            .Select(index => Sample(0xF405, index, index) with { Channel = 0 })
            .ToArray();
        // The previous -0.3, -0.1, +0.1, +0.3 ms grid skipped 0,
        // even though every sample is perfectly simultaneous.
        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [Candidate(database)],
            SignalTimelineBuilder.Build(frames, [database]),
            new TimeCorrelationOptions(MaxCandidates: 1,
                MaximumLagMilliseconds: 0.3,
                LagStepMilliseconds: 0.2,
                MaximumPairDistanceMilliseconds: 0.01)));

        Assert.Equal(8, result.PairCount);
        Assert.Equal(0d, result.LagMilliseconds, precision: 5);
        Assert.Equal(CorrelationVerdict.StrongCandidate, result.Verdict);
    }

    [Fact]
    public void NonDivisibleLagGridStillChecksPositiveAndNegativeIntervalBoundaries()
    {
        var database = Database("engine");
        var did = Enumerable.Range(0, 8)
            .Select(index => Sample(0xF405, index, index) with { Channel = 0 })
            .ToArray();
        foreach (var sign in new[] { -1, 1 })
        {
            var frames = Enumerable.Range(0, 8).Select(index =>
                new CanFrame(0, 256, [(byte)index],
                    index * 1_000_000_000L + sign * 300_000L)).ToArray();
            var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
                did, [Candidate(database)],
                SignalTimelineBuilder.Build(frames, [database]),
                new TimeCorrelationOptions(MaxCandidates: 1,
                    MaximumLagMilliseconds: 0.3,
                    LagStepMilliseconds: 0.2,
                    MaximumPairDistanceMilliseconds: 0.01)));

            Assert.Equal(8, result.PairCount);
            Assert.Equal(sign * 0.3d, result.LagMilliseconds, precision: 5);
            Assert.Equal(CorrelationVerdict.StrongCandidate, result.Verdict);
        }
    }

    [Fact]
    public void ExplicitlyUnknownDiagnosticEndpointCannotBecomeStrongCandidateFromMatchingNumbers()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 10)
            .Select(index => Frame((byte)index, index)).ToArray();
        var did = Enumerable.Range(0, 10)
            .Select(index => Sample(0xF405, index, index) with
            {
                Channel = 0,
                Quality = "Unknown endpoints"
            }).ToArray();

        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [Candidate(database)],
            SignalTimelineBuilder.Build(frames, [database]), NoLag));
        Assert.Equal(CorrelationVerdict.InsufficientData, result.Verdict);
        Assert.Equal(0, result.PairCount);
        Assert.Contains("unknown diagnostic endpoint", result.Explanation);
    }

    [Fact]
    public void MalformedDiagnosticTransportCannotYieldConfirmedDidToCanMapping()
    {
        var database = Database("engine");
        var frames = Enumerable.Range(0, 10)
            .Select(index => Frame((byte)index, index)).ToArray();
        var did = Enumerable.Range(0, 10)
            .Select(index => Sample(0xF405, index, index) with
            {
                Channel = 0,
                Quality = "Malformed · incomplete ISO-TP reassembly"
            }).ToArray();

        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            did, [Candidate(database)],
            SignalTimelineBuilder.Build(frames, [database]), NoLag));
        Assert.Equal(CorrelationVerdict.InsufficientData, result.Verdict);
        Assert.Contains("malformed transport", result.Explanation);
    }

    [Fact]
    public void CollidingCanIdsUseTheCandidateDatabaseSignalDefinition()
    {
        var first = Database("first", factor: 1);
        var second = Database("second", factor: 2);
        var frames = Enumerable.Range(0, 7).Select(i => Frame((byte)i, i)).ToArray();
        var timelines = SignalTimelineBuilder.Build(frames, [first, second]);
        var samples = Enumerable.Range(0, 7).Select(i => Sample(0xF405, 2 * i, i)).ToArray();

        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(
            samples, [Candidate(second)], timelines, NoLag));

        Assert.Equal(7, result.PairCount);
        Assert.Equal(2d, result.Points[1].SignalValue);
    }
}
