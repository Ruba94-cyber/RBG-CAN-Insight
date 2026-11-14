using RbgCanInsight.Analysis;
using RbgCanInsight.Correlation;
using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DiagnosticInsightTests
{

    [Fact]
    public void DidSamplesRetainOriginalLoggerAndDoNotAcceptForeignResponseSegments()
    {
        static DiagnosticTransaction Observed(int sourceFrame, double value)
        {
            var response = new IsoTpMessage(0, 0x7E8, false,
                sourceFrame * 1_000_000L, sourceFrame * 1_000_000L,
                [sourceFrame], [0x62, 0xF4, 0x05, (byte)value], true, "");
            var decoded = new DecodedUdsOperation(
                0x22, "ReadDataByIdentifier", "0xF405", "", "",
                [new DecodedDiagnosticValue("DID 0xF405/Value", "Value",
                    $"0x{(int)value:X2}", value.ToString(), "degC", "", 0xF405, value)],
                [], null, "");
            return new DiagnosticTransaction(sourceFrame, null, [response],
                new DiagnosticEndpoint("Tester", "ECM",
                    DiagnosticDirection.EcuToTester,
                    DiagnosticEvidence.Confirmed, ""),
                DiagnosticOutcome.Positive, decoded,
                response.StartTimestampNanoseconds, response.EndTimestampNanoseconds,
                0, [sourceFrame], "Recorded");
        }
        var samples = DiagnosticInsightAnalyzer.Analyze(
            [Observed(0, 10), Observed(1, 90)], [],
            sourceAt: sourceFrame => sourceFrame).DidSamples;
        Assert.Equal(2, samples.Count);
        Assert.Equal(new[] { 0, 1 },
            samples.Select(sample => sample.OriginalSourceIndex).ToArray());
        Assert.Equal(new[] { 10d, 90d },
            samples.Select(sample => sample.NumericValue!.Value).ToArray());

        var contaminated = Observed(0, 10);
        var response = contaminated.Responses[0] with
        {
            FrameIndices = [0, 1]
        };
        Assert.Throws<InvalidOperationException>(() =>
            DiagnosticInsightAnalyzer.Analyze(
                [contaminated with { Responses = [response] }], [],
                sourceAt: frame => frame));
    }

    [Fact]
    public void DidSamplesRetainAllPositiveIsoTpResponseFramesNotOnlyTheFinalFrame()
    {
        var request = new IsoTpMessage(0, 0x7E0, false, 0,
            0, [2], [0x22, 0xF4, 0x05], true, "");
        var response = new IsoTpMessage(0, 0x7E8, false, 1_000_000,
            2_000_000, [8, 9],
            [0x62, 0xF4, 0x05, 0x2A], true, "");
        var decoded = new DecodedUdsOperation(
            0x22, "ReadDataByIdentifier", "0xF405", "", "",
            [new DecodedDiagnosticValue("DID 0xF405/Value", "Value",
                "0x2A", "42", "degC", "", 0xF405, 42)], [],
            null, "");
        var transaction = new DiagnosticTransaction(1, request, [response],
            new DiagnosticEndpoint("Tester", "ECM",
                DiagnosticDirection.EcuToTester, DiagnosticEvidence.Confirmed, ""),
            // Index 99 simulates later flow-control/aggregate evidence.
            // It is NOT part of the actual positive reply PDU.
            DiagnosticOutcome.Positive, decoded, 0, 2_000_000, 2, [2, 8, 9, 99],
            "Recorded");

        var insight = DiagnosticInsightAnalyzer.Analyze([transaction], []);
        var sample = Assert.Single(insight.DidSamples);
        Assert.Equal(9, sample.FrameIndex);
        Assert.Equal(new[] { 8, 9 }, sample.ResponseFrames);
        Assert.DoesNotContain(2, sample.ResponseFrames); // Tester request is not DID data.
        Assert.DoesNotContain(99, sample.ResponseFrames); // Nor later flow control.
        Assert.NotEqual(transaction.FrameIndices.Last(), sample.FrameIndex);
        Assert.Equal(0, sample.Channel);
        Assert.Equal(42d, sample.NumericValue);
    }

    [Fact]
    public void IncompleteOrUnattributedPositiveReplyCannotProduceDecodedDidSamples()
    {
        var response = new IsoTpMessage(0, 0x7E8, false, 10, 20,
            [5, 6], [0x62, 0xF4, 0x05, 0x2A], false,
            "truncated consecutive frames");
        var decoded = new DecodedUdsOperation(
            0x22, "ReadDataByIdentifier", "0xF405", "", "",
            [new DecodedDiagnosticValue("DID 0xF405/Value", "Value",
                "0x2A", "42", "degC", "", 0xF405, 42)], [],
            null, "");
        var transaction = new DiagnosticTransaction(1, null, [response],
            new DiagnosticEndpoint("Tester", "ECM",
                DiagnosticDirection.EcuToTester, DiagnosticEvidence.Confirmed, ""),
            DiagnosticOutcome.Positive, decoded, 10, 20, null, [5, 6],
            "Malformed · truncated consecutive frames");

        Assert.Empty(DiagnosticInsightAnalyzer.Analyze([transaction], []).DidSamples);
        // A complete but non-attributed reply (zero original frame indices)
        // is also insufficient source evidence for a source-frame mapping.
        var missingSource = transaction with
        {
            Responses = [response with { IsComplete = true, FrameIndices = [] }],
            Quality = "Recorded"
        };
        Assert.Empty(DiagnosticInsightAnalyzer.Analyze([missingSource], []).DidSamples);
    }

    [Fact]
    public void ReconstructsFaultLifecycleIncludingClear()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x03, 0x19, 0x02, 0xFF, 0, 0, 0, 0], 0),
            Frame(0x7E8, [0x07, 0x59, 0x02, 0xFF, 0x12, 0x34, 0x56, 0x0D], 1_000_000),
            Frame(0x7E0, [0x04, 0x14, 0xFF, 0xFF, 0xFF, 0, 0, 0], 2_000_000),
            Frame(0x7E8, [0x01, 0x54, 0, 0, 0, 0, 0, 0], 3_000_000)
        };
        var definition = new DiagnosticTroubleCode(0x123456, "SensorFault", "Sensor fault", "Plausibility", "ECU", "fixture");
        // ECU-specific names are attached only when the catalog confirms the CAN
        // addressing (f3d3bf0); without a connection the ECU is merely "ECU 0".
        var connection = new DiagnosticConnection("CAN", 0x7E0, 0x7E8, false, "physical", "ECU", "fixture");
        var catalog = new DidCatalog("diag", "ECU", "fixture", [], [definition], Connections: [connection]);

        var insight = DiagnosticInsightAnalyzer.Analyze(UdsTransactionAnalyzer.Analyze(frames, [catalog]), [catalog]);

        var fault = Assert.Single(insight.Faults);
        Assert.Equal("Sensor fault", fault.Name);
        Assert.True(fault.WasCleared);
        Assert.Contains(fault.Timeline, transition => transition.Operation == "ClearDiagnosticInformation");
    }


    [Fact]
    public void RoutineStartAndResultsFromDifferentLoggersCannotJoin()
    {
        static DiagnosticTransaction Routine(int frame, int action, int channel)
        {
            var request = new IsoTpMessage(channel, 0x7E0, false,
                frame, frame, [frame], [0x31, (byte)action, 0x02, 0x03],
                true, "");
            var operation = new DecodedUdsOperation(
                0x31, "RoutineControl", "0x0203", "", "", [], [], null, "");
            return new DiagnosticTransaction(frame + 1, request, [],
                new DiagnosticEndpoint("Tester", "ECU",
                    DiagnosticDirection.EcuToTester, DiagnosticEvidence.Confirmed,
                    "physical"),
                DiagnosticOutcome.Positive, operation, frame, frame, 0,
                [frame], "Recorded");
        }
        var catalog = new DidCatalog("diag", "ECU", "fixture.odx",
            [], Routines: [new DiagnosticRoutine(0x0203, "Calibration",
                "Calibration", "", "ECU", "fixture.odx")]);
        var start = Routine(0, 1, 0);
        var foreignResults = Routine(1, 3, 0);
        var insight = DiagnosticInsightAnalyzer.Analyze(
            [start, foreignResults], [catalog], sourceAt: frame => frame);
        Assert.Equal(2, insight.Routines.Count);
        var first = Assert.Single(insight.Routines,
            item => item.OriginalSourceIndex == 0);
        var second = Assert.Single(insight.Routines,
            item => item.OriginalSourceIndex == 1);
        Assert.Single(first.Steps);
        Assert.Single(second.Steps);
        Assert.All(first.FrameIndices, frame => Assert.Equal(0, frame));
        Assert.All(second.FrameIndices, frame => Assert.Equal(1, frame));
        Assert.Equal("fixture.odx", first.DefinitionSourceFile);
    }

    [Fact]
    public void RoutineWithForeignResponseFrameIsRejected()
    {
        var request = new IsoTpMessage(0, 0x7E0, false, 0, 0,
            [0], [0x31, 0x01, 0x02, 0x03], true, "");
        var foreignReply = new IsoTpMessage(0, 0x7E8, false, 1, 1,
            [1], [0x71, 0x01, 0x02, 0x03], true, "");
        var transaction = new DiagnosticTransaction(1, request, [foreignReply],
            new DiagnosticEndpoint("Tester", "ECU",
                DiagnosticDirection.EcuToTester, DiagnosticEvidence.Confirmed, ""),
            DiagnosticOutcome.Positive,
            new DecodedUdsOperation(0x31, "RoutineControl", "0x0203", "", "",
                [], [], null, ""),
            0, 1, 1, [0, 1], "Recorded");
        Assert.Throws<InvalidOperationException>(() =>
            DiagnosticInsightAnalyzer.Analyze([transaction], [],
                sourceAt: i => i));
    }

    [Fact]
    public void ReconstructsRoutineStepsAndResponsePending()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x05, 0x31, 0x01, 0x02, 0x03, 0xAA, 0, 0], 0),
            Frame(0x7E8, [0x03, 0x7F, 0x31, 0x78, 0, 0, 0, 0], 1_000_000),
            Frame(0x7E8, [0x05, 0x71, 0x01, 0x02, 0x03, 0x00, 0, 0], 2_000_000),
            Frame(0x7E0, [0x04, 0x31, 0x03, 0x02, 0x03, 0, 0, 0], 3_000_000),
            Frame(0x7E8, [0x05, 0x71, 0x03, 0x02, 0x03, 0x01, 0, 0], 4_000_000)
        };
        var routine = new DiagnosticRoutine(0x0203, "Calibration", "Sensor calibration", "", "ECU", "fixture");
        // ECU-specific names are attached only when the catalog confirms the CAN
        // addressing (f3d3bf0); without a connection the ECU is merely "ECU 0".
        var connection = new DiagnosticConnection("CAN", 0x7E0, 0x7E8, false, "physical", "ECU", "fixture");
        var catalog = new DidCatalog("diag", "ECU", "fixture", [], Routines: [routine], Connections: [connection]);

        var insight = DiagnosticInsightAnalyzer.Analyze(UdsTransactionAnalyzer.Analyze(frames, [catalog]), [catalog]);

        var execution = Assert.Single(insight.Routines);
        Assert.Equal("Sensor calibration", execution.Name);
        Assert.Equal(RoutineExecutionState.Completed, execution.State);
        Assert.Equal(1, execution.ResponsePendingCount);
        Assert.Equal(2, execution.Steps.Count);
    }

    [Fact]
    public void TimeCorrelationConfirmsAlignedPhysicalValuesAndFindsLag()
    {
        var database = new DbcParser().Parse("vehicle", new StringReader(
            "BO_ 256 Vehicle: 8 ECU\n SG_ VehicleSpeed : 0|8@1+ (1,0) [0|255] \"km/h\" Vector__XXX\n"));
        var frames = Enumerable.Range(0, 8)
            .Select(index => Frame(0x100, [(byte)(10 + index * 5), 0, 0, 0, 0, 0, 0, 0], index * 1_000_000_000L))
            .ToArray();
        var timelines = SignalTimelineBuilder.Build(frames, [database]);
        var did = new DidParameter(0x1234, "VehicleSpeed", "Vehicle speed", "", "km/h", 0, 255, 1, 0,
            "A_UINT8", 8, "ECU", "fixture");
        var samples = Enumerable.Range(0, 8).Select(index => new DiagnosticDidSample(
            0x1234, "Vehicle speed", "DID 0x1234", 10 + index * 5, (10 + index * 5).ToString(), "km/h",
            index * 1_000_000_000L + 100_000_000L, index, "ECU", "Confirmed")).ToArray();
        var candidates = DidSignalCorrelator.Correlate(did, [database]);

        var result = Assert.Single(UdsCanTimeCorrelation.Correlate(samples, candidates, timelines));

        Assert.Equal(CorrelationVerdict.StrongCandidate, result.Verdict); // temporal correlation is never causal proof
        Assert.True(result.Confidence >= 0.82);
        Assert.InRange(result.LagMilliseconds, -125, -75);
        Assert.True(result.RSquared > 0.99);
    }

    private static CanFrame Frame(uint id, byte[] data, long timestamp) =>
        new(0, id, data, timestamp, id > 0x7FF ? CanFrameFlags.Extended : CanFrameFlags.None);
}
