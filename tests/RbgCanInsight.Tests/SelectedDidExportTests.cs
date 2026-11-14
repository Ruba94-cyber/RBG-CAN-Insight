using RbgCanInsight.DiagData;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>A numeric DID is not globally unique across ECU/diagnostic files.</summary>
public sealed class SelectedDidExportTests
{
    [Fact]
    public void SelectedDidExportDoesNotMixEqualIdentifiersFromOtherEcusOrSources()
    {
        var catalogs = new[]
        {
            Catalog("BrakeEcu", "brakes.cdd", "BrakePressure"),
            Catalog("EngineEcu", "engine.cdd", "OilPressure"),
            Catalog("EngineEcu", "engine.cdd", "OtherOilPressure"),
            Catalog("EngineEcu", "engine-alt.cdd", "OtherOilPressure")
        };
        var selection = new DiagnosticDidSelection("engine.cdd", "EngineEcu", 0xF190, "OilPressure");

        var sheets = CatalogDefinitionExport.CreateDiagnosticSheets(catalogs, selectedDid: selection);
        var dids = sheets.Single(sheet => sheet.Name == "DIDs");
        var did = Assert.Single(dids.Rows);
        Assert.Equal("EngineEcu", did[1]);
        Assert.Equal("OilPressure", did[3]);
        Assert.Equal("0xF190", did[2]);

        var fields = Assert.Single(sheets.Single(sheet => sheet.Name == "DID Fields").Rows);
        Assert.Equal("EngineEcu", fields[1]);
        Assert.Equal("OilPressure", fields[3]);
        Assert.Equal("Pressure", fields[4]);

        var meaning = Assert.Single(sheets.Single(sheet => sheet.Name == "DID Values").Rows);
        Assert.Equal("Alert", meaning[6]);

        var engineering = Assert.Single(sheets.Single(
            sheet => sheet.Name == "Engineering Parameters").Rows);
        Assert.Equal("0xF190", engineering[2]);
        Assert.Equal("Pressure", engineering[4]);
        Assert.Equal("Structure not available from source", engineering[20]);

        var coverage = Assert.Single(sheets.Single(
            sheet => sheet.Name == "PAR Extraction Coverage").Rows);
        Assert.Equal("0xF190", coverage[2]);
        Assert.Equal("0", coverage[4]);
        Assert.Equal("1", coverage[5]);
        Assert.True(coverage[7].StartsWith("FALLBACK", StringComparison.Ordinal));

        Assert.All(sheets.Where(sheet => sheet.Name is not (
                "DIDs" or "DID Fields" or "DID Values" or
                "Engineering Parameters" or "PAR Extraction Coverage" or
                "Engineering PAR Values")),
            sheet => Assert.Empty(sheet.Rows));
    }

    [Fact]
    public void NonexistentSourceDidSelectionReturnsNoDataRatherThanAnotherEcu()
    {
        var sheets = CatalogDefinitionExport.CreateDiagnosticSheets(
            [Catalog("EngineEcu", "engine.cdd", "OilPressure")],
            selectedDid: new DiagnosticDidSelection("different.cdd", "EngineEcu", 0xF190));
        Assert.All(sheets, sheet => Assert.Empty(sheet.Rows));
    }

    private static DidCatalog Catalog(string ecu, string source, string didName)
    {
        var field = new DiagnosticFieldDefinition(
            "Pressure", "Pressure", "", -1, 8, "A_UINT32", "bar",
            null, null, null, null, false, "",
            [new DiagnosticEnumValue(1, "Alert")], SupportsPhysicalConversion: false);
        var parameter = new DidParameter(0xF190, didName, didName, "",
            "", null, null, null, null, "", null, ecu, source, [field]);
        return new DidCatalog("Example", ecu, source, [parameter],
            TroubleCodes: [new DiagnosticTroubleCode(0x123456, "Dtc", "Dtc", "", ecu, source)],
            Routines: [new DiagnosticRoutine(0x1001, "Routine", "Routine", "", ecu, source)],
            Services: [new DiagnosticServiceDefinition(0x22, null, 0xF190, "Read", "Read", "", ecu, source)],
            Connections: [new DiagnosticConnection("CAN", 0x700, 0x708, false,
                "11-bit physical", ecu, source)],
            Variants: [new DiagnosticVariant("Variant", "ECU", "")]);
    }
}
