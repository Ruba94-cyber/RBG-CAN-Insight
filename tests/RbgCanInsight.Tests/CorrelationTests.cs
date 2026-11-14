using RbgCanInsight.Correlation;
using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class CorrelationTests
{
    private static DbcDatabase BuildDatabase() =>
        new DbcParser().Parse("vehicle", new StringReader(
            """
            BO_ 256 EngineData: 8 ECM
             SG_ EngCoolantTemp : 0|8@1+ (1,-40) [-40|215] "degC" Vector__XXX
             SG_ EngSpd : 8|16@1+ (0.125,0) [0|8031.875] "rpm" Vector__XXX

            BO_ 512 VehicleData: 8 BCM
             SG_ VehSpd : 0|16@1+ (0.01,0) [0|655.35] "km/h" Vector__XXX
             SG_ AmbientAirTemp : 16|8@1+ (0.5,-40) [-40|87.5] "degC" Vector__XXX
            """));

    [Fact]
    public void NameSimilarityExpandsAutomotiveAbbreviations()
    {
        Assert.True(NameSimilarity.Compute("EngineCoolantTemperature", "EngCoolantTemp") > 0.7);
        Assert.True(NameSimilarity.Compute("VehicleSpeed", "VehSpd") > 0.7);
        Assert.True(NameSimilarity.Compute("VehicleSpeed", "EngCoolantTemp") < 0.3);
    }

    [Fact]
    public void RanksTheMatchingSignalFirst()
    {
        var did = new DidParameter(
            0xF405, "EngineCoolantTemperature", "Engine coolant temperature",
            "Current engine coolant temperature", "degC",
            Minimum: -40, Maximum: 215, Factor: 1, Offset: -40,
            DataType: "A_UINT32", BitLength: 8, Ecu: "ECM", SourceFile: "test.cdd");

        var candidates = DidSignalCorrelator.Correlate(did, [BuildDatabase()]);

        Assert.NotEmpty(candidates);
        Assert.Equal("EngCoolantTemp", candidates[0].Signal.Name);
        Assert.True(candidates[0].Confidence > 0.6);
        Assert.Contains(candidates[0].Reasons, reason => reason.Contains("unit match"));
    }

    [Fact]
    public void StructuredDidCorrelationUsesEachFieldsOwnMetadataAndSignalName()
    {
        var definitions = new[]
        {
            new DiagnosticFieldDefinition("CoolantTemperature",
                "Coolant temperature", "", 0, 8, "A_UINT8", "degC",
                -40, 215, 1, -40, false, "LITTLE"),
            new DiagnosticFieldDefinition("RailPressure",
                "Fuel rail pressure", "", 8, 16, "A_UINT16", "bar",
                0, 1000, 0.1, 0, false, "LITTLE")
        };
        var did = new DidParameter(0xF405, "AllSensors", "Combined sensors",
            "", "bar", null, null, null, null, "STRUCT", 24,
            "ECM", "powertrain.odx", FieldDefinitions: definitions);
        var database = new DbcParser().Parse("powertrain", new StringReader(
            """
            BO_ 256 EngineSensors: 8 ECM
             SG_ CoolantTemperature : 0|8@1+ (1,-40) [-40|215] "degC" ECM
             SG_ RailPressure : 8|16@1+ (0.1,0) [0|1000] "bar" ECM
            """));

        var coolant = DidSignalCorrelator.ScopeToRecordedField(
            did, "DID 0xF405/CoolantTemperature");
        var pressure = DidSignalCorrelator.ScopeToRecordedField(
            did, "DID 0xF405/RailPressure");

        Assert.NotNull(coolant);
        Assert.NotNull(pressure);
        Assert.Equal("degC", coolant.Unit);
        Assert.Equal(8, coolant.BitLength);
        Assert.Equal(-40, coolant.Offset);
        Assert.Equal("bar", pressure.Unit);
        Assert.Equal(16, pressure.BitLength);
        Assert.Equal(0.1, pressure.Factor);

        var matchedCoolant = DidSignalCorrelator.Correlate(coolant, [database]);
        var matchedPressure = DidSignalCorrelator.Correlate(pressure, [database]);
        Assert.Equal("CoolantTemperature", matchedCoolant[0].Signal.Name);
        Assert.Equal("RailPressure", matchedPressure[0].Signal.Name);
        Assert.All(matchedCoolant, candidate =>
            Assert.Equal("CoolantTemperature", candidate.Did.Name));
        Assert.All(matchedPressure, candidate =>
            Assert.Equal("RailPressure", candidate.Did.Name));
    }

    [Fact]
    public void AmbiguousOrUnknownOemFieldCannotReuseUnrelatedDidMetadata()
    {
        var duplicated = new DiagnosticFieldDefinition("Mode", "Mode",
            "", 0, 8, "A_UINT8", "bar", null, null, 1, 0, false, "LITTLE");
        var did = new DidParameter(0xF190, "Status", "Status", "", "degC",
            null, null, 1, 0, "STRUCT", 8, "ECM", "variant.odx",
            FieldDefinitions: [duplicated, duplicated with { Unit = "rpm" }]);

        Assert.Null(DidSignalCorrelator.ScopeToRecordedField(
            did, "DID 0xF190/Mode"));
        Assert.Null(DidSignalCorrelator.ScopeToRecordedField(
            did, "DID 0xF190/NotDeclared"));
        Assert.Null(DidSignalCorrelator.ScopeToRecordedField(
            did, "DID 0xF191/Mode"));
        Assert.Null(DidSignalCorrelator.ScopeToRecordedField(
            did, ""));
    }

    [Fact]
    public void UnrelatedDidGetsLowConfidence()
    {
        var did = new DidParameter(
            0xF190, "VehicleIdentificationNumber", "VIN", "17 character VIN", "",
            null, null, null, null, "A_ASCIISTRING", 136, "BCM", "test.cdd");

        var candidates = DidSignalCorrelator.Correlate(did, [BuildDatabase()]);

        Assert.True(candidates.Count == 0 || candidates[0].Confidence < 0.4);
    }

    [Fact]
    public void MappingDocumentRoundTrips()
    {
        var document = new MappingDocument(
            "Test map",
            DateTimeOffset.UtcNow,
            [
                new DidSignalMapping(
                    0xF405, "EngineCoolantTemperature", "ECM", "vehicle",
                    "EngineData", "EngCoolantTemp", 0.87, "unit match", MappingStatus.Confirmed)
            ]);
        var path = Path.Combine(Path.GetTempPath(), $"rbg-test-{Guid.NewGuid():N}{MappingDocument.FileExtension}");
        try
        {
            document.Save(path);
            var loaded = MappingDocument.Load(path);

            Assert.Single(loaded.Mappings);
            Assert.Equal(0xF405, loaded.Mappings[0].Did);
            Assert.Equal(MappingStatus.Confirmed, loaded.Mappings[0].Status);
        }
        finally
        {
            File.Delete(path);
        }
    }


    [Fact]
    public void MappingDocumentPersistsExactCanIdentityAndReadsLegacyEntries()
    {
        var filename = Path.Combine(Path.GetTempPath(), $"rbg-map-{Guid.NewGuid():N}.rbgmap.json");
        try
        {
            var modern = new DidSignalMapping(
                0xF405, "CoolantTemperature", "ECM",
                "vehicle", "Engine", "Temperature", 0.90,
                "tested", MappingStatus.Confirmed,
                "reviewed", MessageIdentifier: 0x18DAF110, IsExtended: true);
            var legacy = new DidSignalMapping(
                0xF405, "CoolantTemperature", "BCM",
                "vehicle", "Body", "Temperature", 0.50,
                "legacy", MappingStatus.ToVerify);
            new MappingDocument("Test", DateTimeOffset.UtcNow, [modern, legacy]).Save(filename);

            var loaded = MappingDocument.Load(filename);
            Assert.Equal(2, loaded.Mappings.Count);
            Assert.Equal(0x18DAF110u, loaded.Mappings[0].MessageIdentifier);
            Assert.True(loaded.Mappings[0].IsExtended);
            Assert.Null(loaded.Mappings[1].MessageIdentifier);
            Assert.Null(loaded.Mappings[1].IsExtended);
        }
        finally
        {
            File.Delete(filename);
        }
    }

    [Fact]
    public void IsoTpExtractorSummarizesPositiveDidResponses()
    {
        var did = new DidParameter(
            0xF405,
            "EngineCoolantTemperature",
            "Engine coolant temperature",
            "Current engine coolant temperature",
            "degC",
            -40,
            215,
            1,
            -40,
            "A_UINT8",
            8,
            "ECM",
            "test.cdd");
        var frames = new[]
        {
            new CanFrame(0, 0x18DAF110, [0x04, 0x62, 0xF4, 0x05, 0x5A], 0, CanFrameFlags.Extended),
            new CanFrame(0, 0x18DAF110, [0x04, 0x62, 0xF4, 0x05, 0x64], 1_000_000, CanFrameFlags.Extended)
        };

        var summaries = IsoTpDidExtractor.Summarize(frames, [did]);

        var summary = Assert.Single(summaries.Values);
        Assert.Equal(0xF405, summary.Did);
        Assert.Equal(2, summary.SampleCount);
        Assert.Equal(50, summary.MinValue);
        Assert.Equal(60, summary.MaxValue);
    }
}
