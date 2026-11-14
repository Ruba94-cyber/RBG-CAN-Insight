using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DbcSignalRawMathTests
{
    private static DbcSignal Signal(bool signed = false, int bits = 8, double factor = 1,
        double offset = 0, IReadOnlyList<DbcScaleSegment>? segments = null,
        DbcSignalValueType type = DbcSignalValueType.Integer) =>
        new("Test", 0, bits, DbcByteOrder.Intel, signed, factor, offset,
            -1000, 1000, "", "ECM", ScaleSegments: segments, ValueType: type);

    [Fact]
    public void SignedAllOnesIsNegativeOneNotMaximumPositiveValue()
    {
        Assert.Equal(-1d, DbcSignalRawMath.AllOnesPhysical(Signal(signed: true)));
        Assert.Equal(3d, DbcSignalRawMath.AllOnesPhysical(Signal(signed: true, factor: 2, offset: 5)));
        Assert.Equal(255d, DbcSignalRawMath.AllOnesPhysical(Signal()));
    }

    [Fact]
    public void OnlyUnambiguousLinearIntegerCanReconstructRawState()
    {
        Assert.True(DbcSignalRawMath.TryReconstructRaw(Signal(factor: .5, offset: -40), -15, out var raw));
        Assert.Equal(50d, raw);
        Assert.False(DbcSignalRawMath.TryReconstructRaw(Signal(), 999, out _));
        Assert.False(DbcSignalRawMath.TryReconstructRaw(Signal(factor: 0), 0, out _));
    }

    [Fact]
    public void SegmentedFloatAndWideSignalsAreNeverGuessed()
    {
        var segment = new DbcScaleSegment(0, 255, 2, 10);
        Assert.False(DbcSignalRawMath.TryReconstructRaw(
            Signal(segments: [segment]), 50, out _));
        Assert.False(DbcSignalRawMath.TryReconstructRaw(
            Signal(type: DbcSignalValueType.Float32), 50, out _));
        Assert.False(DbcSignalRawMath.TryReconstructRaw(Signal(bits: 64), 50, out _));
        Assert.Null(DbcSignalRawMath.AllOnesPhysical(Signal(bits: 64)));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 255)]
    [InlineData(true, -128)]
    [InlineData(true, -1)]
    [InlineData(true, 127)]
    public void CollapsedAdjacentCodesAreNeverReportedAsExactRaw(bool signed, int actualRaw)
    {
        var signal = Signal(signed: signed, factor: 1, offset: 1e20);
        Assert.True(signal.TryGetPhysicalValue(actualRaw, out var physical));
        Assert.False(DbcSignalRawMath.TryReconstructRaw(signal, physical, out _));
        Assert.Null(DbcSignalRawMath.AllOnesPhysical(signal));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 255)]
    [InlineData(true, -128)]
    [InlineData(true, -1)]
    [InlineData(true, 127)]
    public void DistinctAdjacentCodesRoundTripIncludingRangeBoundaries(bool signed, int actualRaw)
    {
        var signal = Signal(signed: signed, factor: 0.1, offset: -40);
        Assert.True(signal.TryGetPhysicalValue(actualRaw, out var physical));
        Assert.True(DbcSignalRawMath.TryReconstructRaw(signal, physical, out var raw));
        Assert.Equal(actualRaw, raw);
    }

    [Fact]
    public void PhysicalValueNotProducedByAnyIntegerRawIsNotRoundedIntoEvidence()
    {
        Assert.False(DbcSignalRawMath.TryReconstructRaw(Signal(factor: 1, offset: 1e9),
            1e9 + 0.01, out _));
    }

    [Fact]
    public void SmallScaleKeepsAdjacentAndAllOnesValuesDistinct()
    {
        var signal = Signal(factor: 1e-12);
        Assert.True(signal.TryGetPhysicalValue(254, out var previous));
        Assert.True(signal.TryGetPhysicalValue(255, out var allOnes));
        Assert.NotEqual(previous, allOnes);
        Assert.Equal(allOnes, DbcSignalRawMath.AllOnesPhysical(signal));
        Assert.True(DbcSignalRawMath.TryReconstructRaw(signal, previous, out var raw));
        Assert.Equal(254d, raw);
    }
}
