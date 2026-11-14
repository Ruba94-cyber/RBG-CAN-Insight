using RbgCanInsight.Dbc;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DbcSignalInspectorTests
{
    private static DbcSignal Signal(
        string name, int start, int length, DbcByteOrder order = DbcByteOrder.Intel,
        IReadOnlyDictionary<long, string>? values = null, int? mux = null) =>
        new(name, start, length, order, false, 0.5, -40, -40, 100,
            "°C", "ECU", false, mux, values);

    private static DbcMessage Message(int dlc, params DbcSignal[] signals) =>
        new(0x123, "Signals", dlc, "ECM", false, signals);

    [Fact]
    public void IntelBitLayoutStartsAtLsbAndFollowsConsecutivePhysicalBits()
    {
        var signal = Signal("Temp", 3, 10);
        var layout = DbcSignalInspector.Inspect(Message(8, signal), signal);
        Assert.Equal(Enumerable.Range(3, 10), layout.PhysicalBitPositions);
        var start = layout.Rows[0].Cells.Single(bit => bit.BitNumber == 3);
        Assert.True(start.IsSignalStart);
        Assert.Equal(0, start.LogicalSignalBit);
        var mostSignificant = layout.Rows[1].Cells.Single(bit => bit.BitNumber == 4);
        Assert.Equal(9, mostSignificant.LogicalSignalBit);
        Assert.False(layout.Warnings.Any());
    }

    [Fact]
    public void MotorolaSawtoothLayoutAndLogicalBitOrderAreExplicit()
    {
        // DBC @0 start bit 7 walks down to 0 then jumps to byte 1 bit 7.
        var signal = Signal("Temp", 7, 12, DbcByteOrder.Motorola);
        var layout = DbcSignalInspector.Inspect(Message(8, signal), signal);
        Assert.Equal(new[] { 7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12 },
            layout.PhysicalBitPositions);
        Assert.True(layout.Rows[0].Cells[0].IsSignalStart);
        Assert.Equal(11, layout.Rows[0].Cells[0].LogicalSignalBit);
        Assert.Equal(0, layout.Rows[1].Cells.Single(bit => bit.BitNumber == 4).LogicalSignalBit);
        Assert.Equal("Motorola / big-endian (DBC @0)", layout.ByteOrderText);
    }

    [Fact]
    public void InvalidHugeStartBitDoesNotWrapAroundToAFalsePayloadPosition()
    {
        var nearOverflow = Signal("Overflow", int.MaxValue, 3);
        Assert.Empty(DbcSignalInspector.PhysicalBits(nearOverflow));
        Assert.Empty(DbcSignalInspector.PhysicalBits(
            nearOverflow with { ByteOrder = DbcByteOrder.Motorola, StartBit = int.MaxValue - 7 }));
    }

    [Fact]
    public void MotorolaStartAtLsbJumpsToNextByteMsb()
    {
        var signal = Signal("Odd", 0, 5, DbcByteOrder.Motorola);
        Assert.Equal(new[] { 0, 15, 14, 13, 12 }, DbcSignalInspector.PhysicalBits(signal));
    }

    [Fact]
    public void EnumValuesDescribeRawCodesAndPhysicallyScaledValue()
    {
        var states = Signal("State", 0, 2, values: new Dictionary<long, string>
        {
            [0] = "Off",
            [1] = "Standby",
            [2] = "Active"
        });
        var layout = DbcSignalInspector.Inspect(Message(1, states), states);
        Assert.Equal(3, layout.NamedValues.Count);
        var active = layout.NamedValues.Single(value => value.Raw == 2);
        Assert.Equal("Active", active.Meaning);
        Assert.Equal(-39, active.Physical);
        Assert.Equal("0x2", active.RawHex);
    }

    [Fact]
    public void BitCollisionsAreVisibleButNotCalledInvalidWhenMuxBranchesCanOverlap()
    {
        var branchA = Signal("BranchA", 0, 8, mux: 1);
        var branchB = Signal("BranchB", 0, 8, mux: 2);
        var layout = DbcSignalInspector.Inspect(Message(1, branchA, branchB), branchA);
        Assert.All(layout.Rows[0].Cells, cell => Assert.True(cell.IsOverlapping));
        Assert.Contains(layout.Warnings, warning => warning.Contains("multiplexing", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("multiplexor = 1", layout.MultiplexText);
    }

    [Fact]
    public void SignalBeyondDlcIsClearlyFlaggedWithoutHidingPosition()
    {
        var signal = Signal("Invalid", 62, 16);
        var layout = DbcSignalInspector.Inspect(Message(8, signal), signal);
        Assert.Equal(16, layout.PhysicalBitPositions.Count);
        Assert.Contains(layout.Warnings, warning => warning.Contains("outside", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BitOverlayShowsRealRecordedBytesWithoutAssumingMissingPayload()
    {
        var signal = Signal("Mode", 0, 12);
        var layout = DbcSignalInspector.Inspect(Message(8, signal), signal);
        var rows = DbcSignalInspector.OverlayRecordedPayload(layout, [0b1010_0001, 0b0000_0110]);
        Assert.Equal(1, rows[0].Cells.Single(cell => cell.BitNumber == 7).RecordedValue);
        Assert.Equal(0, rows[0].Cells.Single(cell => cell.BitNumber == 6).RecordedValue);
        Assert.Equal(1, rows[0].Cells.Single(cell => cell.BitNumber == 5).RecordedValue);
        Assert.Equal(1, rows[1].Cells.Single(cell => cell.BitNumber == 2).RecordedValue);
        Assert.Null(rows[2].Cells[0].RecordedValue);
        Assert.Contains("recorded raw bit", rows[0].Cells[0].Tooltip);
        Assert.Null(layout.Rows[0].Cells[0].RecordedValue); // Metadata not mutated
    }

    [Fact]
    public void SignedAndUnsignedRawRepresentableRangesAreMathematicallyExact()
    {
        var unsigned = Signal("Full64", 0, 64);
        var signed = unsigned with { Name = "Signed64", IsSigned = true };
        var message = Message(8, unsigned, signed);
        Assert.Contains("18446744073709551615", DbcSignalInspector.Inspect(message, unsigned).CodedRangeText);
        Assert.Contains("-9223372036854775808", DbcSignalInspector.Inspect(message, signed).CodedRangeText);
        Assert.Contains("9223372036854775807", DbcSignalInspector.Inspect(message, signed).CodedRangeText);
        Assert.Contains(DbcSignalInspector.Inspect(message, unsigned).Warnings,
            warning => warning.Contains("2^53-1", StringComparison.Ordinal));
        Assert.Contains(DbcSignalInspector.Inspect(message, signed).Warnings,
            warning => warning.Contains("Wide integer signal", StringComparison.Ordinal));
    }

    [Fact]
    public void RecordedRawValueIsExactForFull64BitSignedAndUnsignedSignals()
    {
        var unsigned = Signal("Unsigned64", 0, 64) with { Factor = 1, Offset = 0 };
        var high = ulong.MaxValue - 3;
        var raw = DbcSignalInspector.InspectRecordedRaw(unsigned,
            BitConverter.GetBytes(high));
        Assert.NotNull(raw);
        Assert.Equal(high, raw.RawBits);
        Assert.Equal("18446744073709551612", raw.RawText);
        Assert.Equal("0xFFFFFFFFFFFFFFFC", raw.RawHex);
        Assert.False(raw.IsExactInDouble);
        Assert.Null(raw.DeclaredMeaning);

        var signed = unsigned with
        {
            Name = "Signed64", IsSigned = true,
            ValueDescriptions = new Dictionary<long, string>
            {
                [long.MinValue + 1] = "Minimum plus one"
            }
        };
        var negative = DbcSignalInspector.InspectRecordedRaw(signed,
            BitConverter.GetBytes(unchecked((ulong)(long.MinValue + 1))));
        Assert.NotNull(negative);
        Assert.Equal((long.MinValue + 1).ToString(
            System.Globalization.CultureInfo.InvariantCulture), negative.RawText);
        Assert.Equal("Minimum plus one", negative.DeclaredMeaning);
        Assert.False(negative.IsExactInDouble);
        Assert.Null(DbcSignalInspector.InspectRecordedRaw(signed, new byte[7]));
    }

    [Fact]
    public void RecordedMotorolaSawtoothBitsProduceExactUnsignedHexAndCodedText()
    {
        var motorola = Signal("MotorolaCode", 7, 12, DbcByteOrder.Motorola) with
        {
            ValueDescriptions = new Dictionary<long, string> { [0xABC] = "Armed" }
        };
        var recorded = DbcSignalInspector.InspectRecordedRaw(motorola,
            new byte[] { 0xAB, 0xC0 });
        Assert.NotNull(recorded);
        Assert.Equal(0xABCUL, recorded.RawBits);
        Assert.Equal("0xABC", recorded.RawHex);
        Assert.Equal("2748", recorded.RawText);
        Assert.Equal("Armed", recorded.DeclaredMeaning);
        Assert.True(recorded.IsExactInDouble);
        Assert.Null(DbcSignalInspector.InspectRecordedRaw(motorola,
            new byte[] { 0xAB }));
    }

    [Fact]
    public void FloatingPointRecordedRawIsAnExactBitPatternNotAnIntegerEnum()
    {
        var floating = Signal("Float32", 0, 32) with
        {
            ValueType = DbcSignalValueType.Float32,
            ValueDescriptions = new Dictionary<long, string> { [0] = "Not a floating enum" }
        };
        var result = DbcSignalInspector.InspectRecordedRaw(floating,
            BitConverter.GetBytes(12.5f));
        Assert.NotNull(result);
        Assert.True(result.IsFloatingPoint);
        Assert.Equal("0x41480000", result.RawHex);
        Assert.Equal("IEEE-754 bit pattern", result.RawText);
        Assert.Null(result.DeclaredMeaning);
        Assert.Null(DbcSignalInspector.InspectRecordedRaw(
            floating with { BitLength = 16 }, new byte[8]));
    }

    [Fact]
    public void Int64DbcNamedCodesDoNotGetMisidentifiedThroughDoubleRounding()
    {
        var named = Signal("Raw64", 0, 64) with
        {
            IsSigned = true,
            ValueDescriptions = new Dictionary<long, string>
            {
                [0] = "Zero",
                [9007199254740991L] = "Largest exact double integer",
                [9007199254740992L] = "Rounded boundary",
                [long.MaxValue] = "Max signed code",
                [long.MinValue] = "Min signed code"
            }
        };
        Assert.Equal("Zero", named.GetValueDescription(0d));
        Assert.Equal("Largest exact double integer",
            named.GetValueDescription(9007199254740991d));
        Assert.Null(named.GetValueDescription(9007199254740992d));
        Assert.Null(named.GetValueDescription(9223372036854775808d));
        Assert.Null(named.GetValueDescription(double.PositiveInfinity));
        Assert.Null(named.GetValueDescription(double.NaN));
        Assert.Null(named.GetValueDescription(0.5d));

        // Integer-oriented consumers can still use the full signed range.
        Assert.Equal("Rounded boundary", named.GetValueDescription(9007199254740992L));
        Assert.Equal("Max signed code", named.GetValueDescription(long.MaxValue));
        Assert.Equal("Min signed code", named.GetValueDescription(long.MinValue));
    }

    [Fact]
    public void ExtractorMatchesDecoderForMotorolaAndIntelCoordinates()
    {
        var intel = Signal("Intel", 5, 12);
        var motorola = Signal("Motorola", 7, 12, DbcByteOrder.Motorola);
        var a = DbcSignalInspector.Inspect(Message(8, intel), intel).PhysicalBitPositions;
        var b = DbcSignalInspector.Inspect(Message(8, motorola), motorola).PhysicalBitPositions;
        Assert.Equal(12, a.Distinct().Count());
        Assert.Equal(12, b.Distinct().Count());
        Assert.Equal(5, a[0]);
        Assert.Equal(7, b[0]);
        Assert.Equal(15, b[8]);
    }
}
