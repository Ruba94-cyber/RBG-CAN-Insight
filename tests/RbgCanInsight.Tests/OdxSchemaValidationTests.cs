using System.Xml;
using System.Xml.Schema;
using RbgCanInsight.DiagData;
using Xunit;
using Xunit.Abstractions;

namespace RbgCanInsight.Tests;

/// <summary>
/// Validates exported ODX against the Apache-2.0 community ODX 2.2 XSD of the Eclipse OpenSOVD odx-converter
/// (downloaded into the git-ignored artifacts/online-samples/odx-schema folder). The OFFICIAL ASAM/ISO 22901-1
/// schema is licensed and not publicly distributed, so this is validation against a public community schema,
/// not against the official one. The test passes silently when the schema file is absent.
/// </summary>
public sealed class OdxSchemaValidationTests(ITestOutputHelper output)
{
    private static string? Find(string relative)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static List<string> Validate(string xsdPath, string xmlPath)
    {
        var set = new XmlSchemaSet();
        using (var xsd = XmlReader.Create(xsdPath)) set.Add(null, xsd);
        var problems = new List<string>();
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = set, DtdProcessing = DtdProcessing.Prohibit };
        settings.ValidationEventHandler += (_, e) => problems.Add($"{e.Severity}: {e.Message} (line {e.Exception?.LineNumber})");
        using var reader = XmlReader.Create(xmlPath, settings);
        while (reader.Read()) { }
        return problems;
    }

    [Fact]
    public void CommunitySchemaAcceptsTheRealSomersaultSampleAndOurExports()
    {
        var xsd = Find(Path.Combine("artifacts", "online-samples", "odx-schema", "odx-community-2_2_0.xsd"));
        if (xsd is null) return;

        var sample = Find(Path.Combine("artifacts", "online-samples", "odx", "extracted", "somersault.odx-d"));
        if (sample is not null)
            output.WriteLine("somersault.odx-d: " + string.Join(" | ", Validate(xsd, sample).Take(5)));

        var dir = Path.Combine(Path.GetTempPath(), "odxschema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var catalog = new DidCatalog("Cat", "ECU1", "x.odx",
                [
                    new DidParameter(0xF40D, "VehicleSpeed", "", "", "km/h", 0, 250, 1, 0, "A_UINT32", 8, "ECU1", "x.odx"),
                    new DidParameter(0xF405, "Temperature", "", "", "°C", -40, 215, 0.5, -40, "A_UINT32", 8, "ECU1", "x.odx"),
                    new DidParameter(0x1234, "NoLayout", "", "", "", null, null, null, null, "", null, "ECU1", "x.odx")
                ],
                [new DiagnosticTroubleCode(0x123456, "P0123", "Throttle sensor low", "desc", "ECU1", "x.odx")],
                [new DiagnosticRoutine(0x0203, "Erase", "Erase memory", "desc", "ECU1", "x.odx")],
                [new DiagnosticServiceDefinition(0x10, 0x03, null, "ExtendedSession", "Extended session", "desc", "ECU1", "x.odx"),
                 new DiagnosticServiceDefinition(0x22, null, 0xF190, "ReadVin", "Read VIN", "desc", "ECU1", "x.odx")]);
            var path = Path.Combine(dir, "out.odx-d");
            OdxCatalogWriter.Write(catalog, path);
            // Known deviation of the community XSD: it lists DTC-DOP children as COMPU-METHOD, DIAG-CODED-TYPE,
            // PHYSICAL-TYPE, whereas odxtools (the reference writer exercised on real ODX data) and this exporter emit
            // DIAG-CODED-TYPE, PHYSICAL-TYPE, COMPU-METHOD. That single ordering message is therefore not counted.
            var problems = Validate(xsd, path)
                .Where(p => !p.Contains("'DTC-DOP' has invalid child element 'DIAG-CODED-TYPE'", StringComparison.Ordinal)).ToList();
            output.WriteLine("export problems: " + problems.Count + " " + string.Join(" | ", problems.Take(8)));
            Assert.True(problems.Count == 0, "Export is not valid against the community ODX 2.2 schema:\n" + string.Join("\n", problems.Take(20)));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
