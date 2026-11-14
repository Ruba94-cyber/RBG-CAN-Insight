using System.IO.Compression;
using System.Xml.Linq;
using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class OdxInheritanceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "rbg-odx-tests-" + Guid.NewGuid().ToString("N"));

    public OdxInheritanceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    /// <summary>Protocol (VIN, 0xF18C) -> base variant (adds 0xF191, overrides nothing) -> two ECU variants.
    /// ECM overrides Read_VIN under the same short name and excludes Read_Serial; ABS inherits everything
    /// and adds its own 0xF405.</summary>
    private const string Layered = """
        <ODX>
          <REQUEST ID="REQ_Read_VIN"><PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM><PARAM SEMANTIC="ID"><CODED-VALUE>61840</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM></REQUEST>
          <REQUEST ID="REQ_Read_Serial"><PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM><PARAM SEMANTIC="ID"><CODED-VALUE>61836</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM></REQUEST>
          <REQUEST ID="REQ_Read_Base"><PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM><PARAM SEMANTIC="ID"><CODED-VALUE>61841</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM></REQUEST>
          <REQUEST ID="REQ_Read_VIN_ECM"><PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM><PARAM SEMANTIC="ID"><CODED-VALUE>61952</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM></REQUEST>
          <REQUEST ID="REQ_Read_Wheel"><PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM><PARAM SEMANTIC="ID"><CODED-VALUE>62469</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM></REQUEST>

          <PROTOCOL ID="PROT"><SHORT-NAME>UDS_on_CAN</SHORT-NAME>
            <DIAG-COMMS>
              <DIAG-SERVICE ID="S1"><SHORT-NAME>Read_VIN</SHORT-NAME><LONG-NAME>VIN from protocol</LONG-NAME><REQUEST-REF ID-REF="REQ_Read_VIN" /></DIAG-SERVICE>
              <DIAG-SERVICE ID="S2"><SHORT-NAME>Read_Serial</SHORT-NAME><LONG-NAME>Serial number</LONG-NAME><REQUEST-REF ID-REF="REQ_Read_Serial" /></DIAG-SERVICE>
            </DIAG-COMMS>
          </PROTOCOL>
          <BASE-VARIANT ID="BASE"><SHORT-NAME>Powertrain_Base</SHORT-NAME>
            <DIAG-COMMS>
              <DIAG-SERVICE ID="S3"><SHORT-NAME>Read_Base</SHORT-NAME><LONG-NAME>Base data</LONG-NAME><REQUEST-REF ID-REF="REQ_Read_Base" /></DIAG-SERVICE>
            </DIAG-COMMS>
            <PARENT-REFS><PARENT-REF ID-REF="PROT" /></PARENT-REFS>
          </BASE-VARIANT>
          <ECU-VARIANT ID="ECM"><SHORT-NAME>ECM_Variant</SHORT-NAME>
            <DIAG-COMMS>
              <DIAG-SERVICE ID="S4"><SHORT-NAME>Read_VIN</SHORT-NAME><LONG-NAME>VIN from ECM</LONG-NAME><REQUEST-REF ID-REF="REQ_Read_VIN_ECM" /></DIAG-SERVICE>
            </DIAG-COMMS>
            <PARENT-REFS>
              <PARENT-REF ID-REF="BASE">
                <NOT-INHERITED-DIAG-COMMS><NOT-INHERITED-DIAG-COMM><DIAG-COMM-SNREF SHORT-NAME="Read_Serial" /></NOT-INHERITED-DIAG-COMM></NOT-INHERITED-DIAG-COMMS>
              </PARENT-REF>
            </PARENT-REFS>
          </ECU-VARIANT>
          <ECU-VARIANT ID="ABS"><SHORT-NAME>ABS_Variant</SHORT-NAME>
            <DIAG-COMMS>
              <DIAG-SERVICE ID="S5"><SHORT-NAME>Read_Wheel</SHORT-NAME><LONG-NAME>Wheel speed</LONG-NAME><REQUEST-REF ID-REF="REQ_Read_Wheel" /></DIAG-SERVICE>
            </DIAG-COMMS>
            <PARENT-REFS><PARENT-REF ID-REF="BASE" /></PARENT-REFS>
          </ECU-VARIANT>
        </ODX>
        """;

    private static int[] Dids(DidCatalog catalog) => catalog.Parameters.Select(p => p.Did).Order().ToArray();

    [Fact]
    public void EachEcuVariantGetsItsEffectiveServicesThroughTheLayerChain()
    {
        var catalogs = OdxCatalogReader.LoadFromDocuments([XDocument.Parse(Layered)], "layered.odx-d");
        Assert.Equal(["ECM_Variant", "ABS_Variant"], catalogs.Select(c => c.Name));

        var ecm = catalogs[0];
        // Own Read_VIN (0xF200) overrides the protocol's (0xF190); Read_Serial (0xF18C) is excluded;
        // Read_Base (0xF191) is inherited from the base variant.
        Assert.Equal([0xF191, 0xF200], Dids(ecm));
        Assert.DoesNotContain(ecm.Parameters, p => p.Did is 0xF190 or 0xF18C);
        Assert.Equal("VIN from ECM", ecm.Parameters.Single(p => p.Did == 0xF200).LongName);
        Assert.All(ecm.Parameters, p => Assert.Equal("ECM_Variant", p.Ecu));

        var abs = catalogs[1];
        // Inherits protocol and base services unchanged, plus its own.
        Assert.Equal([0xF18C, 0xF190, 0xF191, 0xF405], Dids(abs));
        Assert.All(abs.Parameters, p => Assert.Equal("ABS_Variant", p.Ecu));
    }

    [Fact]
    public void VariantListShowsTheInheritanceChain()
    {
        var abs = OdxCatalogReader.LoadFromDocuments([XDocument.Parse(Layered)], "layered.odx-d")[1];
        Assert.Equal(["UDS_on_CAN", "Powertrain_Base", "ABS_Variant"],
            abs.VariantDefinitions.Select(v => v.Name));
        Assert.Equal("PROTOCOL", abs.VariantDefinitions[0].Type);
        Assert.Equal("Effective variant", abs.VariantDefinitions[^1].Description);
    }

    [Fact]
    public void ServicesAreNotSharedAcrossVariants()
    {
        var catalogs = OdxCatalogReader.LoadFromDocuments([XDocument.Parse(Layered)], "layered.odx-d");
        Assert.DoesNotContain(catalogs[1].Parameters, p => p.Did == 0xF200);
        Assert.DoesNotContain(catalogs[0].Parameters, p => p.Did == 0xF405);
    }

    [Fact]
    public void CyclicInheritanceIsRejectedRatherThanPublishingPartialCatalog()
    {
        const string cyclic = """
            <ODX>
              <REQUEST ID="R"><PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM><PARAM SEMANTIC="ID"><CODED-VALUE>61840</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM></REQUEST>
              <BASE-VARIANT ID="A"><SHORT-NAME>A</SHORT-NAME><DIAG-COMMS><DIAG-SERVICE><SHORT-NAME>S</SHORT-NAME><REQUEST-REF ID-REF="R" /></DIAG-SERVICE></DIAG-COMMS><PARENT-REFS><PARENT-REF ID-REF="B" /></PARENT-REFS></BASE-VARIANT>
              <BASE-VARIANT ID="B"><SHORT-NAME>B</SHORT-NAME><PARENT-REFS><PARENT-REF ID-REF="A" /></PARENT-REFS></BASE-VARIANT>
            </ODX>
            """;
        var error = Assert.Throws<InvalidDataException>(() =>
            OdxCatalogReader.LoadFromDocuments([XDocument.Parse(cyclic)], "cyclic.odx-d"));
        Assert.Contains("Cyclic", error.Message);
    }

    [Fact]
    public void FlatFilesWithoutParentRefsStillProduceOneCatalogWithEveryService()
    {
        const string flat = """
            <ODX>
              <ECU-VARIANT><SHORT-NAME>ECM</SHORT-NAME></ECU-VARIANT>
              <REQUEST ID="R1"><PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM><PARAM SEMANTIC="ID"><CODED-VALUE>62469</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM></REQUEST>
              <DIAG-SERVICE><SHORT-NAME>Read_Temp</SHORT-NAME><REQUEST-REF ID-REF="R1" /></DIAG-SERVICE>
            </ODX>
            """;
        var catalog = Assert.Single(OdxCatalogReader.LoadFromDocuments([XDocument.Parse(flat)], "flat.odx"));
        Assert.Equal("ECM", catalog.Ecu);
        Assert.Equal([0xF405], Dids(catalog));
    }

    [Fact]
    public void ExcessivelyDeepAcyclicInheritanceIsRejectedBeforeUnboundedRecursion()
    {
        var root = new XElement("ODX");
        for (var i = 0; i < 70; i++)
        {
            var layer = new XElement("BASE-VARIANT",
                new XAttribute("ID", $"L{i}"),
                new XElement("SHORT-NAME", $"L{i}"));
            if (i > 0)
                layer.Add(new XElement("PARENT-REFS",
                    new XElement("PARENT-REF", new XAttribute("ID-REF", $"L{i - 1}"))));
            root.Add(layer);
        }

        var error = Assert.Throws<InvalidDataException>(() =>
            OdxCatalogReader.LoadFromDocuments([new XDocument(root)], "deep.odx-d"));
        Assert.Contains("inheritance exceeds", error.Message);
    }

    [Fact]
    public void DuplicateParentIdAcrossPdxDocumentsDoesNotSilentlySelectOneEcuBase()
    {
        var first = XDocument.Parse("""
            <ODX><BASE-VARIANT ID="BASE">
              <SHORT-NAME>Engine_Base</SHORT-NAME>
            </BASE-VARIANT></ODX>
            """);
        var second = XDocument.Parse("""
            <ODX><BASE-VARIANT ID="BASE">
              <SHORT-NAME>Brake_Base</SHORT-NAME>
            </BASE-VARIANT></ODX>
            """);
        var variant = XDocument.Parse("""
            <ODX><ECU-VARIANT ID="ECM">
              <SHORT-NAME>Engine</SHORT-NAME>
              <PARENT-REFS><PARENT-REF ID-REF="BASE" /></PARENT-REFS>
            </ECU-VARIANT></ODX>
            """);

        var error = Assert.Throws<InvalidDataException>(() =>
            OdxCatalogReader.LoadFromDocuments([first, second, variant], "ambiguous.pdx"));
        Assert.Contains("BASE", error.Message);
        Assert.Contains("ambiguous", error.Message);
    }

    [Fact]
    public void MissingParentIdDoesNotTurnAnIncompleteVariantIntoAFlatMergedCatalog()
    {
        // Before this guard, an unresolved parent was dropped, causing
        // unrelated global services to appear in a single ECU catalog.
        var document = XDocument.Parse("""
            <ODX>
              <BASE-VARIANT ID="UNRELATED"><SHORT-NAME>Other</SHORT-NAME></BASE-VARIANT>
              <ECU-VARIANT ID="ECM"><SHORT-NAME>Engine</SHORT-NAME>
                <PARENT-REFS><PARENT-REF ID-REF="MISSING" /></PARENT-REFS>
              </ECU-VARIANT>
            </ODX>
            """);

        var error = Assert.Throws<InvalidDataException>(() =>
            OdxCatalogReader.LoadFromDocuments([document], "partial.odx-d"));
        Assert.Contains("MISSING", error.Message);
        Assert.Contains("missing", error.Message);
    }

    [Fact]
    public void ReusedUnreferencedLayerIdDoesNotBlockIndependentVariants()
    {
        var document = XDocument.Parse("""
            <ODX>
              <BASE-VARIANT ID="SHARED"><SHORT-NAME>A</SHORT-NAME></BASE-VARIANT>
              <BASE-VARIANT ID="SHARED"><SHORT-NAME>B</SHORT-NAME></BASE-VARIANT>
              <ECU-VARIANT ID="ECM"><SHORT-NAME>Engine</SHORT-NAME></ECU-VARIANT>
              <ECU-VARIANT ID="ABS"><SHORT-NAME>Brakes</SHORT-NAME></ECU-VARIANT>
            </ODX>
            """);
        var catalogs = OdxCatalogReader.LoadFromDocuments([document], "separate.odx-d");
        Assert.Equal(["Engine", "Brakes"], catalogs.Select(catalog => catalog.Name));
        Assert.All(catalogs, catalog => Assert.Empty(catalog.Parameters));
    }

    [Fact]
    public void PdxPackageIsReadAcrossItsMembersAndIgnoresNonLayerFiles()
    {
        // Layers split over two files: the protocol/base in one, the ECU variants in another.
        var doc = XDocument.Parse(Layered);
        var protocolAndBase = new XElement("ODX", doc.Root!.Elements()
            .Where(e => e.Name.LocalName is "REQUEST" || e.Attribute("ID")?.Value is "PROT" or "BASE"));
        var variants = new XElement("ODX", doc.Root!.Elements()
            .Where(e => e.Attribute("ID")?.Value is "ECM" or "ABS"));
        var pdx = Path.Combine(_directory, "package.pdx");
        using (var archive = ZipFile.Open(pdx, ZipArchiveMode.Create))
        {
            void Add(string name, string content)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }

            Add("index.xml", "<CATALOG />");
            Add("layers.odx-d", protocolAndBase.ToString());
            Add("variants.odx-d", variants.ToString());
            Add("comparam.odx-c", "<ODX><COMPARAM-SPEC /></ODX>");
        }

        var catalogs = DidCatalog.LoadAll(pdx);
        Assert.Equal(["ECM_Variant", "ABS_Variant"], catalogs.Select(c => c.Name));
        Assert.Equal([0xF191, 0xF200], Dids(catalogs[0]));
        Assert.Contains(".pdx", DidCatalog.SupportedExtensions);
        Assert.Contains("*.pdx", DidCatalog.FileDialogFilter);
    }

    [Fact]
    public void PdxWithoutDiagnosticLayersIsRejectedClearly()
    {
        var pdx = Path.Combine(_directory, "empty.pdx");
        using (var archive = ZipFile.Open(pdx, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("index.xml").Open());
            writer.Write("<CATALOG />");
        }

        var error = Assert.Throws<InvalidDataException>(() => DidCatalog.LoadAll(pdx));
        Assert.Contains("no .odx-d", error.Message);
    }
}
