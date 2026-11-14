using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DbcTests
{
    private const string SampleDbc =
        """
        VERSION ""

        BO_ 256 EngineData: 8 ECM
         SG_ EngineSpeed : 0|16@1+ (0.125,0) [0|8031.875] "rpm" Vector__XXX
         SG_ CoolantTemp : 16|8@1+ (1,-40) [-40|215] "degC" Vector__XXX
         SG_ EngineRunning : 24|1@1+ (1,0) [0|1] "" Vector__XXX

        BO_ 512 VehicleData: 8 BCM
         SG_ VehicleSpeed : 0|16@1+ (0.01,0) [0|655.35] "km/h" Vector__XXX

        CM_ SG_ 256 CoolantTemp "Engine coolant temperature";
        CM_ BO_ 256 "Engine status frame";
        VAL_ 256 EngineRunning 0 "Off" 1 "Running";
        """;

    private static DbcDatabase Parse() =>
        new DbcParser().Parse("test", new StringReader(SampleDbc));

    [Fact]
    public void ParsesMessagesSignalsCommentsAndValues()
    {
        var database = Parse();

        Assert.Equal(2, database.Messages.Count);
        var engine = database.Messages[256u];
        Assert.Equal("EngineData", engine.Name);
        Assert.Equal("Engine status frame", engine.Comment);
        Assert.Equal(3, engine.Signals.Count);

        var coolant = engine.Signals.Single(signal => signal.Name == "CoolantTemp");
        Assert.Equal("Engine coolant temperature", coolant.Comment);
        Assert.Equal(-40, coolant.Minimum);
        Assert.Equal(215, coolant.Maximum);
        Assert.Equal("degC", coolant.Unit);

        var running = engine.Signals.Single(signal => signal.Name == "EngineRunning");
        Assert.Equal("Running", running.GetValueDescription(1));
    }

    [Fact]
    public void ParsesVectorGenMsgCycleTimeWithoutConfusingItWithMeasuredTracePeriod()
    {
        const string dbc =
            """
            BO_ 256 Cyclic: 8 ECU
             SG_ Temperature : 0|8@1+ (1,0) [0|255] "degC" ECM
            BA_ "GenMsgCycleTime" BO_ 256 100;
            """;
        var db = new DbcParser().Parse("cycles", new StringReader(dbc));
        var msg = Assert.Single(db.Messages.Values);
        Assert.Equal(100, msg.NominalCycleTimeMilliseconds);
        Assert.Null(new DbcParser().Parse("no_cycle",
            new StringReader("BO_ 256 Cyclic: 8 ECU")).Messages[256].NominalCycleTimeMilliseconds);
    }

    [Fact]
    public void DuplicateMultiplexorNamesCannotCrashOfflineDecodingOrActivateAFalseBranch()
    {
        var controller = new DbcSignal("Selector", 0, 2,
            DbcByteOrder.Intel, false, 1, 0, 0, 3, "", "ECU",
            IsMultiplexor: true);
        var duplicate = controller with { StartBit = 2 };
        var conditional = new DbcSignal("Branch", 8, 8,
            DbcByteOrder.Intel, false, 1, 0, 0, 255, "", "ECU",
            MultiplexValue: 1);
        var unconditional = new DbcSignal("Always", 16, 8,
            DbcByteOrder.Intel, false, 1, 0, 0, 255, "", "ECU");
        var message = new DbcMessage(0x100, "MalformedMux", 3, "ECU", false,
            [controller, duplicate, conditional, unconditional]);
        var database = new DbcDatabase("malformed",
            new Dictionary<uint, DbcMessage> { [0x100] = message });
        var values = new DbcDecoder(database).Decode(
            new CanFrame(0, 0x100, new byte[] { 1, 49, 55 }, 0));

        var unaffected = Assert.Single(values);
        Assert.Equal("Always", unaffected.Signal);
        Assert.Equal(55, unaffected.RawValue);
        Assert.DoesNotContain(values, value => value.Signal == "Branch");
        Assert.DoesNotContain(values, value => value.Signal == "Selector");
    }

    [Fact]
    public void DecodesPhysicalValues()
    {
        var decoder = new DbcDecoder(Parse());
        // EngineSpeed raw 8000 => 1000 rpm; CoolantTemp raw 130 => 90 degC; running = 1.
        var payload = new byte[8];
        payload[0] = 0x40; // 8000 = 0x1F40
        payload[1] = 0x1F;
        payload[2] = 130;
        payload[3] = 0x01;
        var frame = new CanFrame(0, 256, payload, 1_000_000);

        var values = decoder.Decode(frame);

        Assert.Equal(1000, values.Single(v => v.Signal == "EngineSpeed").PhysicalValue);
        Assert.Equal(90, values.Single(v => v.Signal == "CoolantTemp").PhysicalValue);
        Assert.Equal(1, values.Single(v => v.Signal == "EngineRunning").PhysicalValue);
    }

    [Fact]
    public void ParsesCommonExportFormattingVariants()
    {
        const string dbc =
            """
            VERSION ""

            BO_ 2147483904 ExtendedFrame: 8 ECM
             SG_ Mode M : 0 | 4 @ 1 + ( 1, 0 ) [ 0 | 15 ] "" Vector__XXX,LOGGER
             SG_ SignedValue m1 : 8 | 12 @ 1 - ( 0.5, -10 ) [ -100 | 100 ] "A" Vector__XXX, LOGGER

            CM_ BO_ 2147483904 "First line
            second line";
            CM_ SG_ 2147483904 SignedValue "Signed value comment";
            VAL_ 2147483904 Mode 0 "Off" 1 "On" 2 "Fallback";
            """;

        var database = new DbcParser().Parse("variant", new StringReader(dbc));

        var message = Assert.Single(database.Messages.Values);
        Assert.True(message.IsExtended);
        Assert.Equal(256u, message.Identifier);
        Assert.Contains("second line", message.Comment);

        var mode = message.Signals.Single(signal => signal.Name == "Mode");
        Assert.True(mode.IsMultiplexor);
        Assert.Equal("Vector__XXX,LOGGER", mode.Receiver);
        Assert.Equal("Fallback", mode.GetValueDescription(2));

        var signed = message.Signals.Single(signal => signal.Name == "SignedValue");
        Assert.True(signed.IsSigned);
        Assert.Equal(1, signed.MultiplexValue);
        Assert.Equal(0.5, signed.Factor);
        Assert.Equal(-10, signed.Offset);
        Assert.Equal("Signed value comment", signed.Comment);
        Assert.Equal("Vector__XXX, LOGGER", signed.Receiver);
    }

    [Fact]
    public void ParsesEscapedTextLongSymbolsAndMultilineValues()
    {
        const string dbc =
            """
            VERSION ""

            BO_ 2147483905 MsgShort: 8 ECU
             SG_ SigShort : 0|8@1+ (1,0) [0|255] "km\/h" Vector__XXX

            BA_ "SystemMessageLongSymbol" BO_ 2147483905 "Vehicle speed frame";
            BA_ "SystemSignalLongSymbol" SG_ 2147483905 SigShort "Vehicle speed actual";
            CM_ BO_ 2147483905 "Frame comment with \"quotes\"";
            CM_ SG_ 2147483905 SigShort "Signal comment
            second row";
            VAL_ 2147483905 SigShort
              0 "Stopped"
              1 "Moving \"slow\""
              2 "Moving fast";
            """;

        var database = new DbcParser().Parse("attributes", new StringReader(dbc));

        var message = Assert.Single(database.Messages.Values);
        Assert.True(message.IsExtended);
        Assert.Equal(257u, message.Identifier);
        Assert.Contains("Vehicle speed frame", message.Comment);
        Assert.Contains("Frame comment with \"quotes\"", message.Comment);

        var signal = Assert.Single(message.Signals);
        Assert.Equal("km\\/h", signal.Unit);
        Assert.Contains("Vehicle speed actual", signal.Comment);
        Assert.Contains("second row", signal.Comment);
        Assert.Equal("Moving \"slow\"", signal.GetValueDescription(1));
    }

    [Fact]
    public void DecodesSignalValueTypeFloat32()
    {
        const string dbc =
            """
            VERSION ""

            BO_ 768 123Message: 8 ECU
             SG_ 1FloatSignal : 0|32@1+ (1,0) [-1000|1000] "bar" Vector__XXX
            SIG_VALTYPE_ 768 1FloatSignal : 1;
            """;

        var database = new DbcParser().Parse("float", new StringReader(dbc));
        var payload = new byte[8];
        BitConverter.GetBytes(12.5f).CopyTo(payload, 0);

        var values = new DbcDecoder(database).Decode(new CanFrame(0, 768, payload, 0));

        var value = Assert.Single(values);
        Assert.Equal(DbcSignalValueType.Float32, database.Messages[768].Signals[0].ValueType);
        Assert.Equal(12.5, value.PhysicalValue, precision: 3);
    }
}
