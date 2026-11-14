using System.IO.Compression;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Real-desktop acceptance of the diagnostic conditions documentation export.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiDiagnosticConditionsTests
{
    private const string Odx = """
        <ODX>
          <DIAG-LAYER-CONTAINER><SHORT-NAME>ECM</SHORT-NAME></DIAG-LAYER-CONTAINER>
          <ECU-VARIANT><SHORT-NAME>ECM</SHORT-NAME></ECU-VARIANT>
          <STATE-CHART ID="SC_SESSION"><SHORT-NAME>DiagnosticSession</SHORT-NAME><SEMANTIC>SESSION</SEMANTIC>
            <STATES><STATE ID="ST_EXTENDED"><SHORT-NAME>Extended</SHORT-NAME></STATE></STATES></STATE-CHART>
          <STATE-CHART ID="SC_SECURITY"><SHORT-NAME>SecurityAccess</SHORT-NAME><SEMANTIC>SECURITY</SEMANTIC>
            <STATES><STATE ID="ST_LEVEL1"><SHORT-NAME>Level_01</SHORT-NAME></STATE></STATES></STATE-CHART>
          <REQUEST ID="REQ_CAL">
            <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>49</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
            <PARAM SEMANTIC="SUBFUNCTION"><CODED-VALUE>1</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
            <PARAM SEMANTIC="ID"><CODED-VALUE>515</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
          </REQUEST>
          <REQUEST ID="REQ_READ">
            <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
            <PARAM SEMANTIC="ID"><CODED-VALUE>62469</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
          </REQUEST>
          <DIAG-SERVICE ID="S_CAL"><SHORT-NAME>CalibrateSensor</SHORT-NAME>
            <PRE-CONDITION-STATE-REFS>
              <PRE-CONDITION-STATE-REF ID-REF="ST_EXTENDED" />
              <PRE-CONDITION-STATE-REF ID-REF="ST_LEVEL1" />
            </PRE-CONDITION-STATE-REFS>
            <REQUEST-REF ID-REF="REQ_CAL" />
          </DIAG-SERVICE>
          <DIAG-SERVICE ID="S_READ"><SHORT-NAME>Read_Temp</SHORT-NAME><REQUEST-REF ID-REF="REQ_READ" /></DIAG-SERVICE>
        </ODX>
        """;

    [Fact]
    public void ConditionsWorkbookDocumentsDeclaredStatesAndTheAbsenceOfThem()
    {
        using var ui = new GuiSession();
        var odxPath = Path.Combine(ui.Root, "conditions.odx");
        File.WriteAllText(odxPath, Odx);
        var destination = Path.Combine(ui.Root, "conditions.xlsx");

        ui.Navigate("nav.mapping");
        ui.Click("mapping.loadCatalog");
        ui.ChooseFile(odxPath);
        ui.AwaitMappingStatus("from 1 file");
        ui.Navigate("nav.uds");
        ui.Click("uds.openCatalog");

        ui.Click("diagnostics.exportConditions");
        ui.ChooseFile(destination);
        ui.AwaitStatus("Diagnostic conditions exported");

        using var archive = ZipFile.OpenRead(destination);
        var text = string.Join("\n", archive.Entries.Where(entry => entry.FullName.EndsWith(".xml"))
            .Select(entry => { using var reader = new StreamReader(entry.Open()); return reader.ReadToEnd(); }));
        Assert.Contains("CalibrateSensor", text);
        Assert.Contains("Extended", text);
        Assert.Contains("Level_01", text);
        Assert.Contains("Pre-conditions declared in the file", text);
        Assert.Contains("does not prove none is required", text);     // the read DID declares nothing; not claimed as unconditional
        Assert.Contains("not extracted", text, StringComparison.OrdinalIgnoreCase);
    }
}
