using RbgCanInsight.DiagData;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DiagnosticDidDefinitionMergeTests
{
    [Fact]
    public void CddReadAndWriteServicesForSameDidKeepBothEngineeringTrees()
    {
        const string xml = """
            <CANDELA><ECU qual="Engine"><DIAGCLASS>
              <SERVICE><SHORTCUTQUAL>Parameters_Read</SHORTCUTQUAL>
                <SEMANTIC>CURRENTDATA</SEMANTIC>
                <RESPONSE><DATAOBJ id="readPar"><QUAL>ReadStatus</QUAL>
                  <DESC>Read status data</DESC></DATAOBJ></RESPONSE>
              </SERVICE><STATICVALUE v="0xF190"/>
              <SERVICE><SHORTCUTQUAL>Parameters_Write</SHORTCUTQUAL>
                <SEMANTIC>VARIANTCODINGWRITE</SEMANTIC>
                <RESPONSE><DATAOBJ id="writePar"><QUAL>WriteOption</QUAL>
                  <DESC>Writable configuration option</DESC></DATAOBJ></RESPONSE>
              </SERVICE><STATICVALUE v="0xF190"/>
            </DIAGCLASS></ECU></CANDELA>
            """;
        var file = WriteTemp(".cdd", xml);
        try
        {
            var catalog = DidCatalog.Load(file);
            var did = Assert.Single(catalog.Parameters);
            Assert.Equal(0xF190, did.Did);
            Assert.Equal(2, did.EngineeringParameters.Count);
            Assert.Equal(2, did.EngineeringParameters.Select(node => node.Path)
                .Distinct(StringComparer.Ordinal).Count());
            Assert.Contains(did.EngineeringParameters, node =>
                node.Name == "ReadStatus" && node.DefinitionOrigin.Contains("Read"));
            Assert.Contains(did.EngineeringParameters, node =>
                node.Name == "WriteOption" && node.DefinitionOrigin.Contains("Write"));
            Assert.NotEmpty(did.EngineeringIssues);

            var sheets = CatalogDefinitionExport.CreateDiagnosticSheets(
                [catalog], engineeringOnly: true);
            var rows = sheets.Single(sheet => sheet.Name == "Engineering Parameters").Rows;
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, row => row[9] == "ReadStatus" &&
                row[21].Contains("Read", StringComparison.Ordinal));
            Assert.Contains(rows, row => row[9] == "WriteOption" &&
                row[21].Contains("Write", StringComparison.Ordinal));
            var coverage = Assert.Single(sheets.Single(
                sheet => sheet.Name == "PAR Extraction Coverage").Rows);
            Assert.StartsWith("PARTIAL", coverage[7]);
            Assert.Contains("Multiple engineering structures", coverage[8]);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void EquivalentDuplicateTreesDoNotProduceSpuriousRows()
    {
        var parameters = new Dictionary<int, DidParameter>();
        var node = new DiagnosticEngineeringParameter(
            "Status", "Status", "", "/1:Status", "", 0, "DATAOBJ",
            "node1", "", "", "", null);
        var did = new DidParameter(0xF190, "Status", "Status", "", "",
            null, null, null, null, "", null, "ECU", "file.cdd",
            EngineeringParameterDefinitions: [node]);

        DiagnosticDidDefinitionMerger.AddOrMerge(parameters, did, "CDD Read");
        DiagnosticDidDefinitionMerger.AddOrMerge(parameters, did, "CDD ReadAgain");

        var merged = Assert.Single(parameters.Values);
        Assert.Single(merged.EngineeringParameters);
        Assert.Empty(merged.EngineeringIssues);
        Assert.Contains("CDD Read", merged.EngineeringParameters[0].DefinitionOrigin);
        Assert.Contains("CDD ReadAgain", merged.EngineeringParameters[0].DefinitionOrigin);
    }

    [Fact]
    public void EqualReadWriteValueTablesAreNotDuplicatedByReferenceEquality()
    {
        var definitions = new Dictionary<int, DidParameter>();
        DidParameter Create(string originName)
        {
            var meanings = new[]
            {
                new DiagnosticDeclaredValueMeaning("0", "0", "Off", "", "Exact code"),
                new DiagnosticDeclaredValueMeaning("1", "7", "Reserved", "", "Declared range")
            };
            var node = new DiagnosticEngineeringParameter(
                "Mode", "Mode", "Source meaning", "/1:Mode", "", 0,
                "PARAM", "source-mode", "dop.mode", "A_UINT8", "",
                8, DeclaredValueDefinitions: meanings);
            return new DidParameter(0xF190, originName, originName, "",
                "", null, null, null, null, "", null,
                "ECU", "input.odx",
                EngineeringParameterDefinitions: [node]);
        }

        DiagnosticDidDefinitionMerger.AddOrMerge(definitions, Create("Read"), "SID 0x22");
        DiagnosticDidDefinitionMerger.AddOrMerge(definitions, Create("Write"), "SID 0x2E");
        var did = Assert.Single(definitions.Values);
        var node = Assert.Single(did.EngineeringParameters);
        Assert.Equal(2, node.DeclaredValues.Count);
        Assert.Contains("SID 0x22", node.DefinitionOrigin);
        Assert.Contains("SID 0x2E", node.DefinitionOrigin);
        Assert.Empty(did.EngineeringIssues);
    }

    [Fact]
    public void ThirdSourceMatchingOnePreviouslyScopedTreeDoesNotDuplicateSubparameters()
    {
        var definitions = new Dictionary<int, DidParameter>();
        static DiagnosticEngineeringParameter[] Tree(string root, string child) =>
        [
            new(root, root, "Root description", "/1:" + root, "", 0,
                "STRUCT", "root", "", "", "", null),
            new(child, child, "Nested description",
                "/1:" + root + "/1:" + child, "/1:" + root, 1,
                "DATAOBJ", "sub", "", "", "", null)
        ];
        DidParameter NewDid(DiagnosticEngineeringParameter[] nodes) =>
            new(0xF190, "Engineering", "Engineering", "",
                "", null, null, null, null, "", null, "ECU", "catalog.cdd",
                EngineeringParameterDefinitions: nodes);

        DiagnosticDidDefinitionMerger.AddOrMerge(definitions,
            NewDid(Tree("ReadGroup", "Status")), "SERVICE Read");
        DiagnosticDidDefinitionMerger.AddOrMerge(definitions,
            NewDid(Tree("WriteGroup", "Option")), "SERVICE Write A");
        DiagnosticDidDefinitionMerger.AddOrMerge(definitions,
            NewDid(Tree("WriteGroup", "Option")), "SERVICE Write B");
        DiagnosticDidDefinitionMerger.AddOrMerge(definitions,
            NewDid(Tree("ReadGroup", "Status")), "SERVICE Read Again");

        var did = Assert.Single(definitions.Values);
        Assert.Equal(4, did.EngineeringParameters.Count);
        Assert.Equal(4, did.EngineeringParameters.Select(node => node.Path)
            .Distinct(StringComparer.Ordinal).Count());
        Assert.All(did.EngineeringParameters.Where(node => node.Name is "WriteGroup" or "Option"),
            node =>
            {
                Assert.Contains("SERVICE Write A", node.DefinitionOrigin);
                Assert.Contains("SERVICE Write B", node.DefinitionOrigin);
            });
        Assert.All(did.EngineeringParameters.Where(node => node.Name is "ReadGroup" or "Status"),
            node =>
            {
                Assert.Contains("SERVICE Read", node.DefinitionOrigin);
                Assert.Contains("SERVICE Read Again", node.DefinitionOrigin);
            });
        Assert.Single(did.EngineeringIssues);
    }

    [Fact]
    public void ContradictoryPhysicalLayoutsAreNotSilentlySelected()
    {
        var parameters = new Dictionary<int, DidParameter>();
        DiagnosticFieldDefinition Field(string name, int bits) => new(
            name, name, "", 0, bits, "A_UINT32", "V", null, null,
            0.1, 0, false, "BIG_ENDIAN");
        var one = new DidParameter(0xF191, "Voltage", "Voltage", "", "V",
            null, null, 0.1, 0, "", 8, "ECU", "file.odx",
            FieldDefinitions: [Field("Voltage", 8)]);
        var two = one with
        {
            BitLength = 16,
            FieldDefinitions = [Field("Voltage", 16)]
        };

        DiagnosticDidDefinitionMerger.AddOrMerge(parameters, one, "ODX Read A");
        DiagnosticDidDefinitionMerger.AddOrMerge(parameters, two, "ODX Read B");
        // Regression: a third compatible definition must not restore the
        // previously disabled decoder or whole-DID scaling.
        DiagnosticDidDefinitionMerger.AddOrMerge(parameters, one, "ODX Read C");

        var merged = Assert.Single(parameters.Values);
        Assert.Empty(merged.Fields);
        Assert.Null(merged.BitLength);
        Assert.Null(merged.Factor);
        Assert.Contains(merged.EngineeringIssues,
            issue => issue.Contains("Conflicting physical field layouts"));
        Assert.Contains(merged.EngineeringIssues,
            issue => issue.Contains("whole-DID scalar"));
    }

    [Fact]
    public void ConflictingFieldStructureAlsoInvalidatesEqualTopLevelScales()
    {
        var definitions = new Dictionary<int, DidParameter>();
        DiagnosticFieldDefinition MakeField(int position) => new(
            "State", "State", "", position, 8, "A_UINT32", "V",
            null, null, 0.1, 0, false, "BIG_ENDIAN");
        var first = new DidParameter(0xF192, "State", "State", "", "V",
            null, null, 0.1, 0, "", 8, "ECU", "same.odx",
            FieldDefinitions: [MakeField(0)]);
        var second = first with { FieldDefinitions = [MakeField(8)] };

        DiagnosticDidDefinitionMerger.AddOrMerge(definitions, first, "First");
        DiagnosticDidDefinitionMerger.AddOrMerge(definitions, second, "Second");
        var merged = Assert.Single(definitions.Values);
        Assert.Empty(merged.Fields);
        Assert.Null(merged.BitLength);
        Assert.Null(merged.Factor);
        Assert.Null(merged.Offset);
        Assert.Equal("", merged.Unit);
        Assert.Contains(merged.EngineeringIssues, issue =>
            issue.StartsWith("Conflicting physical field layouts", StringComparison.Ordinal));
    }

    [Fact]
    public void WriteFirstThenReadRetainsNonConflictingPhysicalMetadata()
    {
        var definitions = new Dictionary<int, DidParameter>();
        var write = new DidParameter(0xF192, "Coding", "Coding", "", "",
            null, null, null, null, "", null, "ECU", "file.odx");
        var read = write with
        {
            Unit = "V", Factor = 0.25, Offset = -1,
            Minimum = -1, Maximum = 62.75,
            BitLength = 8, DataType = "A_UINT32"
        };
        DiagnosticDidDefinitionMerger.AddOrMerge(definitions, write, "ODX SID 0x2E");
        DiagnosticDidDefinitionMerger.AddOrMerge(definitions, read, "ODX SID 0x22");

        var merged = Assert.Single(definitions.Values);
        Assert.Equal("V", merged.Unit);
        Assert.Equal(0.25, merged.Factor);
        Assert.Equal(-1, merged.Offset);
        Assert.Equal(-1, merged.Minimum);
        Assert.Equal(62.75, merged.Maximum);
        Assert.Equal(8, merged.BitLength);
        Assert.Equal("A_UINT32", merged.DataType);
        Assert.Empty(merged.EngineeringIssues);
    }

    [Fact]
    public void OdxWriteRequestParametersAndReadResponseParametersAreBothRetained()
    {
        const string xml = """
            <ODX><DIAG-LAYER-CONTAINER><SHORT-NAME>Engine</SHORT-NAME>
              <BASE-VARIANT><SHORT-NAME>Engine</SHORT-NAME>
                <DIAG-COMMS>
                  <DIAG-SERVICE><SHORT-NAME>Read_P</SHORT-NAME>
                    <REQUEST-REF ID-REF="r.read"/>
                    <POS-RESPONSE-REFS><POS-RESPONSE-REF ID-REF="response"/></POS-RESPONSE-REFS>
                  </DIAG-SERVICE>
                  <DIAG-SERVICE><SHORT-NAME>Write_P</SHORT-NAME>
                    <REQUEST-REF ID-REF="r.write"/>
                  </DIAG-SERVICE>
                </DIAG-COMMS>
                <REQUESTS>
                  <REQUEST ID="r.read"><PARAMS>
                    <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE>
                      <DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
                    <PARAM SEMANTIC="ID"><CODED-VALUE>61840</CODED-VALUE>
                      <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
                  </PARAMS></REQUEST>
                  <REQUEST ID="r.write"><PARAMS>
                    <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>46</CODED-VALUE>
                      <DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
                    <PARAM SEMANTIC="ID"><CODED-VALUE>61840</CODED-VALUE>
                      <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
                    <PARAM SEMANTIC="DATA"><SHORT-NAME>WriteBlock</SHORT-NAME>
                      <DOP-REF ID-REF="s.write"/></PARAM>
                  </PARAMS></REQUEST>
                </REQUESTS>
                <POS-RESPONSES>
                  <POS-RESPONSE ID="response"><PARAMS>
                    <PARAM SEMANTIC="SERVICE-ID"><SHORT-NAME>SID</SHORT-NAME></PARAM>
                    <PARAM SEMANTIC="DATA"><SHORT-NAME>ReadStatus</SHORT-NAME>
                      <DOP-REF ID-REF="dop.read"/></PARAM>
                  </PARAMS></POS-RESPONSE>
                </POS-RESPONSES>
                <DIAG-DATA-DICTIONARY-SPEC>
                  <STRUCTURES><STRUCTURE ID="s.write"><SHORT-NAME>Options</SHORT-NAME>
                    <PARAMS><PARAM><SHORT-NAME>WriteFlag</SHORT-NAME></PARAM></PARAMS>
                  </STRUCTURE></STRUCTURES>
                  <DATA-OBJECT-PROPS>
                    <DATA-OBJECT-PROP ID="dop.read"><SHORT-NAME>Status</SHORT-NAME>
                      <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32">
                        <BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
                    </DATA-OBJECT-PROP>
                  </DATA-OBJECT-PROPS>
                </DIAG-DATA-DICTIONARY-SPEC>
              </BASE-VARIANT>
            </DIAG-LAYER-CONTAINER></ODX>
            """;
        var file = WriteTemp(".odx", xml);
        try
        {
            var did = Assert.Single(DidCatalog.Load(file).Parameters);
            Assert.Equal(0xF190, did.Did);
            Assert.Contains(did.EngineeringParameters,
                node => node.Name == "ReadStatus" &&
                    node.DefinitionOrigin.Contains("0x22"));
            Assert.Contains(did.EngineeringParameters,
                node => node.Name == "WriteBlock" &&
                    node.DefinitionOrigin.Contains("0x2E"));
            Assert.Contains(did.EngineeringParameters,
                node => node.Name == "WriteFlag" && node.Depth >= 1);
            Assert.Equal(did.EngineeringParameters.Count,
                did.EngineeringParameters.Select(node => node.Path).Distinct().Count());
        }
        finally { File.Delete(file); }
    }

    private static string WriteTemp(string ext, string xml)
    {
        var file = Path.Combine(Path.GetTempPath(),
            "rbg-merge-" + Guid.NewGuid().ToString("N") + ext);
        File.WriteAllText(file, xml);
        return file;
    }
}
