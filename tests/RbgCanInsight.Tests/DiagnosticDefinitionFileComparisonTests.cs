using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Tests the real CDD importer + comparison pipeline rather than
/// only constructing synthetic records by hand. No OEM files are included.</summary>
public sealed class DiagnosticDefinitionFileComparisonTests
{
    [Fact]
    public void CddTextTableChangeIsReportedForDidAndNestedEngineeringParameter()
    {
        const string baseline = """
            <CANDELA><ECU qual="Powertrain"/>
              <DATATYPES><TEXTTBL id="status.table">
                <CVALUETYPE bl="8" enc="uns"/>
                <TEXTMAP s="0" e="0"><TEXT>
                  <TUV xml:lang="en-US">Idle</TUV>
                </TEXT></TEXTMAP>
                <TEXTMAP s="3" e="3"><TEXT>
                  <TUV xml:lang="en-US">Fault</TUV>
                </TEXT></TEXTMAP>
              </TEXTTBL></DATATYPES>
              <DIAGCLASS>
                <SERVICE>
                  <SHORTCUTQUAL>OperatingStatus_Read</SHORTCUTQUAL>
                  <SEMANTIC>CURRENTDATA</SEMANTIC>
                  <RESPONSE>
                    <DATAOBJ qual="ModeStatus" dtref="status.table">
                      <BITOFFSET>24</BITOFFSET>
                    </DATAOBJ>
                  </RESPONSE>
                </SERVICE>
                <STATICVALUE v="0xF194"/>
              </DIAGCLASS>
            </CANDELA>
            """;
        var current = baseline.Replace(">Fault<", ">FaultCode<", StringComparison.Ordinal);
        var directory = Path.Combine(Path.GetTempPath(),
            "rbg-cdd-diff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var beforePath = Path.Combine(directory, "before.cdd");
        var afterPath = Path.Combine(directory, "after.cdd");
        try
        {
            File.WriteAllText(beforePath, baseline);
            File.WriteAllText(afterPath, current);
            var before = DidCatalog.LoadAll(beforePath);
            var after = DidCatalog.LoadAll(afterPath);
            var baselineDid = Assert.Single(Assert.Single(before).Parameters);
            Assert.Equal(0xF194, baselineDid.Did);
            Assert.False(Assert.Single(baselineDid.Fields).SupportsPhysicalConversion);
            var report = DiagnosticDefinitionComparer.Compare(before, after);
            var field = Assert.Single(report.Rows.Where(row =>
                row.Scope == "DID Field" &&
                row.Change == DiagnosticDefinitionChange.Changed));
            var par = Assert.Single(report.Rows.Where(row =>
                row.Scope == "Engineering PAR" &&
                row.Change == DiagnosticDefinitionChange.Changed));
            var parent = Assert.Single(report.Rows.Where(row =>
                row.Scope == "DID" &&
                row.Change == DiagnosticDefinitionChange.Changed));
            Assert.Equal(DiagnosticDefinitionImpact.Encoding, field.Impact);
            Assert.Contains("Raw meanings", field.ChangedFields);
            Assert.Equal(DiagnosticDefinitionImpact.Structure, par.Impact);
            Assert.Contains("Raw declared meanings", par.ChangedFields);
            Assert.Contains("Nested", parent.ChangedFields);
            Assert.Contains("FaultCode", par.CurrentDefinition);
            Assert.Contains("Fault", par.BaselineDefinition);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void SameCddDefinitionInRenamedFolderDoesNotInventEncodingDifferences()
    {
        const string xml = """
            <CANDELA><ECU qual="Demo"><DIAGCLASS>
              <SERVICE><SHORTCUTQUAL>Pressure_Read</SHORTCUTQUAL>
                <SEMANTIC>CURRENTDATA</SEMANTIC>
                <UNIT>bar</UNIT><BITLENGTH>16</BITLENGTH>
                <FACTOR>0.1</FACTOR><OFFSET>0</OFFSET>
              </SERVICE>
              <STATICVALUE v="0xF192"/>
            </DIAGCLASS></ECU></CANDELA>
            """;
        var dir = Path.Combine(Path.GetTempPath(),
            "rbg-cdd-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "old.cdd");
            var b = Path.Combine(dir, "new.cdd");
            File.WriteAllText(a, xml);
            File.WriteAllText(b, xml);
            var result = DiagnosticDefinitionComparer.Compare(
                DidCatalog.LoadAll(a), DidCatalog.LoadAll(b));
            Assert.True(result.Unchanged > 0);
            Assert.Equal(0, result.RequiresReview);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
