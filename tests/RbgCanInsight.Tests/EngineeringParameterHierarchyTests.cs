using RbgCanInsight.DiagData;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class EngineeringParameterHierarchyTests
{
    [Fact]
    public void CddDiagramLikeDiaginstExportsReferencedEngineeringParAndEverySubparameter()
    {
        // Real-world CANdela files may declare candela.dtd; reading must not
        // fetch it. A DTD entity reference is still rejected as an error.
        const string cdd = """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE CANDELA SYSTEM "https://invalid.example/candela.dtd">
            <CANDELA><ECU qual="BodyEcu"/><ECUDOC>
              <DATATYPES>
                <IDENT id="t.speed"><QUAL>SpeedType</QUAL>
                  <CVALUETYPE bl="16"/></IDENT>
                <STRUCT id="t.packet"><QUAL>PacketType</QUAL>
                  <DATAOBJ id="p.speed" par="0x0A" dtref="t.speed"><QUAL>VehicleSpeed</QUAL>
                    <NAME><TUV xml:lang="en-US">Vehicle speed</TUV></NAME>
                    <DESC><TUV xml:lang="en-US">Encoded speed</TUV></DESC>
                  </DATAOBJ>
                  <STRUCT id="p.group"><QUAL>ModeGroup</QUAL>
                    <GAPDATAOBJ id="p.bit" bl="1"><QUAL>ModeFlag</QUAL>
                      <DESC><TUV xml:lang="en-US">One status bit</TUV></DESC>
                    </GAPDATAOBJ>
                  </STRUCT>
                </STRUCT>
              </DATATYPES>
              <DIAGCLASS><DIAGINST id="d1"><QUAL>EngineeringStatus</QUAL>
                <NAME><TUV xml:lang="en-US">Engineering status</TUV></NAME>
                <SERVICE><QUAL>Read</QUAL></SERVICE>
                <STATICVALUE v="0xF190"/>
                <SIMPLECOMPCONT>
                  <DATAOBJ id="par.master" dtref="t.packet"><QUAL>EngineeringPackage</QUAL>
                    <DESC><TUV xml:lang="en-US">Top-level package</TUV></DESC>
                  </DATAOBJ>
                </SIMPLECOMPCONT>
                <SIMPLECOMPCONT>
                  <SPECDATAOBJ id="negative" spec="rc"><QUAL>NRC</QUAL></SPECDATAOBJ>
                </SIMPLECOMPCONT>
              </DIAGINST></DIAGCLASS>
            </ECUDOC></CANDELA>
            """;
        var path = WriteTemp(".cdd", cdd);
        try
        {
            var did = Assert.Single(DidCatalog.Load(path).Parameters);
            Assert.Equal(0xF190, did.Did);
            Assert.Equal("EngineeringStatus", did.Name);
            Assert.Empty(did.Fields); // Defining PARs is not the same as decoding a DID.
            Assert.Equal(4, did.EngineeringParameters.Count);
            var master = Assert.Single(did.EngineeringParameters.Where(p => p.Name == "EngineeringPackage"));
            Assert.Equal(0, master.Depth);
            Assert.Equal("par.master", master.SourceId);
            Assert.Equal("Top-level package", master.Description);
            var speed = Assert.Single(did.EngineeringParameters.Where(p => p.Name == "VehicleSpeed"));
            Assert.Equal(master.Path, speed.ParentPath);
            Assert.Equal(16, speed.DeclaredBitLength);
            Assert.Equal("Encoded speed", speed.Description);
            Assert.Equal("p.speed", speed.SourceId);
            Assert.Equal("0x0A", speed.DeclaredParCode);
            Assert.Equal("", master.DeclaredParCode); // XML ID is not a PAR code.
            var group = Assert.Single(did.EngineeringParameters.Where(p => p.Name == "ModeGroup"));
            var bit = Assert.Single(did.EngineeringParameters.Where(p => p.Name == "ModeFlag"));
            Assert.Equal(group.Path, bit.ParentPath);
            Assert.Equal("One status bit", bit.Description);
            Assert.DoesNotContain(did.EngineeringParameters, p => p.Name == "NRC");

            var sheet = CatalogDefinitionExport.CreateDiagnosticSheets(
                [DidCatalog.Load(path)]).Single(s => s.Name == "Engineering Parameters");
            Assert.Equal(4, sheet.Rows.Count);
            Assert.Contains(sheet.Rows, row =>
                row[2] == "0xF190" && row[4] == "EngineeringPackage" &&
                row[6] == "Vehicle speed" && row[7] == "Encoded speed" &&
                row[8] == "p.speed" && row[18] == "16" &&
                row[22] == "0x0A");
            Assert.Contains(sheet.Rows, row =>
                row[4] == "EngineeringPackage" && row[6] == "ModeFlag" &&
                row[7] == "One status bit" && row[13] == "2");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OdxNestedStructureReferencesAreRetainedWithoutInventingPhysicalOffsets()
    {
        const string odx = """
            <ODX><DIAG-LAYER-CONTAINER>
              <SHORT-NAME>Powertrain</SHORT-NAME>
              <BASE-VARIANT>
                <SHORT-NAME>Powertrain</SHORT-NAME>
                <DIAG-COMMS><DIAG-SERVICE>
                  <SHORT-NAME>Read_Engineering</SHORT-NAME>
                  <REQUEST-REF ID-REF="req"/>
                  <POS-RESPONSE-REFS><POS-RESPONSE-REF ID-REF="resp"/></POS-RESPONSE-REFS>
                </DIAG-SERVICE></DIAG-COMMS>
                <REQUESTS><REQUEST ID="req"><PARAMS>
                  <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE>
                    <DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
                  <PARAM SEMANTIC="ID"><CODED-VALUE>61840</CODED-VALUE>
                    <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
                </PARAMS></REQUEST></REQUESTS>
                <POS-RESPONSES><POS-RESPONSE ID="resp"><PARAMS>
                  <PARAM SEMANTIC="SERVICE-ID"><SHORT-NAME>SID</SHORT-NAME></PARAM>
                  <PARAM SEMANTIC="ID"><SHORT-NAME>DID</SHORT-NAME></PARAM>
                  <PARAM SEMANTIC="DATA"><SHORT-NAME>EngineeringData</SHORT-NAME>
                    <LONG-NAME>Engineering data</LONG-NAME>
                    <DOP-REF ID-REF="struct1"/>
                  </PARAM>
                </PARAMS></POS-RESPONSE></POS-RESPONSES>
                <DIAG-DATA-DICTIONARY-SPEC><STRUCTURES>
                  <STRUCTURE ID="struct1"><SHORT-NAME>Group</SHORT-NAME><PARAMS>
                    <PARAM SEMANTIC="ID"><SHORT-NAME>SubCode</SHORT-NAME>
                      <DESC><L-2>Internal group selector</L-2></DESC></PARAM>
                    <PARAM><SHORT-NAME>Temperature</SHORT-NAME>
                      <DOP-REF ID-REF="dop.temp"/></PARAM>
                  </PARAMS></STRUCTURE>
                </STRUCTURES><DATA-OBJECT-PROPS>
                  <DATA-OBJECT-PROP ID="dop.temp"><SHORT-NAME>Temp</SHORT-NAME>
                    <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32">
                      <BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
                  </DATA-OBJECT-PROP>
                </DATA-OBJECT-PROPS></DIAG-DATA-DICTIONARY-SPEC>
              </BASE-VARIANT>
            </DIAG-LAYER-CONTAINER></ODX>
            """;
        var path = WriteTemp(".odx", odx);
        try
        {
            var did = Assert.Single(DidCatalog.Load(path).Parameters);
            Assert.Equal(0xF190, did.Did);
            var nodes = did.EngineeringParameters;
            Assert.Equal(3, nodes.Count);
            var root = Assert.Single(nodes.Where(p => p.Name == "EngineeringData"));
            Assert.Equal(0, root.Depth);
            var selector = Assert.Single(nodes.Where(p => p.Name == "SubCode"));
            Assert.Equal(root.Path, selector.ParentPath);
            Assert.Equal("Internal group selector", selector.Description);
            Assert.Equal(root.Path, nodes.Single(p => p.Name == "Temperature").ParentPath);

            var rows = CatalogDefinitionExport.CreateDiagnosticSheets([DidCatalog.Load(path)])
                .Single(sheet => sheet.Name == "Engineering Parameters").Rows;
            Assert.Equal(3, rows.Count);
            Assert.Contains(rows, row => row[4] == "Engineering data" &&
                row[6] == "SubCode" && row[7] == "Internal group selector");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SameNamesInDifferentContainersReceiveDifferentHierarchyPaths()
    {
        const string cdd = """
            <CANDELA><ECU qual="E"><DIAGCLASS>
              <SERVICE><SHORTCUTQUAL>Test_Read</SHORTCUTQUAL><SEMANTIC>CURRENTDATA</SEMANTIC>
                <RESPONSE>
                  <STRUCT><QUAL>A</QUAL><DATAOBJ><QUAL>Status</QUAL></DATAOBJ></STRUCT>
                  <STRUCT><QUAL>B</QUAL><DATAOBJ><QUAL>Status</QUAL></DATAOBJ></STRUCT>
                </RESPONSE>
              </SERVICE><STATICVALUE v="0xF193"/>
            </DIAGCLASS></ECU></CANDELA>
            """;
        var path = WriteTemp(".cdd", cdd);
        try
        {
            var nodes = Assert.Single(DidCatalog.Load(path).Parameters).EngineeringParameters;
            Assert.Equal(4, nodes.Count);
            Assert.Equal(4, nodes.Select(node => node.Path).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(2, nodes.Count(node => node.Name == "Status"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EngineeringOnlyExportRetainsEveryNestedParAndFiltersBySubparameter()
    {
        var nested = new[]
        {
            new DiagnosticEngineeringParameter("Top", "Top", "Top description",
                "/1:Top", "", 0, "STRUCT", "par0", "", "", "", null),
            new DiagnosticEngineeringParameter("Child", "Child", "Child description",
                "/1:Top/1:Child", "/1:Top", 1, "DATAOBJ", "par1", "", "", "", null),
            new DiagnosticEngineeringParameter("Leaf", "Leaf", "Leaf description",
                "/1:Top/1:Child/1:Leaf", "/1:Top/1:Child", 2,
                "DATAOBJ", "par2", "", "", "", null)
        };
        var one = new DidParameter(0xF190, "Example", "Example", "", "",
            null, null, null, null, "", null, "ECU", "one.cdd",
            EngineeringParameterDefinitions: nested);
        var other = new DidParameter(0xF191, "Unrelated", "Unrelated", "",
            "", null, null, null, null, "", null, "ECU", "one.cdd",
            EngineeringParameterDefinitions:
                [new DiagnosticEngineeringParameter("Other", "Other", "",
                    "/1:Other", "", 0, "DATAOBJ", "other", "", "", "", null)]);
        var catalog = new DidCatalog("Example", "ECU", "one.cdd", [one, other]);

        var sheets = CatalogDefinitionExport.CreateDiagnosticSheets(
            [catalog], engineeringOnly: true);
        Assert.Equal(3, sheets.Count);
        Assert.Empty(sheets.Single(sheet => sheet.Name == "Engineering PAR Values").Rows);
        var onlySheet = sheets.Single(sheet => sheet.Name == "Engineering Parameters");
        Assert.Equal(4, onlySheet.Rows.Count);
        var coverage = sheets.Single(sheet => sheet.Name == "PAR Extraction Coverage");
        Assert.Equal(2, coverage.Rows.Count);
        Assert.Contains(coverage.Rows, row => row[2] == "0xF190" && row[4] == "3" &&
            row[6] == "0" && row[7].StartsWith("SOURCE EXTRACTED", StringComparison.Ordinal) &&
            row[9] == "0" && row[10] == "3" && row[11] == "3");
        Assert.Contains(coverage.Rows, row => row[2] == "0xF191" && row[4] == "1" &&
            row[9] == "1" && row[10] == "1");

        var leaf = Assert.Single(onlySheet.Rows.Where(row => row[9] == "Leaf"));
        Assert.Equal("0xF190", leaf[2]);
        Assert.Equal("Top", leaf[4]);
        Assert.Equal("Top description", leaf[5]);
        Assert.Equal("Leaf", leaf[6]);
        Assert.Equal("Leaf description", leaf[7]);
        Assert.Equal("par2", leaf[8]);
        Assert.Equal("2", leaf[13]);

        var filtered = CatalogDefinitionExport.CreateDiagnosticSheets(
            [catalog], "Leaf", engineeringOnly: true);
        var filteredParameters = filtered.Single(sheet => sheet.Name == "Engineering Parameters");
        Assert.Equal(3, filteredParameters.Rows.Count);
        Assert.All(filteredParameters.Rows, row => Assert.Equal("0xF190", row[2]));
        Assert.Single(filtered.Single(sheet => sheet.Name == "PAR Extraction Coverage").Rows);
    }

    [Fact]
    public void RecursiveCddTypesStopAtCyclesAndKeepAnExplicitWarning()
    {
        const string xml = """
            <CANDELA><ECU qual="Cycle"><DATATYPES>
              <STRUCT id="A"><DATAOBJ id="a.child" dtref="B">
                <QUAL>ChildB</QUAL></DATAOBJ></STRUCT>
              <STRUCT id="B"><DATAOBJ id="b.child" dtref="A">
                <QUAL>ChildA</QUAL></DATAOBJ></STRUCT>
            </DATATYPES><DIAGCLASS>
              <SERVICE><SHORTCUTQUAL>Cycle_Read</SHORTCUTQUAL>
                <SEMANTIC>CURRENTDATA</SEMANTIC>
                <RESPONSE><DATAOBJ id="root" dtref="A">
                  <QUAL>Root</QUAL></DATAOBJ></RESPONSE>
              </SERVICE><STATICVALUE v="61840"/>
            </DIAGCLASS></ECU></CANDELA>
            """;
        var path = WriteTemp(".cdd", xml);
        try
        {
            var nodes = Assert.Single(DidCatalog.Load(path).Parameters).EngineeringParameters;
            Assert.Equal(3, nodes.Count);
            Assert.Equal("Cyclic reference, expansion stopped", nodes[2].ReferenceStatus);
            Assert.Equal("ChildA", nodes[2].Name);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CddWriteOnlyDidEngineeringParametersAreExportedButRoutineControlsAreNotDids()
    {
        const string cdd = """
            <CANDELA><ECU qual="BodyEcu">
              <DIAGCLASS><QUAL>IDENTIFICATION</QUAL>
                <DIAGINST><QUAL>WriteOnlyCoding</QUAL>
                  <SERVICE><QUAL>Write</QUAL></SERVICE>
                  <STATICVALUE v="0xF193"/>
                  <SIMPLECOMPCONT><STRUCT id="group"><QUAL>CodingBlock</QUAL>
                    <DATAOBJ id="par1"><QUAL>OptionFlag</QUAL>
                      <DESC>Enables the option</DESC></DATAOBJ>
                  </STRUCT></SIMPLECOMPCONT>
                </DIAGINST>
              </DIAGCLASS>
              <DIAGCLASS><QUAL>ROUTINE_CONTROL</QUAL>
                <DIAGINST><QUAL>UnrelatedRoutine</QUAL>
                  <SERVICE><QUAL>Write</QUAL></SERVICE>
                  <STATICVALUE v="0xF194"/>
                  <SIMPLECOMPCONT><DATAOBJ><QUAL>NotDid</QUAL></DATAOBJ></SIMPLECOMPCONT>
                </DIAGINST>
              </DIAGCLASS>
            </ECU></CANDELA>
            """;
        var path = WriteTemp(".cdd", cdd);
        try
        {
            var catalog = DidCatalog.Load(path);
            var did = Assert.Single(catalog.Parameters);
            Assert.Equal(0xF193, did.Did);
            Assert.Equal("WriteOnlyCoding", did.Name);
            Assert.Equal(2, did.EngineeringParameters.Count);
            Assert.Equal("OptionFlag", did.EngineeringParameters[1].Name);
            Assert.DoesNotContain(catalog.Parameters, item => item.Did == 0xF194);

            var sheet = CatalogDefinitionExport.CreateDiagnosticSheets(
                [catalog], engineeringOnly: true).Single(item =>
                    item.Name == "Engineering Parameters");
            Assert.Equal(2, sheet.Rows.Count);
            Assert.All(sheet.Rows, row => Assert.Equal("0xF193", row[2]));
        }
        finally { File.Delete(path); }
    }

    private static string WriteTemp(string suffix, string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), "rbg-engineering-" +
            Guid.NewGuid().ToString("N") + suffix);
        File.WriteAllText(path, contents);
        return path;
    }
}
