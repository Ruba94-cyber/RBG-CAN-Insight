using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DiagnosticCatalogEditorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rbg-editor-" + Guid.NewGuid().ToString("N"));

    public DiagnosticCatalogEditorTests() => Directory.CreateDirectory(_folder);
    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
        return path;
    }

    private const string Cdd = """
        <?xml version="1.0" encoding="utf-8"?>
        <!DOCTYPE CANDELA SYSTEM "https://invalid.example/candela.dtd">
        <CANDELA>
          <ECU qual="DemoEcu">
            <DIAGCLASS>
              <VENDOREXT vendor="keep-me" flag='single-quoted'><DEEP>unknown vendor content</DEEP></VENDOREXT>
              <SERVICE>
                <NAME><TUV xml:lang="en-US">Engine coolant temperature</TUV></NAME>
                <DESC><TUV xml:lang="en-US">Old description</TUV></DESC>
                <SHORTCUTQUAL>EngineCoolantTemperature_Read</SHORTCUTQUAL>
                <SEMANTIC>CURRENTDATA</SEMANTIC>
              </SERVICE>
              <STATICVALUE v="62469" />
              <SERVICE>
                <NAME><TUV xml:lang="en-US">Write configuration</TUV></NAME>
                <SHORTCUTQUAL>Configuration_Write</SHORTCUTQUAL>
                <SEMANTIC>VARIANTCODINGWRITE</SEMANTIC>
              </SERVICE>
              <STATICVALUE v="62470" />
            </DIAGCLASS>
          </ECU>
        </CANDELA>
        """;

    private const string Odx = """
        <ODX>
          <DIAG-LAYER-CONTAINER><SHORT-NAME>ECM</SHORT-NAME></DIAG-LAYER-CONTAINER>
          <ECU-VARIANT><SHORT-NAME>ECM</SHORT-NAME></ECU-VARIANT>
          <REQUEST ID="REQ_A">
            <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
            <PARAM SEMANTIC="ID"><CODED-VALUE>62469</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
          </REQUEST>
          <REQUEST ID="REQ_B">
            <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
            <PARAM SEMANTIC="ID"><CODED-VALUE>61840</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
          </REQUEST>
          <POS-RESPONSE ID="RESP_A"><PARAM SEMANTIC="DATA"><BYTE-POSITION>3</BYTE-POSITION><DOP-REF ID-REF="DOP_A" /></PARAM></POS-RESPONSE>
          <DATA-OBJECT-PROP ID="DOP_A"><SHORT-NAME>Temp</SHORT-NAME>
            <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT8"><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></DATA-OBJECT-PROP>
          <DIAG-SERVICE>
            <SHORT-NAME>Read_Temp</SHORT-NAME><LONG-NAME>Engine coolant temperature</LONG-NAME>
            <DESC><p>Old description</p></DESC>
            <REQUEST-REF ID-REF="REQ_A" /><POS-RESPONSE-REF ID-REF="RESP_A" />
          </DIAG-SERVICE>
          <DIAG-SERVICE>
            <SHORT-NAME>Read_Vin</SHORT-NAME>
            <REQUEST-REF ID-REF="REQ_B" />
          </DIAG-SERVICE>
        </ODX>
        """;

    [Fact]
    public void CddNameAndDescriptionAreEditedWhileEverythingElseIsPreserved()
    {
        var source = Write("demo.cdd", Cdd);
        var original = File.ReadAllBytes(source);
        var destination = Path.Combine(_folder, "demo-edited.cdd");

        var result = DiagnosticCatalogEditor.SaveCopy(source, destination, [new DiagnosticTextEdit(
            62469, "Engine coolant temperature", "Old description", "Coolant temperature", "New description")]);

        Assert.True(result.Saved);
        Assert.Equal(original, File.ReadAllBytes(source));                              // source untouched
        var text = File.ReadAllText(destination);
        Assert.Contains("unknown vendor content", text);                                // unknown vendor element kept
        Assert.Contains("vendor=\"keep-me\"", text);
        Assert.Contains("<!DOCTYPE CANDELA SYSTEM \"https://invalid.example/candela.dtd\">", text);
        Assert.Contains("xml:lang=\"en-US\">Coolant temperature<", text);
        var edited = DidCatalog.Load(destination);
        var did = edited.Parameters.Single(p => p.Did == 62469);
        Assert.Equal("Coolant temperature", did.LongName);
        Assert.Equal("New description", did.Description);
        Assert.Equal("Write configuration", edited.Parameters.Single(p => p.Did == 62470).LongName);   // other DID unchanged
    }

    [Fact]
    public void OdxMissingLongNameAndDescriptionAreCreatedInSchemaOrder()
    {
        var source = Write("ecm.odx", Odx);
        var destination = Path.Combine(_folder, "ecm-edited.odx");

        DiagnosticCatalogEditor.SaveCopy(source, destination, [new DiagnosticTextEdit(
            61840, string.Empty, string.Empty, "Vehicle identification number", "VIN as stored in the ECU")]);

        var text = File.ReadAllText(destination);
        Assert.Matches(@"<SHORT-NAME>Read_Vin</SHORT-NAME><LONG-NAME>Vehicle identification number</LONG-NAME><DESC><p>VIN as stored in the ECU</p></DESC>", text);
        var edited = DidCatalog.Load(destination);
        Assert.Equal("Vehicle identification number", edited.Parameters.Single(p => p.Did == 61840).LongName);
        Assert.Equal("Engine coolant temperature", edited.Parameters.Single(p => p.Did == 62469).LongName);
    }

    [Fact]
    public void EditingExistingOdxTextKeepsTheOtherDefinitionsIdentical()
    {
        var source = Write("ecm.odx", Odx);
        var destination = Path.Combine(_folder, "ecm2.odx");
        DiagnosticCatalogEditor.SaveCopy(source, destination, [new DiagnosticTextEdit(
            62469, "Engine coolant temperature", "Old description", "Coolant temp", "Updated")]);
        var report = DiagnosticDefinitionComparer.Compare(DidCatalog.LoadAll(source), DidCatalog.LoadAll(source));
        Assert.Equal(0, report.Changed);
        var edited = DidCatalog.Load(destination).Parameters.Single(p => p.Did == 62469);
        Assert.Equal("Coolant temp", edited.LongName);
        Assert.Equal("Updated", edited.Description);
    }

    [Fact]
    public void RefusesAmbiguousStaleOrUnsafeRequestsWithoutWritingAnything()
    {
        var source = Write("demo.cdd", Cdd);
        var destination = Path.Combine(_folder, "out.cdd");
        // Stale expectation (the file no longer says this) -> not found.
        Assert.Throws<InvalidDataException>(() => DiagnosticCatalogEditor.SaveCopy(source, destination,
            [new DiagnosticTextEdit(62469, "Something else", "Old description", "x", "y")]));
        // CDD element creation is not supported (order unverified): the write service has no DESC.
        Assert.Throws<InvalidDataException>(() => DiagnosticCatalogEditor.SaveCopy(source, destination,
            [new DiagnosticTextEdit(62470, "Write configuration", "", "Write configuration", "new desc")]));
        Assert.False(File.Exists(destination));
        Assert.Throws<IOException>(() => DiagnosticCatalogEditor.SaveCopy(source, source,
            [new DiagnosticTextEdit(62469, "Engine coolant temperature", "Old description", "a", "b")]));
        Assert.Throws<NotSupportedException>(() => DiagnosticCatalogEditor.SaveCopy(source, destination + ".odx",
            [new DiagnosticTextEdit(62469, "Engine coolant temperature", "Old description", "a", "b")]));
        Assert.Empty(Directory.GetFiles(_folder, "*.partial-*"));
    }

    [Fact]
    public void PdxEntityDeclaringAndMalformedFilesAreReportedAsNotEditable()
    {
        Assert.False(DiagnosticCatalogEditor.CanEdit(Path.Combine(_folder, "x.pdx"), out var pdx));
        Assert.Contains("PDX", pdx);
        var entity = Write("entity.cdd", "<?xml version=\"1.0\"?><!DOCTYPE CANDELA [<!ENTITY e \"text\">]><CANDELA/>");
        Assert.False(DiagnosticCatalogEditor.CanEdit(entity, out var entityReason));
        Assert.Contains("entities", entityReason);
        var broken = Write("broken.odx", "<ODX><unclosed></ODX>");
        Assert.False(DiagnosticCatalogEditor.CanEdit(broken, out var brokenReason));
        Assert.Contains("well-formed", brokenReason);
        Assert.True(DiagnosticCatalogEditor.CanEdit(Write("ok.cdd", Cdd), out _));
    }

    [Fact]
    public void ExistingDestinationIsNeverOverwritten()
    {
        var source = Write("demo.cdd", Cdd);
        var existing = Write("taken.cdd", "keep");
        Assert.Throws<IOException>(() => DiagnosticCatalogEditor.SaveCopy(source, existing,
            [new DiagnosticTextEdit(62469, "Engine coolant temperature", "Old description", "a", "b")]));
        Assert.Equal("keep", File.ReadAllText(existing));
    }
}
