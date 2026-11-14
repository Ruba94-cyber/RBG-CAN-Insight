using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Regressions for defects found by the 2026-11-11 DBC / ARXML audit. Expected values
/// are worked out by hand from the payload bytes.</summary>
public sealed class DbcAuditRegressionTests
{
    private const string Dbc =
        "VERSION \"\"\n" +
        "NS_ :\n" +
        "BS_:\n" +
        "BU_: ECU\n" +
        "BO_ 256 Msg: 8 ECU\n" +
        " SG_ Sig : 0|8@1+ (1,0) [0|0] \"\" ECU\n" +
        " SG_ Scaled : 8|2@1+ (0.1,0) [0|0.3] \"\" ECU\n" +
        "CM_ SG_ 256 Sig \"Bits: 0=off;\n" +
        "1=on\";\n" +
        "CM_ SG_ 256 Scaled \"see C:\\new\";\n" +
        "VAL_ 256 Sig 1 \"On\" 18446744073709551615 \"Invalid\" 0 \"Off\" ;\n";

    private static DbcDatabase Parse() => new DbcParser().Parse("audit", new StringReader(Dbc));

    [Fact]
    public void ValueTableKeyBeyondInt64DoesNotAbortTheDatabase()
    {
        var database = Parse();

        var signal = database.Messages[256].Signals.Single(s => s.Name == "Sig");
        Assert.Equal("On", signal.ValueDescriptions![1]);
        Assert.Equal("Off", signal.ValueDescriptions[0]);
        Assert.Equal(2, signal.ValueDescriptions.Count);
        Assert.Contains(database.ParseWarnings, w => w.Contains("VAL_ key 18446744073709551615"));
    }

    [Fact]
    public void SemicolonAtLineEndInsideAQuotedCommentDoesNotEndTheStatement()
    {
        var signal = Parse().Messages[256].Signals.Single(s => s.Name == "Sig");

        Assert.Equal("Bits: 0=off;\n1=on", signal.Comment);
    }

    [Fact]
    public void BackslashFollowedByNIsNotALineBreak()
    {
        var signal = Parse().Messages[256].Signals.Single(s => s.Name == "Scaled");

        Assert.Equal(@"see C:\new", signal.Comment);
    }

    [Fact]
    public void UndeclaredRangeAndValuesOnTheLimitAreNotOutOfRange()
    {
        var message = Parse().Messages[256];
        var unbounded = message.Signals.Single(s => s.Name == "Sig");
        var scaled = message.Signals.Single(s => s.Name == "Scaled");

        Assert.False(unbounded.IsOutsideDeclaredRange(5));   // [0|0] declares no range
        Assert.False(scaled.IsOutsideDeclaredRange(3 * 0.1)); // 0.30000000000000004 is the limit itself
        Assert.False(scaled.IsOutsideDeclaredRange(0));
        Assert.True(scaled.IsOutsideDeclaredRange(0.4));
        Assert.True(scaled.IsOutsideDeclaredRange(-0.1));
        Assert.False(scaled.IsOutsideDeclaredRange(double.NaN));
    }

    private static string Signal(string name, int length, string baseType) =>
        $"""
        <I-SIGNAL>
          <SHORT-NAME>{name}</SHORT-NAME>
          <LENGTH>{length}</LENGTH>
          <NETWORK-REPRESENTATION-PROPS><SW-DATA-DEF-PROPS-VARIANTS><SW-DATA-DEF-PROPS-CONDITIONAL>
            <BASE-TYPE-REF DEST="SW-BASE-TYPE">/Demo/{baseType}</BASE-TYPE-REF>
          </SW-DATA-DEF-PROPS-CONDITIONAL></SW-DATA-DEF-PROPS-VARIANTS></NETWORK-REPRESENTATION-PROPS>
        </I-SIGNAL>
        """;

    private static string Mapping(string name, int start, string order) =>
        $"""
        <I-SIGNAL-TO-I-PDU-MAPPING>
          <SHORT-NAME>{name}Map</SHORT-NAME>
          <I-SIGNAL-REF DEST="I-SIGNAL">/Demo/{name}</I-SIGNAL-REF>
          <PACKING-BYTE-ORDER>{order}</PACKING-BYTE-ORDER>
          <START-POSITION>{start}</START-POSITION>
        </I-SIGNAL-TO-I-PDU-MAPPING>
        """;

    private static string BaseType(string name, string encoding) =>
        $"<SW-BASE-TYPE><SHORT-NAME>{name}</SHORT-NAME><BASE-TYPE-ENCODING>{encoding}</BASE-TYPE-ENCODING></SW-BASE-TYPE>";

    private static DbcDatabase ParseArxml(string addressingMode)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rbg-arxml-audit-{Guid.NewGuid():N}.arxml");
        File.WriteAllText(path,
            "<AUTOSAR><AR-PACKAGES><AR-PACKAGE><SHORT-NAME>Demo</SHORT-NAME><ELEMENTS>" +
            BaseType("uint16", "NONE") + BaseType("sint8", "2C") + BaseType("float32", "IEEE754") + BaseType("sm8", "SM") +
            Signal("BeSpeed", 16, "uint16") + Signal("SignedTemp", 8, "sint8") +
            Signal("FloatVal", 32, "float32") + Signal("Legacy", 8, "sm8") +
            "<I-SIGNAL-I-PDU><SHORT-NAME>Pdu</SHORT-NAME><I-SIGNAL-TO-PDU-MAPPINGS>" +
            Mapping("BeSpeed", 7, "MOST-SIGNIFICANT-BYTE-FIRST") +
            Mapping("SignedTemp", 16, "MOST-SIGNIFICANT-BYTE-LAST") +
            Mapping("Legacy", 24, "MOST-SIGNIFICANT-BYTE-LAST") +
            Mapping("FloatVal", 32, "MOST-SIGNIFICANT-BYTE-LAST") +
            "</I-SIGNAL-TO-PDU-MAPPINGS></I-SIGNAL-I-PDU>" +
            "<CAN-FRAME><SHORT-NAME>Frame</SHORT-NAME><FRAME-LENGTH>8</FRAME-LENGTH><PDU-TO-FRAME-MAPPINGS>" +
            "<PDU-TO-FRAME-MAPPING><PDU-REF DEST=\"I-SIGNAL-I-PDU\">/Demo/Pdu</PDU-REF></PDU-TO-FRAME-MAPPING>" +
            "</PDU-TO-FRAME-MAPPINGS></CAN-FRAME>" +
            "<CAN-FRAME-TRIGGERING><SHORT-NAME>Trig</SHORT-NAME>" + addressingMode +
            "<IDENTIFIER>291</IDENTIFIER><FRAME-REF DEST=\"CAN-FRAME\">/Demo/Frame</FRAME-REF></CAN-FRAME-TRIGGERING>" +
            "</ELEMENTS></AR-PACKAGE></AR-PACKAGES></AUTOSAR>");
        try { return new ArxmlParser().ParseFile(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ArxmlHonoursByteOrderSignednessAndIeeeEncoding()
    {
        var database = ParseArxml("<CAN-ADDRESSING-MODE>STANDARD</CAN-ADDRESSING-MODE>");

        // 12 34 big-endian = 0x1234; FF two's complement = -1; 00 00 C0 3F little-endian float = 1.5.
        var frame = new CanFrame(0, 0x123, [0x12, 0x34, 0xFF, 0x81, 0x00, 0x00, 0xC0, 0x3F], 0);
        var values = new DbcDecoder(database).Decode(frame).ToDictionary(v => v.Signal, v => v.PhysicalValue);

        Assert.Equal(4660, values["BeSpeed"]);
        Assert.Equal(-1, values["SignedTemp"]);
        Assert.Equal(1.5, values["FloatVal"]);

        // Sign-magnitude is not two's complement: no physical value may be invented for it.
        var legacy = database.Messages[0x123].Signals.Single(s => s.Name == "Legacy");
        Assert.True(legacy.PhysicalConversionUnsupported);
        Assert.Contains(database.ParseWarnings, w => w.Contains("Legacy") && w.Contains("SM"));
    }

    [Fact]
    public void ArxmlExtendedFrameWithSmallIdentifierMatchesOnlyExtendedFrames()
    {
        var database = ParseArxml("<CAN-ADDRESSING-MODE>EXTENDED</CAN-ADDRESSING-MODE>");

        Assert.True(database.TryGetMessage(0x123, extended: true, out var message));
        Assert.True(message.IsExtended);
        Assert.False(database.TryGetMessage(0x123, extended: false, out _));
    }

    [Fact]
    public void ArxmlWithoutAddressingModeStillInfersTheFormatFromTheIdentifier()
    {
        var database = ParseArxml(string.Empty);

        Assert.True(database.TryGetMessage(0x123, extended: false, out _));
    }
}
