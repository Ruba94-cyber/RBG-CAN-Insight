using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>
/// Regression tests for the DBC parser hardening (pseudo-messages, duplicate IDs, malformed
/// multi-line statements) and for the decoder matching options (J1939 PGN, channel assignment).
/// </summary>
public sealed class DbcHardeningTests
{
    [Fact]
    public void VectorIndependentSignalsPseudoMessageIsSkippedWithWarning()
    {
        // 3221225472 = 0xC0000000: after 29-bit masking it would collide with a real ID 0.
        var database = Parse(
            """
            BO_ 3221225472 VECTOR__INDEPENDENT_SIG_MSG: 8 Vector__XXX
             SG_ OrphanSignal : 0|8@1+ (1,0) [0|255] "" Vector__XXX

            BO_ 0 RealZeroMessage: 8 ECM
             SG_ RealSignal : 0|8@1+ (1,0) [0|255] "" Vector__XXX
            """);

        var message = Assert.Single(database.Messages.Values);
        Assert.Equal("RealZeroMessage", message.Name);
        Assert.Equal("RealSignal", Assert.Single(message.Signals).Name);
        Assert.Contains(database.ParseWarnings, warning => warning.Contains("VECTOR__INDEPENDENT_SIG_MSG"));
    }

    [Fact]
    public void DuplicateMessageIdentifierIsReportedAsWarning()
    {
        var database = Parse(
            """
            BO_ 256 FirstMessage: 8 ECM
             SG_ SignalA : 0|8@1+ (1,0) [0|255] "" Vector__XXX

            BO_ 256 SecondMessage: 8 BCM
             SG_ SignalB : 0|8@1+ (1,0) [0|255] "" Vector__XXX
            """);

        Assert.Single(database.Messages);
        Assert.Contains(database.ParseWarnings, warning => warning.Contains("duplicate message ID"));
    }

    [Fact]
    public void StandardAndExtendedSameNumericIdStaySeparateInDbcAndDecoder()
    {
        const string dbc =
            """
            BO_ 256 StandardEngine: 8 ECM
             SG_ State : 0|8@1+ (1,0) [0|255] "" ECM

            BO_ 2147483904 ExtendedBody: 8 BCM
             SG_ State : 0|8@1+ (2,10) [10|520] "" BCM

            CM_ SG_ 256 State "Engine state";
            CM_ SG_ 2147483904 State "Body state";
            VAL_ 256 State 1 "Engine enabled";
            VAL_ 2147483904 State 1 "Body unlocked";
            BA_ "GenMsgCycleTime" BO_ 256 10;
            BA_ "GenMsgCycleTime" BO_ 2147483904 100;
            """;
        var database = Parse(dbc);
        Assert.Equal(2, database.MessageDefinitionCount);
        Assert.Equal(2, database.AllSignals.Count());
        Assert.True(database.TryGetMessage(256, false, out var standard));
        Assert.True(database.TryGetMessage(256, true, out var extended));
        Assert.NotSame(standard, extended);
        Assert.Equal("StandardEngine", standard.Name);
        Assert.Equal("ExtendedBody", extended.Name);
        Assert.Equal(10, standard.NominalCycleTimeMilliseconds);
        Assert.Equal(100, extended.NominalCycleTimeMilliseconds);
        Assert.Equal("Engine enabled", Assert.Single(standard.Signals).GetValueDescription(1));
        Assert.Equal("Body unlocked", Assert.Single(extended.Signals).GetValueDescription(1));
        Assert.Contains("Engine state", standard.Signals[0].Comment);
        Assert.Contains("Body state", extended.Signals[0].Comment);

        var decoder = new DbcDecoder(database);
        var stdFrame = new CanFrame(0, 256, [1, 0, 0, 0, 0, 0, 0, 0], 0);
        var extFrame = new CanFrame(0, 256, [1, 0, 0, 0, 0, 0, 0, 0], 0, CanFrameFlags.Extended);
        Assert.Equal(1d, Assert.Single(decoder.Decode(stdFrame)).PhysicalValue);
        Assert.Equal(12d, Assert.Single(decoder.Decode(extFrame)).PhysicalValue);
    }

    [Fact]
    public void PreservesTypedAndVendorSpecificDbcAttributesWithoutGuessingTheirMeaning()
    {
        var database = Parse(
            """
            BA_DEF_ BO_ "GenMsgCycleTime" INT 0 60000;
            BA_DEF_ SG_ "GenSigStartValue" INT 0 255;
            BO_ 256 Drive: 8 ECM
             SG_ Mode : 0|8@1+ (1,0) [0|255] "" ECM
            BA_ "GenMsgCycleTime" BO_ 256 100;
            BA_ "GenMsgSendType" BO_ 256 "cyclic; event";
            BA_ "GenSigStartValue" SG_ 256 Mode 2;
            BA_ "VendorStatus" SG_ 256 Mode "State; ECU-defined";
            """);
        var message = Assert.Single(database.AllMessageDefinitions);
        Assert.Equal(100, message.NominalCycleTimeMilliseconds);
        Assert.Equal(2, message.SourceAttributes.Count);
        Assert.Equal("\"cyclic; event\"",
            message.SourceAttributes.Single(item => item.Name == "GenMsgSendType").RawValue);
        var signal = Assert.Single(message.Signals);
        Assert.Equal("2", signal.SourceAttributes.Single(item => item.Name == "GenSigStartValue").RawValue);
        Assert.Equal("\"State; ECU-defined\"",
            signal.SourceAttributes.Single(item => item.Name == "VendorStatus").RawValue);
        Assert.Equal(2, database.RawAttributeDeclarations.Count);
    }

    [Fact]
    public void ExtendedMuxRangesSupportMultipleAlternativeValues()
    {
        var database = Parse(
            """
            BO_ 256 Multiplexed: 8 ECM
             SG_ Switch M : 0|8@1+ (1,0) [0|255] "" ECM
             SG_ Basic m2 : 8|8@1+ (1,0) [0|255] "" ECM
             SG_ Extended m0 : 16|8@1+ (1,0) [0|255] "" ECM
            SG_MUL_VAL_ 256 Extended Switch 10-12,15-15;
            """);
        var message = database.Messages[256];
        var ext = message.Signals.Single(signal => signal.Name == "Extended");
        var condition = Assert.Single(ext.MultiplexConditions!);
        Assert.Equal("Switch", condition.Multiplexor);
        Assert.Equal(2, condition.Ranges.Count);
        Assert.Equal(new[] { 10L, 12L }, new[] {
            condition.Ranges[0].Minimum, condition.Ranges[0].Maximum
        });
        var decoder = new DbcDecoder(database);
        CanFrame Frame(byte sw) => new(0, 256, [sw, 22, 33, 0, 0, 0, 0, 0], 0);
        Assert.DoesNotContain(decoder.Decode(Frame(9)), value => value.Signal == "Extended");
        Assert.Contains(decoder.Decode(Frame(10)), value => value.Signal == "Extended" && value.RawValue == 33);
        Assert.Contains(decoder.Decode(Frame(12)), value => value.Signal == "Extended");
        Assert.DoesNotContain(decoder.Decode(Frame(13)), value => value.Signal == "Extended");
        Assert.Contains(decoder.Decode(Frame(15)), value => value.Signal == "Extended");
        Assert.Contains(decoder.Decode(Frame(2)), value => value.Signal == "Basic" && value.RawValue == 22);
    }

    [Fact]
    public void NestedMuxDoesNotDecodeInactiveBranchesAsRealMeasurements()
    {
        var database = Parse(
            """
            BO_ 256 Nested: 8 ECM
             SG_ Root M : 0|8@1+ (1,0) [0|255] "" ECM
             SG_ Child m1M : 8|8@1+ (1,0) [0|255] "" ECM
             SG_ NestedData m0 : 16|8@1+ (1,0) [0|255] "" ECM
             SG_ RootOnly m0 : 24|8@1+ (1,0) [0|255] "" ECM
            SG_MUL_VAL_ 256 NestedData Child 2-4;
            SG_MUL_VAL_ 256 RootOnly Root 9-9;
            """);
        var decoder = new DbcDecoder(database);
        CanFrame Frame(byte root, byte child) => new(0, 256, [root, child, 42, 99, 0, 0, 0, 0], 0);
        Assert.Contains(decoder.Decode(Frame(1, 3)), value => value.Signal == "Child");
        Assert.Contains(decoder.Decode(Frame(1, 3)), value => value.Signal == "NestedData");
        Assert.DoesNotContain(decoder.Decode(Frame(0, 3)), value => value.Signal == "Child");
        Assert.DoesNotContain(decoder.Decode(Frame(0, 3)), value => value.Signal == "NestedData");
        Assert.DoesNotContain(decoder.Decode(Frame(1, 9)), value => value.Signal == "NestedData");
        Assert.Contains(decoder.Decode(Frame(9, 3)), value => value.Signal == "RootOnly");
    }

    [Fact]
    public void ReusedMessageObjectInSeveralIndexesDoesNotCrashMuxPlanCompilation()
    {
        var selector = new DbcSignal("Selector", 0, 8,
            DbcByteOrder.Intel, false, 1, 0, 0, 255, "", "ECM",
            IsMultiplexor: true);
        var branch = new DbcSignal("Branch", 8, 8,
            DbcByteOrder.Intel, false, 1, 0, 0, 255, "", "ECM",
            MultiplexValue: 2);
        var shared = new DbcMessage(0x100, "Shared", 2, "ECM",
            false, [selector, branch]);
        var database = new DbcDatabase("shared",
            new Dictionary<uint, DbcMessage>
            {
                [0x100] = shared,
                [0x101] = shared
            });
        var decoder = new DbcDecoder(database);
        var samples = decoder.Decode(new CanFrame(0, 0x100,
            new byte[] { 2, 44 }, 0));
        Assert.Contains(samples, sample =>
            sample.Signal == "Branch" && sample.RawValue == 44);
    }

    [Fact]
    public void PrecompiledMuxPlanPreservesOrWithinControllerAndAcrossControllers()
    {
        var first = new DbcSignal("SwitchA", 0, 8,
            DbcByteOrder.Intel, false, 1, 0, 0, 255, "", "ECM",
            IsMultiplexor: true);
        var second = first with { Name = "SwitchB", StartBit = 8 };
        var conditional = new DbcSignal("ConditionalData", 16, 8,
            DbcByteOrder.Intel, false, 1, 0, 0, 255, "", "ECM",
            MultiplexConditions:
            [
                new DbcMultiplexCondition("SwitchA", [new DbcMuxRange(1, 1)]),
                new DbcMultiplexCondition("SwitchA", [new DbcMuxRange(3, 3)]),
                new DbcMultiplexCondition("SwitchB", [new DbcMuxRange(2, 2)])
            ]);
        var message = new DbcMessage(0x100, "MultiController", 3,
            "ECM", false, [first, second, conditional]);
        var decoder = new DbcDecoder(new DbcDatabase("compiled",
            new Dictionary<uint, DbcMessage> { [0x100] = message }));

        // Several frames through the same immutable plan: OR(1,3) on A,
        // AND(2) on B. Neither selector can be ignored during caching.
        for (var index = 0; index < 1000; index++)
        {
            var a = index % 4;
            var b = index % 3;
            var frame = new CanFrame(0, 0x100,
                new byte[] { (byte)a, (byte)b, 44 }, index);
            var values = decoder.Decode(frame);
            var expected = (a == 1 || a == 3) && b == 2;
            Assert.Equal(expected, values.Any(value =>
                value.Signal == "ConditionalData" && value.RawValue == 44));
            Assert.Equal(2 + (expected ? 1 : 0), values.Count);
        }
    }

    [Fact]
    public void SignedMultiplexorUsesActualSignedCodeForNegativeRanges()
    {
        var selector = new DbcSignal("SignedSwitch", 0, 8,
            DbcByteOrder.Intel, true, 1, 0, -128, 127, "", "ECM",
            IsMultiplexor: true);
        var negativeBranch = new DbcSignal("NegativeMode", 8, 8,
            DbcByteOrder.Intel, false, 1, 0, 0, 255, "", "ECM",
            MultiplexConditions:
            [
                new DbcMultiplexCondition("SignedSwitch",
                    [new DbcMuxRange(-2, -1)])
            ]);
        var positiveBranch = negativeBranch with
        {
            Name = "PositiveMode",
            MultiplexConditions =
            [
                new DbcMultiplexCondition("SignedSwitch",
                    [new DbcMuxRange(1, 2)])
            ]
        };
        var message = new DbcMessage(0x100, "SignedMux", 2, "ECM", false,
            [selector, negativeBranch, positiveBranch]);
        var decoder = new DbcDecoder(new DbcDatabase("signed",
            new Dictionary<uint, DbcMessage> { [0x100] = message }));
        var negative = decoder.Decode(new CanFrame(0, 0x100,
            new byte[] { 0xFF, 77 }, 0));
        Assert.Contains(negative, sample =>
            sample.Signal == "NegativeMode" && sample.RawValue == 77);
        Assert.DoesNotContain(negative, sample => sample.Signal == "PositiveMode");
        var positive = decoder.Decode(new CanFrame(0, 0x100,
            new byte[] { 0x02, 88 }, 1));
        Assert.Contains(positive, sample =>
            sample.Signal == "PositiveMode" && sample.RawValue == 88);
        Assert.DoesNotContain(positive, sample => sample.Signal == "NegativeMode");
        var validation = DbcEngineeringValidator.Validate(message);
        Assert.DoesNotContain(validation, issue => issue.Code == "MUX_OVERLAP_REVIEW");
    }

    [Fact]
    public void FloatingPointMuxBitPatternMustNotActivateIntegerBranch()
    {
        var floatSwitch = new DbcSignal("FloatSwitch", 0, 32,
            DbcByteOrder.Intel, false, 1, 0, 0, 100, "", "ECM",
            IsMultiplexor: true, ValueType: DbcSignalValueType.Float32);
        var branch = new DbcSignal("PayloadBranch", 32, 8,
            DbcByteOrder.Intel, false, 1, 0, 0, 255, "", "ECM",
            MultiplexConditions:
            [
                new DbcMultiplexCondition("FloatSwitch",
                    [new DbcMuxRange(0x3F800000, 0x3F800000)])
            ]);
        var independent = branch with
        {
            Name = "Independent",
            MultiplexConditions = null
        };
        var msg = new DbcMessage(0x100, "FloatMux", 5, "ECM", false,
            [floatSwitch, branch, independent]);
        var decoder = new DbcDecoder(new DbcDatabase("float",
            new Dictionary<uint, DbcMessage> { [0x100] = msg }));
        var payload = BitConverter.GetBytes(1f).Concat(new byte[] { 9 }).ToArray();
        var samples = decoder.Decode(new CanFrame(0, 0x100, payload, 0));
        Assert.DoesNotContain(samples, sample => sample.Signal == "PayloadBranch");
        Assert.Contains(samples, sample => sample.Signal == "Independent");
        Assert.Contains(DbcEngineeringValidator.Validate(msg),
            issue => issue.Code == "MUX_CONTROLLER_FLOAT");
    }

    [Fact]
    public void ErrorAndRemoteCanObjectsNeverProduceDbcSignalObservations()
    {
        var database = Parse(
            """
            BO_ 256 Payload: 8 ECM
             SG_ State : 0|8@1+ (1,0) [0|255] "" ECM
            """);
        var decoder = new DbcDecoder(database);
        var error = new CanFrame(0, 256, [0xFF], 0, CanFrameFlags.Error);
        var remote = new CanFrame(0, 256, ReadOnlySpan<byte>.Empty, 0, CanFrameFlags.Remote);
        Assert.Empty(decoder.Decode(error));
        Assert.Empty(decoder.Decode(remote));
        Assert.Single(decoder.Decode(new CanFrame(0, 256, [0xFF], 0)));
    }

    [Fact]
    public void ExtremeIntelStartBitCannotOverflowBoundsAndCrashTheTrace()
    {
        var invalid = new DbcSignal("Overflow", int.MaxValue - 4, 16,
            DbcByteOrder.Intel, false, 1, 0, 0, 65535, "", "ECU");
        var valid = new DbcSignal("Valid", 0, 8,
            DbcByteOrder.Intel, false, 1, 0, 0, 255, "", "ECU");
        var message = new DbcMessage(0x100, "MalformedOffsets", 8, "ECU",
            false, [invalid, valid]);
        var decoder = new DbcDecoder(new DbcDatabase("overflow",
            new Dictionary<uint, DbcMessage> { [0x100] = message }));
        var observed = decoder.Decode(new CanFrame(0, 0x100,
            new byte[] { 0xAB, 0, 0, 0, 0, 0, 0, 0 }, 0));
        var only = Assert.Single(observed);
        Assert.Equal("Valid", only.Signal);
        Assert.Equal(0xAB, only.RawValue);
        Assert.Contains(DbcEngineeringValidator.Validate(message), issue =>
            issue.Code == "OUTSIDE_DLC" && issue.Signal == "Overflow");
    }

    [Fact]
    public void InvalidZeroBitSignalIsNotDecodedOrAllowedToCrashTheTrace()
    {
        var invalid = new DbcSignal("Invalid", 0, 0, DbcByteOrder.Motorola, false,
            1, 0, 0, 10, "", "ECM");
        var message = new DbcMessage(256, "Bad", 8, "ECM", false, [invalid]);
        var database = new DbcDatabase("bad", new Dictionary<uint, DbcMessage> { [256] = message });
        var frame = new CanFrame(0, 256, [0xFF], 0);
        Assert.Empty(new DbcDecoder(database).Decode(frame));
    }

    [Fact]
    public void MalformedMuxRangesAndRepeatedSignalNamesBecomeWarningsNotCrashes()
    {
        var database = Parse(
            """
            BO_ 256 Caution: 8 ECM
             SG_ Switch M : 0|8@1+ (1,0) [0|255] "" ECM
             SG_ Switch M : 8|8@1+ (1,0) [0|255] "" ECM
             SG_ TooLong : 60|16@1+ (1,0) [0|255] "" ECM
             SG_ BadMux m0 : 16|8@1+ (1,0) [0|255] "" ECM
            SG_MUL_VAL_ 256 BadMux Switch 2-1,3-4;
            """);
        var message = Assert.Single(database.AllMessageDefinitions);
        Assert.Equal(3, message.Signals.Count);
        Assert.Contains(database.ParseWarnings, warning => warning.Contains("duplicate signal", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(database.ParseWarnings, warning => warning.Contains("exceeds DLC", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(database.ParseWarnings, warning => warning.Contains("inverted", StringComparison.OrdinalIgnoreCase));
        var decoder = new DbcDecoder(database);
        var frame = new CanFrame(0, 256, [3, 0, 45, 0, 0, 0, 0, 0], 0);
        Assert.Contains(decoder.Decode(frame), value => value.Signal == "BadMux");
    }

    [Fact]
    public void UnknownExtendedMuxControllerMustNotGenerateFabricatedSignal()
    {
        var database = Parse(
            """
            BO_ 256 UnknownMux: 8 ECM
             SG_ Signal m0 : 16|8@1+ (1,0) [0|255] "" ECM
            SG_MUL_VAL_ 256 Signal NonexistentSwitch 1-3;
            """);
        var frame = new CanFrame(0, 256, [1, 2, 3, 4, 0, 0, 0, 0], 0);
        Assert.Empty(new DbcDecoder(database).Decode(frame));
    }

    [Fact]
    public void UnterminatedCommentDoesNotSwallowFollowingSignals()
    {
        // The CM_ statement is missing its trailing ';': without the guard it would join every
        // following line and the second message would report zero signals.
        var database = Parse(
            """
            BO_ 256 EngineData: 8 ECM
             SG_ EngCoolantTemp : 0|8@1+ (1,-40) [-40|215] "degC" Vector__XXX

            CM_ SG_ 256 EngCoolantTemp "unterminated comment

            BO_ 512 VehicleData: 8 BCM
             SG_ VehicleSpeed : 0|16@1+ (0.01,0) [0|250] "km/h" Vector__XXX
            """);

        Assert.Equal(2, database.Messages.Count);
        Assert.Single(database.Messages[512].Signals);
        Assert.Contains(database.ParseWarnings, warning => warning.Contains("not terminated"));
    }

    [Fact]
    public void PgnMatchingResolvesFramesFromDifferentSourceAddresses()
    {
        // J1939 EEC1 (PGN 0xF004) declared with source address 0x00 in the DBC.
        var database = Parse(
            """
            BO_ 2364540158 EEC1: 8 Vector__XXX
             SG_ EngineSpeed : 24|16@1+ (0.125,0) [0|8031.875] "rpm" Vector__XXX
            """);

        // 2364540158 = 0x8CF004FE → extended flag stripped → 0x0CF004FE (SA 0xFE).
        var declaredId = 0x0CF004FEu;
        var otherSourceId = 0x0CF00400u; // same PGN, source address 0x00

        var exact = new DbcDecoder(database);
        Assert.True(exact.TryGetMessage(0, declaredId, true, out _));
        Assert.False(exact.TryGetMessage(0, otherSourceId, true, out _));

        var pgnAware = new DbcDecoder(database, new DbcDecoderOptions(MatchByPgn: true));
        Assert.True(pgnAware.TryGetMessage(0, otherSourceId, true, out var message));
        Assert.Equal("EEC1", message.Name);

        // And the decode path produces signal values for the PGN-matched frame.
        var frame = new CanFrame(
            0,
            otherSourceId,
            [0x00, 0x00, 0x00, 0x10, 0x27, 0x00, 0x00, 0x00],
            timestampNanoseconds: 0,
            CanFrameFlags.Extended);
        var values = pgnAware.Decode(frame);
        Assert.Contains(values, value => value.Signal == "EngineSpeed");
    }

    [Fact]
    public void ChannelAssignmentExcludesOtherBuses()
    {
        var database = Parse(
            """
            BO_ 256 EngineData: 8 ECM
             SG_ EngCoolantTemp : 0|8@1+ (1,-40) [-40|215] "degC" Vector__XXX
            """);

        var channelOne = new DbcDecoder(database, new DbcDecoderOptions(Channel: 0));
        Assert.True(channelOne.TryGetMessage(0, 256, false, out _));
        Assert.False(channelOne.TryGetMessage(1, 256, false, out _));
    }

    [Fact]
    public void PgnExtractionHandlesPdu1AndPdu2()
    {
        // PDU2 (PF >= 240): PS is part of the PGN.
        Assert.Equal(0xF004u, J1939.ExtractPgn(0x0CF00400u));
        // PDU1 (PF < 240): PS is a destination address and is excluded.
        Assert.Equal(0xE800u, J1939.ExtractPgn(0x18E8A17Bu));
    }

    [Fact]
    public void J1939Pdu1AndPdu2DetailedInterpretationUsesPhysicalIdBits()
    {
        var destinationSpecific = J1939.Explain(0x18E8A17Bu);
        Assert.True(destinationSpecific.IsPdu1);
        Assert.Equal(6, destinationSpecific.Priority);
        Assert.Equal(0xE800u, destinationSpecific.Pgn);
        Assert.Equal((byte?)0xA1, destinationSpecific.DestinationAddress);
        Assert.Equal(0x7B, destinationSpecific.SourceAddress);
        Assert.Contains("PDU1", destinationSpecific.Summary);

        var broadcast = J1939.Explain(0x18EAFF00u);
        Assert.True(broadcast.IsGlobalDestination);
        Assert.Equal(0xEA00u, broadcast.Pgn);

        var groupExtension = J1939.Explain(0x0CF004FEu);
        Assert.False(groupExtension.IsPdu1);
        Assert.Null(groupExtension.DestinationAddress);
        Assert.Equal(3, groupExtension.Priority);
        Assert.Equal(0xF004u, groupExtension.Pgn);
        Assert.Equal(0xFE, groupExtension.SourceAddress);
        Assert.Contains("PDU2", groupExtension.Summary);
        Assert.Throws<ArgumentOutOfRangeException>(() => J1939.Explain(0x20000000u));
    }

    private static DbcDatabase Parse(string content)
    {
        using var reader = new StringReader(content);
        return new DbcParser().Parse("test", reader);
    }
}
