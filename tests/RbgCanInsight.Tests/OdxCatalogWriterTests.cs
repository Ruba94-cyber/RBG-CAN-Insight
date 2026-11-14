using System.Xml.Linq;
using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class OdxCatalogWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "odxw-" + Guid.NewGuid().ToString("N"));

    public OdxCatalogWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private static DidParameter Did(int did, string name, string unit = "", double? factor = null,
        double? offset = null, int? bits = null, double? min = null, double? max = null,
        string dataType = "", string longName = "") =>
        new(did, name, longName, "", unit, min, max, factor, offset, dataType, bits, "ECU1", "x.odx");

    private static DidCatalog Catalog(IReadOnlyList<DidParameter> parameters,
        IReadOnlyList<DiagnosticTroubleCode>? dtcs = null,
        IReadOnlyList<DiagnosticRoutine>? routines = null,
        IReadOnlyList<DiagnosticServiceDefinition>? services = null,
        string ecu = "ECU1") =>
        new("Cat", ecu, "x.odx", parameters, dtcs, routines, services);

    private DidCatalog RoundTrip(DidCatalog catalog, string file = "out.odx-d")
    {
        var path = P(file);
        OdxCatalogWriter.Write(catalog, path);
        Assert.Empty(OdxCatalogWriter.Validate(path));
        return OdxCatalogReader.Load(path);
    }

    [Fact]
    public void RoundTrip_PreservesDidsNamesUnitsAndScaling()
    {
        var source = Catalog(
        [
            Did(0xF40D, "VehicleSpeed", "km/h", 1, 0, 8, 0, 250, "A_UINT32"),
            Did(0xF405, "Temperature", "°C", 0.5, -40, 8, -40, 215, "A_UINT32"),
            Did(0x1234, "NoLayout")
        ]);
        var loaded = RoundTrip(source);

        Assert.Equal([0x1234, 0xF405, 0xF40D], loaded.Parameters.Select(p => p.Did));
        var speed = loaded.Parameters.Single(p => p.Did == 0xF40D);
        Assert.Equal("VehicleSpeed", speed.Name);
        Assert.Equal("km/h", speed.Unit);
        Assert.Equal(1d, speed.Factor);
        Assert.Equal(0d, speed.Offset);
        Assert.Equal(8, speed.BitLength);
        Assert.Equal("A_UINT32", speed.DataType);
        Assert.Equal(0d, speed.Minimum);
        Assert.Equal(250d, speed.Maximum);

        var temp = loaded.Parameters.Single(p => p.Did == 0xF405);
        Assert.Equal("°C", temp.Unit);
        Assert.Equal(0.5, temp.Factor);
        Assert.Equal(-40d, temp.Offset);
        Assert.Equal(-40d, temp.Minimum);
        Assert.Equal(215d, temp.Maximum);

        var raw = loaded.Parameters.Single(p => p.Did == 0x1234);
        Assert.Equal("NoLayout", raw.Name);
        Assert.Equal("", raw.Unit);
        Assert.Null(raw.Factor);
        Assert.Null(raw.BitLength);

        Assert.Equal("ECU1", loaded.Ecu);
    }

    [Fact]
    public void UnknownLayoutIsFlaggedOnlyWhereTheCatalogIsSilent()
    {
        var path = P("flag.odx-d");
        OdxCatalogWriter.Write(Catalog([Did(1, "Known", bits: 16), Did(2, "Unknown")]), path);
        var text = File.ReadAllText(path);
        Assert.Equal(1, text.Split(OdxCatalogWriter.UnknownLayoutMarker).Length - 1);
        // Bit length known but data type not stated is flagged separately, never silent.
        Assert.Contains(OdxCatalogWriter.AssumedDataTypeMarker, text);
    }

    [Fact]
    public void ValuesTheCatalogDoesNotStateAreNotWritten()
    {
        var path = P("minimal.odx-d");
        OdxCatalogWriter.Write(Catalog([Did(0x10, "Plain", bits: 8, dataType: "A_UINT32")]), path);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("UNIT-REF", text);
        Assert.DoesNotContain("PHYS-CONSTR", text);
        Assert.DoesNotContain("LINEAR", text);
    }

    [Fact]
    public void DtcsRoutinesAndServicesRoundTrip()
    {
        var source = Catalog(
            [Did(0xF190, "Vin", bits: 8)],
            dtcs: [new DiagnosticTroubleCode(0x012345, "P0123", "Throttle low", "Desc", "ECU1", "x")],
            routines: [new DiagnosticRoutine(0x0203, "EraseMemory", "", "", "ECU1", "x")],
            services: [new DiagnosticServiceDefinition(0x10, 0x03, null, "SessionExtended", "", "", "ECU1", "x")]);
        var loaded = RoundTrip(source);

        var dtc = Assert.Single(loaded.DtcDefinitions);
        Assert.Equal(0x012345, dtc.Code);
        Assert.Equal("P0123", dtc.Name);
        Assert.Equal("Throttle low", dtc.DisplayText);
        Assert.Equal("Desc", dtc.Description);
        Assert.Equal(0x0203, Assert.Single(loaded.RoutineDefinitions).Identifier);
        Assert.Contains(loaded.ServiceDefinitions, s => s is { ServiceId: 0x10, SubFunction: 3 });
        Assert.Contains(loaded.ServiceDefinitions, s => s is { ServiceId: 0x22, Identifier: 0xF190 });
    }

    [Fact]
    public void SpecialCharactersAreEscapedAndIdsSanitised()
    {
        var weird = "A&B <x> \"q\" 'z' ü\u0001";
        var source = Catalog(
        [
            Did(1, weird, unit: "m/s<2>&"),
            Did(2, "a b"), Did(3, "a_b"), Did(4, "a-b"), Did(5, "9lives"), Did(6, "")
        ], ecu: "ECU <1> & co");
        var loaded = RoundTrip(source);

        Assert.Equal(6, loaded.Parameters.Count);
        Assert.Equal("A&B <x> \"q\" 'z' ü", loaded.Parameters[0].DisplayName);
        Assert.Equal("a b", loaded.Parameters[1].DisplayName);
        Assert.Equal("a_b", loaded.Parameters[2].DisplayName);
        Assert.Equal("a-b", loaded.Parameters[3].DisplayName);
        Assert.Equal("9lives", loaded.Parameters[4].DisplayName);
        Assert.Equal("ECU__1____co", loaded.Ecu);
    }

    [Fact]
    public void EmptyCatalogProducesValidDocumentWithNoServices()
    {
        var loaded = RoundTrip(Catalog([], ecu: ""));
        Assert.Empty(loaded.Parameters);
        Assert.Empty(loaded.ServiceDefinitions);
        Assert.Empty(loaded.DtcDefinitions);
    }

    [Fact]
    public void OutputIsDeterministicRegardlessOfInputOrder()
    {
        var a = Did(0x20, "B", "V", 0.1, 0, 16, dataType: "A_UINT32");
        var b = Did(0x10, "A", bits: 8, dataType: "A_UINT32");
        OdxCatalogWriter.Write(Catalog([a, b]), P("1.odx-d"));
        OdxCatalogWriter.Write(Catalog([b, a]), P("2.odx-d"));
        OdxCatalogWriter.Write(Catalog([a, b]), P("3.odx-d"));
        Assert.Equal(File.ReadAllBytes(P("1.odx-d")), File.ReadAllBytes(P("2.odx-d")));
        Assert.Equal(File.ReadAllBytes(P("1.odx-d")), File.ReadAllBytes(P("3.odx-d")));
    }

    [Fact]
    public void ValidateDetectsMalformedDuplicateAndDanglingReferences()
    {
        var good = P("good.odx-d");
        OdxCatalogWriter.Write(Catalog([Did(1, "One", bits: 8, dataType: "A_UINT32")]), good);
        var text = File.ReadAllText(good);

        File.WriteAllText(P("bad1.odx-d"), "<ODX><unclosed></ODX>");
        Assert.NotEmpty(OdxCatalogWriter.Validate(P("bad1.odx-d")));

        File.WriteAllText(P("bad2.odx-d"), text.Replace("RQ.One", "DS.One"));
        Assert.Contains(OdxCatalogWriter.Validate(P("bad2.odx-d")), i => i.Contains("Duplicate ID"));

        File.WriteAllText(P("bad3.odx-d"), text.Replace("ID-REF=\"PR.One\"", "ID-REF=\"PR.Missing\""));
        Assert.Contains(OdxCatalogWriter.Validate(P("bad3.odx-d")), i => i.Contains("PR.Missing"));

        File.WriteAllText(P("bad4.odx-d"), text.Replace("ID=\"RQ.One\"", "ID=\"1 bad\""));
        Assert.Contains(OdxCatalogWriter.Validate(P("bad4.odx-d")), i => i.Contains("NCName"));
    }

    [Fact]
    public void ContainsPositiveResponseServiceIdPlus0x40AndNegativeResponse()
    {
        var path = P("sid.odx-d");
        OdxCatalogWriter.Write(Catalog([Did(0xF190, "Vin", bits: 8)]), path);
        var doc = XDocument.Load(path);
        string[] values(string parent) => doc.Descendants(parent).Descendants("CODED-VALUE")
            .Select(e => e.Value).ToArray();
        Assert.Equal(["34", "61840"], values("REQUEST"));
        Assert.Equal(["98", "61840"], values("POS-RESPONSE"));
        Assert.Equal(["127"], values("NEG-RESPONSE"));
    }
}
