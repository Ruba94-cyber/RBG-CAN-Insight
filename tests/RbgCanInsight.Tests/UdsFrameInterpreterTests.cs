using RbgCanInsight.Correlation;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class UdsFrameInterpreterTests
{
    private static string? Describe(uint id, byte[] payload, Func<int, string?>? resolver = null) =>
        UdsFrameInterpreter.Describe(payload, id, isExtended: false, resolver);

    [Fact]
    public void ReadDataByIdentifierRequestNamesTheDid()
    {
        var text = Describe(0x7E0, [0x03, 0x22, 0xF1, 0x90, 0x00, 0x00, 0x00, 0x00]);

        Assert.NotNull(text);
        Assert.Contains("UDS Req", text);
        Assert.Contains("ReadDataByIdentifier", text);
        Assert.Contains("0xF190", text);
        Assert.Contains("VIN", text); // standard DID fallback name
    }

    [Fact]
    public void CddResolverNameWinsOverStandardTable()
    {
        var text = Describe(
            0x7E0,
            [0x03, 0x22, 0x4A, 0x21, 0x00, 0x00, 0x00, 0x00],
            did => did == 0x4A21 ? "CoolantTemperature" : null);

        Assert.NotNull(text);
        Assert.Contains("CoolantTemperature", text);
    }

    [Fact]
    public void PositiveResponseShowsDataPreview()
    {
        var text = Describe(0x7E8, [0x05, 0x62, 0xF1, 0x86, 0x03, 0x00, 0x00, 0x00]);

        Assert.NotNull(text);
        Assert.Contains("UDS Resp", text);
        Assert.Contains("ReadDataByIdentifier", text);
        Assert.Contains("ActiveDiagnosticSession", text);
        Assert.Contains("= 03", text);
    }

    [Fact]
    public void NegativeResponseNamesServiceAndReason()
    {
        var text = Describe(0x7E8, [0x03, 0x7F, 0x22, 0x31, 0x00, 0x00, 0x00, 0x00]);

        Assert.NotNull(text);
        Assert.Contains("UDS Neg", text);
        Assert.Contains("ReadDataByIdentifier", text);
        Assert.Contains("requestOutOfRange", text);
    }

    [Fact]
    public void SessionControlNamesTheSession()
    {
        var text = Describe(0x7E0, [0x02, 0x10, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00]);

        Assert.NotNull(text);
        Assert.Contains("DiagnosticSessionControl", text);
        Assert.Contains("extended", text);
    }

    [Fact]
    public void FirstFrameIsRecognisedWithTotalLength()
    {
        // FF: total 23 bytes of a 0x62 F1 90 response.
        var text = Describe(0x7E8, [0x10, 0x17, 0x62, 0xF1, 0x90, 0x57, 0x42, 0x41]);

        Assert.NotNull(text);
        Assert.Contains("UDS Resp ReadDataByIdentifier", text);
        Assert.Contains("first frame", text);
        Assert.Contains("23 B", text);
    }

    [Fact]
    public void ConsecutiveAndFlowControlFramesAreLabelledOnDiagnosticIdsOnly()
    {
        Assert.Contains("continuation #1", Describe(0x7E8, [0x21, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77]));
        Assert.Contains("flow control", Describe(0x7E0, [0x30, 0x00, 0x14, 0x00, 0x00, 0x00, 0x00, 0x00]));

        // The same byte patterns on a normal broadcast ID must not be labelled.
        Assert.Null(Describe(0x1A0, [0x21, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77]));
        Assert.Null(Describe(0x1A0, [0x30, 0x00, 0x14, 0x00, 0x00, 0x00, 0x00, 0x00]));
    }

    [Fact]
    public void NormalSignalTrafficIsNotMislabelled()
    {
        // Typical cyclic payloads that superficially resemble ISO-TP single frames.
        Assert.Null(Describe(0x1A0, [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]));
        Assert.Null(Describe(0x1A0, [0x04, 0x5A, 0x12, 0xFF, 0x00, 0x00, 0x00, 0x00]));
        Assert.Null(Describe(0x1A0, [0x83, 0x22, 0xF1, 0x90, 0x00, 0x00, 0x00, 0x00]));
    }

    [Fact]
    public void TesterPresentOnDiagnosticIdIsRecognised()
    {
        var text = Describe(0x7DF, [0x02, 0x3E, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00]);

        Assert.NotNull(text);
        Assert.Contains("TesterPresent", text);
        Assert.Contains("suppress", text);
    }

    [Fact]
    public void ExtendedDiagnosticAddressingIsRecognised()
    {
        var text = UdsFrameInterpreter.Describe(
            [0x02, 0x27, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00],
            0x18DA10F1, isExtended: true, didNameResolver: null);

        Assert.NotNull(text);
        Assert.Contains("SecurityAccess", text);
        Assert.Contains("request seed", text);

        Assert.True(UdsFrameInterpreter.LooksLikeDiagnosticId(0x18DAF110, isExtended: true));
        Assert.False(UdsFrameInterpreter.LooksLikeDiagnosticId(0x18FEF100, isExtended: true));
    }

    [Fact]
    public void RoutineControlNamesActionAndRoutine()
    {
        var text = Describe(0x7E0, [0x04, 0x31, 0x01, 0x02, 0x03, 0x00, 0x00, 0x00]);

        Assert.NotNull(text);
        Assert.Contains("RoutineControl", text);
        Assert.Contains("start", text);
        Assert.Contains("0x0203", text);
    }

    [Fact]
    public void EmptyAndShortPayloadsAreSafe()
    {
        Assert.Null(Describe(0x7E0, []));
        Assert.Null(Describe(0x7E0, [0x10]));
        Assert.Null(Describe(0x7E0, [0x00]));
    }

    [Fact]
    public void LearnedDiagnosticIdDecodesContinuationsOnAnyIdRange()
    {
        // OEM-specific diagnostic ID outside 0x700-0x7FF: with assumeDiagnostic (learned from
        // the trace) continuation and flow-control frames decode there too.
        var continuation = UdsFrameInterpreter.Describe(
            [0x21, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77], 0x654, isExtended: false,
            didNameResolver: null, assumeDiagnostic: true);
        var flowControl = UdsFrameInterpreter.Describe(
            [0x30, 0x00, 0x14, 0x00, 0x00, 0x00, 0x00, 0x00], 0x654, isExtended: false,
            didNameResolver: null, assumeDiagnostic: true);

        Assert.Contains("continuation", continuation);
        Assert.Contains("flow control", flowControl);
    }

    [Fact]
    public void LooksLikeUdsFrameLearnsFromWellFormedFramesAndCatalogDids()
    {
        // RDBI request: strong evidence on any identifier.
        Assert.True(UdsFrameInterpreter.LooksLikeUdsFrame([0x03, 0x22, 0xF1, 0x90, 0x00, 0x00, 0x00, 0x00]));
        // Response whose DID exists in the loaded CDD: evidence even on an OEM-range ID.
        Assert.True(UdsFrameInterpreter.LooksLikeUdsFrame(
            [0x04, 0x62, 0x4A, 0x21, 0x5C, 0x00, 0x00, 0x00],
            did => did == 0x4A21 ? "CoolantTemperature" : null));
        // Plain cyclic signal data: no evidence.
        Assert.False(UdsFrameInterpreter.LooksLikeUdsFrame([0x21, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77]));
        Assert.False(UdsFrameInterpreter.LooksLikeUdsFrame([0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]));
    }
}
