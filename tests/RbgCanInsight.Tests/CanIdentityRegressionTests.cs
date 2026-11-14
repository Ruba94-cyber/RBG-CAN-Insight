using RbgCanInsight.Correlation;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class CanIdentityRegressionTests
{
    [Fact]
    public void StandardAndExtendedIdentifiersAreNotInterchangeable()
    {
        const uint id = 0x123;
        var standard = new DbcMessage(id, "Standard", 8, "ECM", false, []);
        var extended = new DbcMessage(id, "Extended", 8, "ECM", true, []);
        var standardDecoder = new DbcDecoder(new DbcDatabase(
            "standard", new Dictionary<uint, DbcMessage> { [id] = standard }));
        var extendedDecoder = new DbcDecoder(new DbcDatabase(
            "extended", new Dictionary<uint, DbcMessage> { [id] = extended }));

        Assert.True(standardDecoder.TryGetMessage(0, id, false, out _));
        Assert.False(standardDecoder.TryGetMessage(0, id, true, out _));
        Assert.True(extendedDecoder.TryGetMessage(0, id, true, out _));
        Assert.False(extendedDecoder.TryGetMessage(0, id, false, out _));
    }

    [Fact]
    public void TraceEvidenceKeysIncludeDatabaseAndFrameFormat()
    {
        var message = new DbcMessage(0x123, "Signals", 8, "ECM", false, []);
        var extended = message with { IsExtended = true };
        var signal = new DbcSignal("Temperature", 0, 8, DbcByteOrder.Intel,
            false, 1, 0, 0, 255, "degC", "ECM");

        var a = DidSignalCorrelator.TraceReportKey("networkA", message, signal);
        var b = DidSignalCorrelator.TraceReportKey("networkB", message, signal);
        var c = DidSignalCorrelator.TraceReportKey("networkA", extended, signal);

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void ShortPayloadFramesHaveDeterministicEquality()
    {
        var a = new CanFrame(0, 0x123, [0x01], 1_000_000);
        var b = new CanFrame(0, 0x123, [0x01], 1_000_000);

        Assert.Equal(a, b);
    }
}
