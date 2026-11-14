using RbgCanInsight.Correlation;
using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class MappingDefinitionFingerprintTests
{
    private static DidParameter Did(string source = @"C:\\project\\catalog.odx") =>
        new(0xF190, "VehicleSpeed", "Vehicle speed", "Recorded speed",
            "km/h", 0, 250, 0.01, 0, "A_UINT16", 16, "VCU", source,
            [new DiagnosticFieldDefinition("Speed", "Speed", "Wheel-based", 0, 16,
                "A_UINT16", "km/h", 0, 250, 0.01, 0, false, "LITTLE")]);

    private static (DbcDatabase Db, DbcMessage Message, DbcSignal Signal) Dbc() 
    {
        var db = new DbcParser().Parse("network", new StringReader(
            "BO_ 256 Vehicle: 8 VCU\n SG_ Speed : 0|16@1+ (0.01,0) [0|250] \"km/h\" VCU\n"));
        var msg = db.Messages[256];
        return (db, msg, msg.Signals[0]);
    }

    [Fact]
    public void DidFingerprintSurvivesMovingTheSameCatalogBetweenMachines()
    {
        var first = MappingDefinitionFingerprint.ForDid(Did());
        var moved = MappingDefinitionFingerprint.ForDid(Did("/opt/reports/catalog.odx"));
        Assert.Equal(first, moved);
        Assert.StartsWith("sem-v1:", first);
        Assert.Equal(71, first.Length);
    }

    [Fact]
    public void DidScalarMetadataAndFieldsInvalidateThePreviousReview()
    {
        var did = Did();
        var initial = MappingDefinitionFingerprint.ForDid(did);
        Assert.NotEqual(initial, MappingDefinitionFingerprint.ForDid(did with { Factor = 0.02 }));
        Assert.NotEqual(initial, MappingDefinitionFingerprint.ForDid(did with { Ecu = "BCM" }));
        Assert.NotEqual(initial, MappingDefinitionFingerprint.ForDid(did with { 
            FieldDefinitions = [did.Fields[0] with { BitOffset = 8 }] }));
        Assert.NotEqual(initial, MappingDefinitionFingerprint.ForDid(did with {
            DefinitionIssues = ["Ambiguous response layout"] }));
    }

    [Fact]
    public void SignalFingerprintDistinguishesFrameFormatBitLayoutAndScale()
    {
        var (db, message, signal) = Dbc();
        var initial = MappingDefinitionFingerprint.ForSignal(db.Name, message, signal);
        Assert.Equal(initial, MappingDefinitionFingerprint.ForSignal(db.Name, message, signal));
        Assert.NotEqual(initial, MappingDefinitionFingerprint.ForSignal(
            db.Name, message with { IsExtended = true }, signal));
        Assert.NotEqual(initial, MappingDefinitionFingerprint.ForSignal(
            db.Name, message, signal with { StartBit = 8 }));
        Assert.NotEqual(initial, MappingDefinitionFingerprint.ForSignal(
            db.Name, message, signal with { Factor = 0.02 }));
        Assert.NotEqual(initial, MappingDefinitionFingerprint.ForSignal(
            db.Name, message, signal with { ByteOrder = DbcByteOrder.Motorola }));
    }

    [Fact]
    public void DbcValueMeaningOrderDoesNotChangeTheFingerprint()
    {
        var (db, message, signal) = Dbc();
        var first = signal with { ValueDescriptions = new Dictionary<long, string> {
            [7] = "invalid", [0] = "idle" } };
        var second = signal with { ValueDescriptions = new Dictionary<long, string> {
            [0] = "idle", [7] = "invalid" } };
        Assert.Equal(MappingDefinitionFingerprint.ForSignal(db.Name, message, first),
            MappingDefinitionFingerprint.ForSignal(db.Name, message, second));
        Assert.NotEqual(MappingDefinitionFingerprint.ForSignal(db.Name, message, first),
            MappingDefinitionFingerprint.ForSignal(db.Name, message, second with {
                ValueDescriptions = new Dictionary<long, string> {
                    [0] = "idle", [7] = "fault" }
            }));
    }
}
