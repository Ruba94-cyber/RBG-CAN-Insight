using System.Xml.Linq;
using RbgCanInsight.DiagData;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DiagnosticConditionsTests
{
    private const string Odx = """
        <ODX>
          <DIAG-LAYER-CONTAINER><SHORT-NAME>ECM</SHORT-NAME></DIAG-LAYER-CONTAINER>
          <ECU-VARIANT><SHORT-NAME>ECM</SHORT-NAME></ECU-VARIANT>
          <STATE-CHART ID="SC_SESSION"><SHORT-NAME>DiagnosticSession</SHORT-NAME><SEMANTIC>SESSION</SEMANTIC>
            <STATES>
              <STATE ID="ST_DEFAULT"><SHORT-NAME>Default</SHORT-NAME></STATE>
              <STATE ID="ST_EXTENDED"><SHORT-NAME>Extended</SHORT-NAME></STATE>
            </STATES></STATE-CHART>
          <STATE-CHART ID="SC_SECURITY"><SHORT-NAME>SecurityAccess</SHORT-NAME><SEMANTIC>SECURITY</SEMANTIC>
            <STATES><STATE ID="ST_LEVEL1"><SHORT-NAME>Level_01</SHORT-NAME></STATE></STATES></STATE-CHART>
          <STATE-CHART ID="SC_MISC"><SHORT-NAME>Vehicle</SHORT-NAME><SEMANTIC>VEHICLE-STATE</SEMANTIC>
            <STATES><STATE ID="ST_STANDSTILL"><SHORT-NAME>Standstill</SHORT-NAME></STATE></STATES></STATE-CHART>
          <REQUEST ID="REQ_CAL">
            <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>49</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
            <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>1</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
            <PARAM SEMANTIC="ID"><CODED-VALUE>515</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
          </REQUEST>
          <REQUEST ID="REQ_FLUSH">
            <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>49</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
            <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>1</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
            <PARAM SEMANTIC="ID"><CODED-VALUE>516</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
          </REQUEST>
          <REQUEST ID="REQ_READ">
            <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
            <PARAM SEMANTIC="ID"><CODED-VALUE>62469</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
          </REQUEST>
          <DIAG-SERVICE ID="S_CAL"><SHORT-NAME>Calibrate</SHORT-NAME>
            <PRE-CONDITION-STATE-REFS>
              <PRE-CONDITION-STATE-REF ID-REF="ST_EXTENDED" />
              <PRE-CONDITION-STATE-REF ID-REF="ST_LEVEL1" />
              <PRE-CONDITION-STATE-REF ID-REF="ST_STANDSTILL" />
            </PRE-CONDITION-STATE-REFS>
            <REQUEST-REF ID-REF="REQ_CAL" />
          </DIAG-SERVICE>
          <DIAG-SERVICE ID="S_FLUSH"><SHORT-NAME>Flush</SHORT-NAME>
            <PRE-CONDITION-STATE-REFS>
              <PRE-CONDITION-STATE-REF ID-REF="ST_DEFAULT" />
              <PRE-CONDITION-STATE-REF ID-REF="ST_EXTENDED" />
            </PRE-CONDITION-STATE-REFS>
            <REQUEST-REF ID-REF="REQ_FLUSH" />
          </DIAG-SERVICE>
          <DIAG-SERVICE ID="S_READ"><SHORT-NAME>Read_Temp</SHORT-NAME><REQUEST-REF ID-REF="REQ_READ" /></DIAG-SERVICE>
        </ODX>
        """;

    private static DidCatalog Load()
    {
        var catalog = Assert.Single(OdxCatalogReader.LoadFromDocuments([XDocument.Parse(Odx)], "ecm.odx"));
        return catalog;
    }

    [Fact]
    public void OdxPreConditionStatesAreExtractedAsSessionSecurityAndOtherConditions()
    {
        var catalog = Load();
        var calibrate = catalog.ConditionDefinitions.Where(c => c.ServiceName == "Calibrate").ToArray();
        Assert.Equal(3, calibrate.Length);
        Assert.All(calibrate, c => { Assert.Equal(0x31, c.ServiceId); Assert.Equal(0x0203, c.Identifier); });
        Assert.Contains(calibrate, c => c.Kind == DiagnosticConditionKind.Session && c.State == "Extended" && c.StateChart == "DiagnosticSession");
        Assert.Contains(calibrate, c => c.Kind == DiagnosticConditionKind.Security && c.State == "Level_01");
        Assert.Contains(calibrate, c => c.Kind == DiagnosticConditionKind.Other && c.State == "Standstill");
        Assert.DoesNotContain(catalog.ConditionDefinitions, c => c.ServiceName == "Read_Temp");   // no pre-condition declared
    }

    [Fact]
    public void RoutinePrerequisitesComeFromTheDeclaredStatesAndKeepAlternativesAsASet()
    {
        var catalog = Load();
        var calibrate = catalog.RoutineDefinitions.Single(r => r.Identifier == 0x0203);
        Assert.Equal("Extended", calibrate.RequiredSession);
        Assert.Equal("Level_01", calibrate.RequiredSecurityLevel);
        var flush = catalog.RoutineDefinitions.Single(r => r.Identifier == 0x0204);
        Assert.Equal("Default | Extended", flush.RequiredSession);
        Assert.Equal(string.Empty, flush.RequiredSecurityLevel);
    }

    private static CanFrame Frame(uint id, byte[] data, long ms) =>
        new(0, id, data.Concat(new byte[8 - data.Length]).ToArray(), ms * 1_000_000);

    [Fact]
    public void RecordedTrafficIsCheckedAgainstTheDeclaredPreConditions()
    {
        var catalog = Load();
        var frames = new[]
        {
            // Calibrate (needs Extended + level 1) in the default session after a session change to default.
            Frame(0x7E0, [0x02, 0x10, 0x01], 0), Frame(0x7E8, [0x06, 0x50, 0x01, 0, 0x32, 1, 0xF4], 10),
            Frame(0x7E0, [0x04, 0x31, 0x01, 0x02, 0x03], 100), Frame(0x7E8, [0x04, 0x71, 0x01, 0x02, 0x03], 110),
            // Flush (Default or Extended) in the default session -> satisfied.
            Frame(0x7E0, [0x04, 0x31, 0x01, 0x02, 0x04], 200), Frame(0x7E8, [0x04, 0x71, 0x01, 0x02, 0x04], 210),
            // Extended session + level 1 unlocked, then Calibrate -> satisfied.
            Frame(0x7E0, [0x02, 0x10, 0x03], 300), Frame(0x7E8, [0x06, 0x50, 0x03, 0, 0x32, 1, 0xF4], 310),
            Frame(0x7E0, [0x04, 0x27, 0x02, 0xAA, 0xBB], 400), Frame(0x7E8, [0x02, 0x67, 0x02], 410),
            Frame(0x7E0, [0x04, 0x31, 0x01, 0x02, 0x03], 500), Frame(0x7E8, [0x04, 0x71, 0x01, 0x02, 0x03], 510)
        };
        var transactions = UdsTransactionAnalyzer.Analyze(frames, [catalog]);

        var report = DiagnosticSpecTraceChecker.Check(transactions, [catalog]);

        var notMet = Assert.Single(report.Findings, f => f.Kind == SpecTraceFindingKind.RoutinePrerequisiteNotMet);
        Assert.Contains("declared session 0x", notMet.Identifier);
        Assert.Contains("recorded session 0x01", notMet.Identifier);
        Assert.Equal([2, 3], notMet.FrameIndices);                                  // only the first Calibrate request
        Assert.DoesNotContain(report.Findings, f => f.Identifier.Contains("0x0204")); // Flush: set satisfied
        Assert.DoesNotContain(report.Findings, f => f.Kind == SpecTraceFindingKind.RoutinePrerequisiteUnverifiable);
    }

    [Theory]
    [InlineData("Level_01", 1)]
    [InlineData("security level 3", 3)]
    [InlineData("Level 0x05", 5)]
    [InlineData("0x01", 1)]
    [InlineData("none", null)]
    [InlineData("Unlocked", null)]
    public void SecurityLevelNamesAreParsedConservatively(string text, int? expected) =>
        Assert.Equal(expected, DiagnosticSpecTraceChecker.ParseSecurityLevel(text));
}
