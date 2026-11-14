using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>
/// Regressions for the source-backed Signals & Plot workbench. Uses an isolated
/// synthetic trace/DBC and the actual WPF process in an interactive Windows session.
/// </summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiObservedSignalsPlotTests
{
    [Fact]
    public void ObservedSignalsCanFilterToChangesAndOpenQuickPlot()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");

        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.signals");
        var grid = ui.Visible("signals.grid");
        ui.Until(() => grid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length >= 4
            ? "four observed series" : null, "all four recorded DBC signals");

        // EngCoolantTemp and EngineLoad change. Mode and VehicleSpeed do not.
        ui.Click("signals.changingOnly");
        ui.Until(() => grid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length == 2
            ? "changing series only" : null, "only two changed series after filter");

        ui.Click("signals.quickPlot");
        var plot = ui.WindowWithTitle("Signal detail -");
        Assert.NotNull(plot);
        Assert.Contains("Signal detail -", plot.Title);
    }


    [Fact]
    public void QuickPlotAndCatalogIdentifyAnExactPhysicalLoggerInMultiFileTrace()
    {
        using var ui = new GuiSession();
        var second = Path.Combine(ui.Root, "other-logger.asc");
        File.Copy(ui.TracePath, second);
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFiles(ui.TracePath, second);
        var merge = ui.WindowWithTitle("Merge mode");
        merge.FindFirstDescendant(cf => cf.ByAutomationId("merge.ok"))!
            .Patterns.Invoke.Pattern.Invoke();
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.signals");
        var grid = ui.Visible("signals.grid");
        ui.Fill("signals.search", "logger:2 EngCoolantTemp");
        ui.Until(() => grid.FindAllDescendants(
                cf => cf.ByControlType(ControlType.DataItem)).Length == 1
                ? "second logger" : null,
            "source-specific DBC signal selection");
        ui.Click("signals.quickPlot");
        var quickPlot = ui.WindowWithTitle("Signal detail - EngineData.EngCoolantTemp");
        Assert.Contains("Logger 2", quickPlot.Title);
        Assert.Contains("other-logger.asc", quickPlot.Title);
        ui.Fill("signals.search", "logger:0");
        ui.Until(() => grid.FindAllDescendants(
                cf => cf.ByControlType(ControlType.DataItem)).Length == 0
                ? "invalid logger ordinal rejected" : null,
            "safe logger filter");
    }


    [Fact]
    public void RecordingPickerScopesMainPlotAndPreservesCurveSelections()
    {
        using var ui = new GuiSession();
        var other = Path.Combine(ui.Root, "scope-b.asc");
        File.Copy(ui.TracePath, other);
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFiles(ui.TracePath, other);
        var merge = ui.WindowWithTitle("Merge mode");
        merge.FindFirstDescendant(cf => cf.ByAutomationId("merge.ok"))!
            .Patterns.Invoke.Pattern.Invoke();
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.signals");
        var picker = ui.Visible("signals.loggerScope").AsComboBox();
        var grid = ui.Visible("signals.grid");
        // The signal grid is virtualized and only ~4-6 rows are realized in the
        // viewport, so count rows through the UIA grid pattern (total row count).
        int SeriesRows() => grid.Patterns.Grid.PatternOrDefault?.RowCount.ValueOrDefault ?? -1;
        ui.Until(() => SeriesRows() == 8
            ? "eight independent series" : null,
            "multi-file signal inventory");
        picker.Select(1);
        ui.Until(() => SeriesRows() == 4
            ? "first physical logger" : null,
            "source picker scopes decoded signal catalog");
        Assert.Contains("4 in view / 8 observed", ui.Visible("signals.summary").Name);
        ui.Click("signals.plotVisible");
        ui.Until(() => ui.Visible("signals.summary").Name.Contains("4 plotted here")
            ? "first logger plotted" : null, "first logger plot selection");
        picker.Select(2);
        ui.Until(() => ui.Visible("signals.summary").Name.Contains("0 plotted here")
            ? "other logger initially unplotted" : null,
            "source scope hides other logger's main-plot curves");
        ui.Click("signals.plotVisible");
        ui.Until(() => ui.Visible("signals.summary").Name.Contains("4 plotted here")
            ? "second logger plotted" : null, "second logger plot selection");
        picker.Select(0);
        ui.Until(() => ui.Visible("signals.summary").Name.Contains("8 plotted here")
            ? "both physical loggers restored" : null,
            "switching back preserves selected curves");
        ui.Click("signals.clearFilters");
        Assert.Contains("8 in view / 8 observed", ui.Visible("signals.summary").Name);
    }


    [Fact]
    public void ImportedLoggerWithoutDbcMatchesIsStillSelectableAndExplained()
    {
        using var ui = new GuiSession();
        var unmatched = Path.Combine(ui.Root, "unmatched-logger.asc");
        File.WriteAllText(unmatched, """
            date Tue Jul 07 10:00:00.000 2026
            base hex  timestamps absolute
            internal events logged
            0.100000 1 777 Rx d 8 AA BB CC DD EE FF 00 11
            0.200000 1 777 Rx d 8 AA BB CC DD EE FF 00 22
            """);
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFiles(ui.TracePath, unmatched);
        var mode = ui.WindowWithTitle("Merge mode");
        mode.FindFirstDescendant(cf => cf.ByAutomationId("merge.ok"))!
            .Patterns.Invoke.Pattern.Invoke();
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.signals");
        var picker = ui.Visible("signals.loggerScope").AsComboBox();
        picker.Select(2);
        ui.Until(() => ui.Visible("signals.empty").Name.Contains(
            "no DBC-decoded signals", StringComparison.OrdinalIgnoreCase)
            ? "unmatched logger explained" : null,
            "original logger without decoded DBC signals remains selectable");
        Assert.Contains("0 in view /", ui.Visible("signals.summary").Name);
        picker.Select(1);
        ui.Until(() => !ui.Visible("signals.summary").Name.StartsWith("0 in view")
            ? "matching logger observations" : null,
            "switch to source with DBC observations");
    }


    [Fact]
    public void ChangeMatrixProvidesRecordingPickerWithSourceScopedSummary()
    {
        using var ui = new GuiSession();
        var second = Path.Combine(ui.Root, "matrix-second.asc");
        File.Copy(ui.TracePath, second);
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFiles(ui.TracePath, second);
        var merge = ui.WindowWithTitle("Merge mode");
        merge.FindFirstDescendant(cf => cf.ByAutomationId("merge.ok"))!
            .Patterns.Invoke.Pattern.Invoke();
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.matrix");
        var picker = ui.Visible("matrix.loggerScope").AsComboBox();
        picker.Select(1);
        ui.Until(() => ui.Visible("matrix.grid")
            .FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length > 0
            ? "first logger matrix findings" : null, "source-specific matrix rows");
        picker.Select(2);
        ui.Until(() => ui.Visible("matrix.grid")
            .FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length > 0
            ? "second logger matrix findings" : null, "second source matrix rows");
        picker.Select(0);
        Assert.NotNull(ui.Visible("matrix.grid"));
    }

    [Fact]
    public void PlotSelectedKeepsOtherCurvesAndSupportsSortableSignalCatalog()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.signals");
        ui.Fill("signals.search", "EngCoolantTemp");
        ui.Until(() => ui.Visible("signals.grid")
            .FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length == 1
            ? "one matching signal" : null, "select exact observed signal");

        ui.Click("signals.plotSelected");
        ui.Until(() => ui.Visible("signals.summary").Name.Contains("1 plotted",
            StringComparison.Ordinal) ? "one plotted signal" : null,
            "Plot selected adds one curve");
        ui.Click("signals.clearFilters");
        ui.Until(() => ui.Visible("signals.grid")
            .FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length >= 4
            ? "all observed rows" : null, "restore full catalog");
        ui.Click("signals.plotVisible");
        ui.Until(() => ui.Visible("signals.summary").Name.Contains("4 plotted",
            StringComparison.Ordinal) ? "all four observed curves" : null,
            "incremental add visible preserves previous selection");
        ui.Click("signals.plotVisible");
        ui.Until(() => ui.Visible("signals.summary").Name.Contains("4 plotted",
            StringComparison.Ordinal) ? "no duplicate selections" : null,
            "repeated add visible is idempotent after all are plotted");
        Assert.NotNull(ui.Visible("signals.grid").FindFirstDescendant(
            cf => cf.ByControlType(ControlType.Header)));
    }

    [Fact]
    public void QuickPlotIdentifiesItsBusAndScrubsExactRecordedSignalValues()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");

        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.signals");
        ui.Fill("signals.search", "EngCoolantTemp");
        var grid = ui.Visible("signals.grid");
        ui.Until(() => grid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length == 1
            ? "one exact signal" : null, "single matching recorded signal");

        ui.Click("signals.quickPlot");
        var plot = ui.WindowWithTitle("Signal detail - EngineData.EngCoolantTemp");
        Assert.Contains("fixture", plot.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CAN 1", plot.Title);
        Assert.Contains("0x100", plot.Title);

        FlaUI.Core.AutomationElements.AutomationElement Control(string id) =>
            ui.Until(() => plot.FindFirstDescendant(cf => cf.ByAutomationId(id)),
                "Quick Plot control " + id);

        var physical = Control("quickplot.cursor.physical");
        ui.Until(() => physical.Name.Contains("20", StringComparison.Ordinal) ? "tail 20 degC" : null,
            "last recorded coolant value");

        var mainTimeline = ui.Until(() => ui.Find("signals.timeline"),
            "main Signals & Plot timeline");

        Control("quickplot.head").Press();
        ui.Until(() => physical.Name.Contains("10", StringComparison.Ordinal) ? "head 10 degC" : null,
            "first recorded coolant value");
        Assert.Contains("50", Control("quickplot.cursor.raw").Name);
        ui.Until(() => mainTimeline.AsSlider().Value < 20 ? "main cursor at beginning" : null,
            "shared main plot follows Quick Plot head");
        var initialMainPercent = mainTimeline.AsSlider().Value;

        Control("quickplot.tail").Press();
        ui.Until(() => physical.Name.Contains("20", StringComparison.Ordinal) ? "tail 20 degC" : null,
            "tail value restored");
        Assert.Contains("60", Control("quickplot.cursor.raw").Name);
        // This signal ends at 0.2 s while the overall fixture continues
        // with UDS traffic to 0.51 s: the correct global slider position
        // is ~39%, NOT >50 or a falsely clamped 100%.
        ui.Until(() => mainTimeline.AsSlider().Value > initialMainPercent + 25
            ? "main cursor follows this signal's tail" : null,
            "shared main plot follows Quick Plot tail without shrinking trace extent");
    }

    [Fact]
    public void SilentEcuGapKeepsGlobalTimelineAndLinearModeIsExplicit()
    {
        using var ui = new GuiSession();
        // One ECU reports a value near trace start, while another DBC
        // message continues sending ten seconds later. The selected
        // signal's last frame MUST NOT truncate the visible trace range.
        File.WriteAllText(ui.TracePath, """
            date Tue Jul 07 10:00:00.000 2026
            base hex  timestamps absolute
            internal events logged
            0.000000 1 100 Rx d 8 32 20 00 00 00 00 00 00
            0.100000 1 100 Rx d 8 37 28 00 00 00 00 00 00
            10.000000 1 200 Rx d 8 88 13 00 00 00 00 00 00
            """);
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.signals");
        ui.Fill("signals.search", "EngCoolantTemp");
        ui.Until(() => ui.Visible("signals.grid")
            .FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length == 1
            ? "selected one engine signal" : null, "filter engine signal");
        ui.Click("signals.plotVisible");
        ui.Click("signals.quickPlot");
        var detail = ui.WindowWithTitle("Signal detail - EngineData.EngCoolantTemp");

        ui.Fill("signals.windowSeconds", "1");
        ui.Click("signals.tail"); // commits LostFocus and seeks global end
        var position = ui.Visible("signals.timeline").AsSlider();
        ui.Until(() => position.Value >= 99 ? "global trace end" : null,
            "global decoded timeline must include other bus messages");
        Assert.False(ui.Visible("signals.chart").IsOffscreen);
        var source = ui.Until(() => detail.FindFirstDescendant(
            cf => cf.ByAutomationId("quickplot.cursor.source")), "Quick Plot source evidence");
        var rawFrameAction = ui.Until(() => detail.FindFirstDescendant(
            cf => cf.ByAutomationId("quickplot.sourceFrame")), "visible source-frame navigation");
        Assert.False(rawFrameAction.IsOffscreen);
        Assert.Contains("original recorded CAN", rawFrameAction.Name, StringComparison.OrdinalIgnoreCase);
        ui.Until(() => source.Name.Contains("Held " + 9.90.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " s", StringComparison.Ordinal)
            ? "older ECU frame is explicitly held, not freshly observed" : null,
            "Quick Plot reports full source age across a CAN gap");

        // Optional linear lines are never on implicitly: sample-and-hold
        // is the evidence-faithful default, even for analog values.
        var mode = ui.Visible("signals.linearInterpolation").AsCheckBox();
        Assert.False(mode.IsChecked);
        ui.Click("signals.linearInterpolation");
        Assert.True(mode.IsChecked);
    }

    [Fact]
    public void J1939SourceAddressAndPgnSearchOpenOnlyTheSelectedRecordedQuickPlot()
    {
        using var ui = new GuiSession();
        // Two 29-bit CAN IDs from independently recorded source
        // addresses, same EEC1 PGN. Keep both definitions explicit so
        // this test does not depend on automatic PGN option selection.
        File.WriteAllText(ui.DbcPath, """
            BO_ 2364540158 EEC1: 8 ECM
             SG_ EngineSpeed : 24|16@1+ (0.125,0) [0|8031.875] "rpm" ECM
            BO_ 2364539904 EEC1: 8 ECM
             SG_ EngineSpeed : 24|16@1+ (0.125,0) [0|8031.875] "rpm" ECM
            """);
        File.WriteAllText(ui.TracePath, """
            date Tue Jul 07 10:00:00.000 2026
            base hex  timestamps absolute
            internal events logged
            0.000000 1 CF004FEx Rx d 8 00 00 00 10 27 00 00 00
            0.100000 1 CF00400x Rx d 8 00 00 00 20 4E 00 00 00
            0.200000 1 CF004FEx Rx d 8 00 00 00 E0 2E 00 00 00
            0.300000 1 CF00400x Rx d 8 00 00 00 80 3E 00 00 00
            """);
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.signals");
        var grid = ui.Visible("signals.grid");
        ui.Until(() => grid.FindAllDescendants(
            cf => cf.ByControlType(ControlType.DataItem)).Length == 2
            ? "both real EEC1 sources" : null, "distinct recorded source addresses");

        ui.Fill("signals.search", "pgn:0xF004");
        ui.Until(() => grid.FindAllDescendants(
            cf => cf.ByControlType(ControlType.DataItem)).Length == 2
            ? "two sources share PGN" : null, "PGN query shows both real EEC1 sources");

        ui.Fill("signals.search", "sa:0x00 EngineSpeed");
        ui.Until(() => grid.FindAllDescendants(
            cf => cf.ByControlType(ControlType.DataItem)).Length == 1
            ? "SA 00 exact source only" : null, "source address filters one observed ECU");
        ui.Click("signals.quickPlot");
        var plot = ui.WindowWithTitle("Signal detail - EEC1.EngineSpeed");
        Assert.Contains("0x0CF00400", plot.Title);

        ui.Fill("signals.search", "sa:FE");
        ui.Until(() => grid.FindAllDescendants(
            cf => cf.ByControlType(ControlType.DataItem)).Length == 1
            ? "SA FE exact source only" : null, "other J1939 source remains independently filterable");

        ui.Fill("signals.search", "sa:GG");
        ui.Until(() => grid.FindAllDescendants(
            cf => cf.ByControlType(ControlType.DataItem)).Length == 0
            ? "malformed SA query matches nothing" : null,
            "invalid source filter cannot silently become a general name query");
        ui.Click("signals.clearFilters");
        ui.Until(() => grid.FindAllDescendants(
            cf => cf.ByControlType(ControlType.DataItem)).Length == 2
            ? "both source-backed signals restored" : null,
            "clearing source query retains observed signal catalog");
    }

    [Fact]
    public void EmptySignalSearchCanBeClearedWithoutRemovingThePlottedCurves()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.signals");
        ui.Click("signals.plotVisible");
        ui.Until(() => ui.Visible("signals.summary").Name.Contains("4 plotted")
            ? "four plotted" : null, "plot selection");

        ui.Fill("signals.search", "no-such-observed-signal");
        ui.Until(() => !ui.Visible("signals.empty").IsOffscreen
            ? "empty result explained" : null, "empty filter state");
        ui.Click("signals.clearFilters");
        ui.Until(() => ui.Visible("signals.visibleCount").Name.Contains("4 / 4")
            ? "all four restored" : null, "clear filter without changing curve selections");
        Assert.Contains("4 plotted", ui.Visible("signals.summary").Name);
    }

    [Fact]
    public void UniversalSearchFindsConstantSignalsAndOpensTheRecordedSeriesDirectly()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Click("globalSearch.open");
        var window = ui.WindowWithTitle("Search");
        var box = ui.Until(() => window.FindFirstDescendant(
            cf => cf.ByAutomationId("globalSearch.query")), "global search input");
        box.AsTextBox().Text = "Mode";
        var open = ui.Until(() => window.FindFirstDescendant(
            cf => cf.ByAutomationId("globalSearch.openResult")), "open recorded search hit");
        open.Press();
        ui.Until(() => !ui.IsWindowOpen("Search") ? "search closed" : null,
            "global search activation");
        Assert.False(ui.Visible("signals.grid").IsOffscreen);
        ui.Until(() => ui.Visible("signals.selectedSource").Name.Contains("EngineData.Mode")
            ? "constant signal selected" : null, "global search selects constant recorded series");
        Assert.Contains("1 plotted", ui.Visible("signals.summary").Name);
    }

    [Fact]
    public void UniversalSearchDistinguishesIdenticalSignalsOnDifferentCanChannels()
    {
        using var ui = new GuiSession();
        File.AppendAllText(ui.TracePath,
            "\n0.600000 2 100 Rx d 8 41 28 00 00 00 00 00 00\n");
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Click("globalSearch.open");
        var window = ui.WindowWithTitle("Search");
        var box = ui.Until(() => window.FindFirstDescendant(
            cf => cf.ByAutomationId("globalSearch.query")), "global search input");
        box.AsTextBox().Text = "EngCoolantTemp";
        var actions = ui.Until(() =>
        {
            var buttons = window.FindAllDescendants(
                cf => cf.ByAutomationId("globalSearch.openResult"));
            return buttons.Length >= 2 ? buttons : null;
        }, "separate recorded source results for CAN 1 and CAN 2");
        // Same-name search results sort by exact name, then physical CAN
        // channel; the second recorded result is the channel-two source.
        actions[1].Press();
        ui.Until(() => ui.Visible("signals.selectedSource").Name.Contains("CAN 2")
            ? "second physical CAN bus selected" : null, "global search exact source");
        Assert.Contains("EngCoolantTemp", ui.Visible("signals.selectedSource").Name);

        // The local filter accepts multiple source-specific terms, and all
        // must resolve on the SAME channel / DBC signal.
        ui.Fill("signals.search", "ch2 0x100 EngCoolantTemp");
        ui.Until(() => ui.Visible("signals.grid")
            .FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length == 1
            ? "one exact channel-two match" : null,
            "space-separated filters isolate a physical CAN source");
        Assert.Contains("CAN 2", ui.Visible("signals.selectedSource").Name);
    }

    [Fact]
    public void ChangeNavigationCanOpenExactRecordedSourceFrame()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");

        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.signals");
        var grid = ui.Visible("signals.grid");
        ui.Until(() => grid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length >= 4
            ? "observed series" : null, "signal selection");

        // At trace tail, previous change must find an actual recorded CAN frame;
        // the source-frame command cannot jump to an inferred DBC-only value.
        ui.Click("signals.previousChange");
        ui.Click("signals.openSourceFrame");
        Assert.False(ui.Visible("trace.grid").IsOffscreen);
    }

    [Fact]
    public void DbcInspectorQuickPlotOpensDedicatedChartForObservedSignal()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.dbc");
        ui.Click("dbc.quickPlot");
        Assert.NotNull(ui.WindowWithTitle("Signal detail -"));
    }

    [Fact]
    public void DbcObservedOnlyFiltersUntransmittedDefinitionsWithoutEditingTheSource()
    {
        using var ui = new GuiSession();
        File.AppendAllText(ui.DbcPath, """

            BO_ 768 UntransmittedData: 8 ECM
             SG_ UntransmittedSignal : 0|8@1+ (1,0) [0|255] "" Vector__XXX
            """);
        var originalDbc = File.ReadAllText(ui.DbcPath);
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.dbc");
        var messages = ui.Visible("dbc.messagesGrid");
        ui.Until(() => messages.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length == 3
            ? "three DBC messages" : null, "untransmitted definition visible in full DBC catalog");
        ui.Click("dbc.observedOnly");
        ui.Until(() => messages.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length == 2
            ? "two observed messages" : null, "unobserved message excluded from evidence lens");

        Assert.Equal(originalDbc, File.ReadAllText(ui.DbcPath));
    }

    [Fact]
    public void PlottedOnlyFilterTracksChangesToSelectedSignals()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.signals");
        var grid = ui.Visible("signals.grid");
        ui.Until(() => grid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length >= 4
            ? "observed series loaded" : null, "signal catalog populated");
        ui.Click("signals.plotVisible");
        ui.Click("signals.plottedOnly");
        ui.Until(() => grid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length >= 4
            ? "selected signals visible" : null, "selected series retained by plotted-only filter");
        ui.Click("signals.clearPlot");
        ui.Until(() => grid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length == 0
            ? "plot selection cleared" : null, "plotted-only filter tracks clearing selections");
    }
}
