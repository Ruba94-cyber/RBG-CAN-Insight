using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Real-desktop acceptance of navigation in both directions between a diagnostic DID
/// and the correlated CAN signal (recorded evidence only, never a verified identity).</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiDidSignalNavigationTests
{
    // Declared byte position, ECU identity and diagnostic CAN IDs make the DID field verifiable.
    private const string Odx = """
        <ODX>
          <DIAG-LAYER-CONTAINER><SHORT-NAME>ECM</SHORT-NAME></DIAG-LAYER-CONTAINER>
          <ECU-VARIANT><SHORT-NAME>ECM</SHORT-NAME></ECU-VARIANT>
          <REQUEST ID="REQ_F405">
            <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
            <PARAM SEMANTIC="ID"><CODED-VALUE>62469</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
          </REQUEST>
          <POS-RESPONSE ID="RESP_F405"><PARAM SEMANTIC="DATA"><BYTE-POSITION>3</BYTE-POSITION><DOP-REF ID-REF="DOP_TEMP" /></PARAM></POS-RESPONSE>
          <DATA-OBJECT-PROP ID="DOP_TEMP">
            <SHORT-NAME>EngineCoolantTemperature</SHORT-NAME>
            <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT8"><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
            <PHYSICAL-TYPE BASE-DATA-TYPE="A_FLOAT64" />
            <COMPU-METHOD><COMPU-INTERNAL-TO-PHYS><COMPU-SCALES><COMPU-SCALE><COMPU-RATIONAL-COEFFS><COMPU-NUMERATOR><V>-40</V><V>1</V></COMPU-NUMERATOR></COMPU-RATIONAL-COEFFS><LOWER-LIMIT>-40</LOWER-LIMIT><UPPER-LIMIT>215</UPPER-LIMIT></COMPU-SCALE></COMPU-SCALES></COMPU-INTERNAL-TO-PHYS></COMPU-METHOD>
            <UNIT-REF ID-REF="UNIT_C" />
          </DATA-OBJECT-PROP>
          <UNIT ID="UNIT_C"><SHORT-NAME>degC</SHORT-NAME><DISPLAY-NAME>degC</DISPLAY-NAME></UNIT>
          <DIAG-SERVICE>
            <SHORT-NAME>Read_EngineCoolantTemperature</SHORT-NAME><LONG-NAME>Engine coolant temperature</LONG-NAME>
            <REQUEST-REF ID-REF="REQ_F405" /><POS-RESPONSE-REF ID-REF="RESP_F405" />
          </DIAG-SERVICE>
          <COMPARAM><SHORT-NAME>CP_REQUEST_ID</SHORT-NAME><VALUE>2016</VALUE></COMPARAM>
          <COMPARAM><SHORT-NAME>CP_RESPONSE_ID</SHORT-NAME><VALUE>2024</VALUE></COMPARAM>
        </ODX>
        """;

    // CAN signal EngCoolantTemp (0x100, byte 0) moves exactly with the DID 0xF405 response.
    private const string Trace = """
        date Tue Jul 07 10:00:00.000 2026
        base hex  timestamps absolute
        internal events logged
        2.000000 1 100 Rx d 8 32 20 00 00 00 00 00 00
        2.010000 1 7E0 Rx d 8 03 22 F4 05 00 00 00 00
        2.020000 1 7E8 Rx d 8 04 62 F4 05 32 00 00 00
        3.000000 1 100 Rx d 8 37 20 00 00 00 00 00 00
        3.010000 1 7E0 Rx d 8 03 22 F4 05 00 00 00 00
        3.020000 1 7E8 Rx d 8 04 62 F4 05 37 00 00 00
        4.000000 1 100 Rx d 8 3C 20 00 00 00 00 00 00
        4.010000 1 7E0 Rx d 8 03 22 F4 05 00 00 00 00
        4.020000 1 7E8 Rx d 8 04 62 F4 05 3C 00 00 00
        5.000000 1 100 Rx d 8 41 20 00 00 00 00 00 00
        5.010000 1 7E0 Rx d 8 03 22 F4 05 00 00 00 00
        5.020000 1 7E8 Rx d 8 04 62 F4 05 41 00 00 00
        6.000000 1 100 Rx d 8 46 20 00 00 00 00 00 00
        6.010000 1 7E0 Rx d 8 03 22 F4 05 00 00 00 00
        6.020000 1 7E8 Rx d 8 04 62 F4 05 46 00 00 00
        """;

    private static GuiSession Prepare()
    {
        var ui = new GuiSession();
        var odxPath = Path.Combine(ui.Root, "nav.odx");
        var tracePath = Path.Combine(ui.Root, "nav.asc");
        File.WriteAllText(odxPath, Odx);
        File.WriteAllText(tracePath, Trace);

        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.mapping");
        ui.Click("mapping.loadCatalog");
        ui.ChooseFile(odxPath);
        ui.AwaitMappingStatus("from 1 file");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(tracePath);
        ui.AwaitStatus("Trace loaded:");
        return ui;
    }

    private static void OpenCorrelationView(GuiSession ui)
    {
        ui.Navigate("nav.uds");
        ui.Until(() => ui.Main.FindFirstDescendant(cf => cf.ByName("Show UDS to CAN signal correlation")),
            "UDS-CAN correlation button").Press();
        ui.Until(() => ui.Visible("uds.correlationGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "a UDS-CAN correlation row", 60);
    }

    [Fact]
    public void EmptyCorrelationWorkspaceExplainsHowToStartWithoutDeadFilterActions()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.uds");
        ui.Until(() => ui.Main.FindFirstDescendant(cf =>
            cf.ByName("Show UDS to CAN signal correlation")),
            "UDS and CAN view selector").Press();

        ui.Until(() => ui.Visible("uds.correlationEmptyMessage").Name.Contains(
            "Load a trace", StringComparison.Ordinal)
                ? "empty state offers a clear next action" : null,
            "first-run correlation guidance");
        Assert.False(ui.Visible("uds.correlationOpenTrace").IsOffscreen);
        Assert.False(ui.Visible("uds.correlationLoadDbc").IsOffscreen);
        Assert.False(ui.Visible("uds.correlationClearFilters").IsEnabled);
    }

    [Fact]
    public void CorrelatedSignalOpensInTheDbcManagerFromTheDiagnosticCorrelation()
    {
        using var ui = Prepare();
        OpenCorrelationView(ui);
        ui.Visible("uds.correlationGrid").FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem))!.Press();

        ui.Click("uds.openCorrelatedSignal");

        ui.Until(() => ui.Status.Contains("Opened the exact source DBC", StringComparison.Ordinal) &&
                       ui.Status.Contains("EngineData.EngCoolantTemp", StringComparison.Ordinal) &&
                       ui.Status.Contains("CAN 1", StringComparison.Ordinal)
                ? "opened exact DBC/bus" : null,
            "unique DBC and physical channel opened from correlation");
        var signalGrid = ui.Visible("dbc.signalsGrid");
        Assert.False(signalGrid.IsOffscreen); // landed on the DBC manager
        ui.Until(() => signalGrid
            .FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))
            .FirstOrDefault(row => !row.IsOffscreen &&
                row.FindAllDescendants(cf => cf.ByName("EngCoolantTemp")).Length > 0),
            "correlation target is actually scrolled into view in virtualized DBC rows"); 
    }

    [Fact]
    public void CorrelationSearchAndRecordedPairFilterDoNotDiscardSourceEvidence()
    {
        using var ui = Prepare();
        OpenCorrelationView(ui);

        var grid = ui.Visible("uds.correlationGrid");
        ui.Fill("uds.correlationSearch", "unmatched-no-such-DBC");
        ui.Until(() => grid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length == 0
            ? "no matching hypotheses" : null, "search narrows candidate list");
        ui.Until(() => ui.Visible("uds.correlationEmptyMessage").Name.Contains(
            "No candidate matches", StringComparison.Ordinal)
                ? "actionable empty-state guidance" : null,
            "empty correlation search explains why nothing is shown");
        ui.Click("uds.correlationShowAll");
        ui.Until(() => grid.FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "show all candidates restores visible source rows");
        Assert.False(ui.Visible("uds.correlationClearFilters").IsEnabled);

        ui.Fill("uds.correlationSearch", "EngineData");
        ui.Until(() => grid.FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "DBC source visible after search");

        ui.Click("uds.correlationPairsOnly");
        ui.Until(() => grid.FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "source-paired candidate is preserved");

        ui.Fill("uds.correlationSearch", "no-such-DID");
        ui.Navigate("nav.dbc");
        ui.Until(() => ui.Visible("dbc.messagesGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "DBC source message").Press();
        var signals = ui.Visible("dbc.signalsGrid");
        var coolant = ui.Until(() =>
            signals.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))
                .FirstOrDefault(row => row.FindAllDescendants(
                    cf => cf.ByName("EngCoolantTemp")).Length > 0),
            "source-backed temperature signal");
        coolant.Press();
        ui.Click("dbc.relatedDids");

        // A cross-workspace jump explicitly reveals the correlation even if
        // an old UDS search was hiding it; no stale-list fallback is allowed.
        ui.Until(() => grid.FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "source-backed correlation revealed despite old filter");
        Assert.False(grid.IsOffscreen);
    }

    [Fact]
    public void PairedDidsAndCanSignalsOpenTheirActualRawSourceFrames()
    {
        using var ui = Prepare();
        OpenCorrelationView(ui);
        ui.Visible("uds.correlationGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem))!.Press();

        var summary = ui.Visible("uds.pairSummary");
        ui.Until(() => summary.Name.Contains("Pair 1 /", StringComparison.Ordinal)
            ? "first source pair" : null, "first recorded correlation pair");
        Assert.False(ui.Visible("uds.pairPrevious").IsEnabled);
        Assert.True(ui.Visible("uds.pairNext").IsEnabled);

        ui.Click("uds.pairNext");
        ui.Until(() => summary.Name.Contains("Pair 2 /", StringComparison.Ordinal)
            ? "second source pair" : null, "next distinct correlation evidence pair");
        Assert.True(ui.Visible("uds.pairPrevious").IsEnabled);

        // Both buttons navigate to real imported frame indices; they must
        // never select a synthetic decoded value without a source CAN frame.
        ui.Click("uds.pairCanFrame");
        Assert.False(ui.Visible("trace.grid").IsOffscreen);

        ui.Navigate("nav.uds");
        ui.Click("uds.pairDidFrame");
        Assert.False(ui.Visible("trace.grid").IsOffscreen);
    }

    [Fact]
    public void RawCanTraceFrameOpensOnlyItsRecordedDidCanEvidence()
    {
        using var ui = Prepare();
        ui.Navigate("nav.trace");
        var trace = ui.Visible("trace.grid");
        var row = ui.Until(() => trace.FindFirstDescendant(cf =>
            cf.ByControlType(ControlType.DataItem)), "first source CAN frame");
        row.Press();
        ui.ShowTraceSecondaryControls();
        ui.Click("trace.relatedPairs");

        ui.Until(() => ui.Status.Contains("recorded UDS↔CAN candidate(s)", StringComparison.Ordinal) &&
                       ui.Status.Contains("Frame #1", StringComparison.Ordinal)
                ? "raw source frame matches a paired candidate" : null,
            "source-evidenced raw Trace to UDS/CAN navigation");
        Assert.False(ui.Visible("uds.correlationGrid").IsOffscreen);
    }

    [Fact]
    public void RecordedDidFieldCanOpenItsPairedCanCandidatesWithoutGuessingTheEcu()
    {
        using var ui = Prepare();
        ui.Navigate("nav.uds");
        ui.Until(() => ui.Main.FindFirstDescendant(cf =>
            cf.ByName("Show decoded DID values")), "DID values toolbar").Press();

        var samples = ui.Visible("uds.didSamplesGrid");
        var recorded = ui.Until(() => samples.FindFirstDescendant(cf =>
            cf.ByControlType(ControlType.DataItem)), "recorded DID row");
        recorded.Press();
        ui.Click("uds.didToCan");

        ui.Until(() => ui.Status.Contains("source-paired CAN candidate(s)", StringComparison.Ordinal) &&
                       ui.Status.Contains("0xF405", StringComparison.Ordinal)
                ? "DID-to-CAN link" : null,
            "recorded DID field opens matching physical CAN candidate(s)");
        Assert.False(ui.Visible("uds.correlationGrid").IsOffscreen);
        Assert.Contains("Review every paired frame", ui.Status);
    }

    [Fact]
    public void DiagnosticDidsOpenFromTheSelectedCanSignal()
    {
        using var ui = Prepare();
        OpenCorrelationView(ui);                                   // make sure correlations are computed

        ui.Navigate("nav.dbc");
        ui.Until(() => ui.Visible("dbc.messagesGrid").FindFirstDescendant(
            cf => cf.ByControlType(ControlType.DataItem)), "first DBC message").Press();
        var signals = ui.Visible("dbc.signalsGrid");
        var coolant = ui.Until(() => signals.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))
            .FirstOrDefault(item => item.FindAllDescendants(cf => cf.ByName("EngCoolantTemp")).Length > 0),
            "EngCoolantTemp signal row");
        coolant.Press();

        ui.Until(() => ui.Visible("dbc.relatedDids").IsEnabled ? "enabled" : null, "Related DIDs enabled");
        ui.Click("dbc.relatedDids");

        ui.Until(() => ui.Status.Contains("statistical DID candidate(s)", StringComparison.Ordinal) &&
                       ui.Status.Contains("EngineData.EngCoolantTemp", StringComparison.Ordinal) &&
                       ui.Status.Contains("CAN 1", StringComparison.Ordinal) &&
                       ui.Status.Contains("0xF405", StringComparison.Ordinal)
                ? "source-matched" : null, "related DID status");
        Assert.False(ui.Visible("uds.correlationGrid").IsOffscreen);   // landed on the correlation view
        Assert.Contains("no automatic identity confirmation", ui.Status);
    }
}
