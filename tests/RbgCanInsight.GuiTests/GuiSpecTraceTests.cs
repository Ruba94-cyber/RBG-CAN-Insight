using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Real-desktop acceptance of the declared-vs-observed diagnostic panel.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiSpecTraceTests
{
    // Fixture trace (frames 0..7) plus one DID (0xF190) that no loaded catalog declares (frames 8, 9).
    private static string TraceWithUndeclaredDid =>
        Fixtures.Trace.TrimEnd() + "\n0.600000 1 7E0 Rx d 8 03 22 F1 90 00 00 00 00\n" +
        "0.610000 1 7E8 Rx d 8 05 62 F1 90 00 5A 00 00\n";

    [Fact]
    public void SpecVsTracePanelListsUndeclaredDidWithFramesAndOpensTheSourceFrame()
    {
        using var ui = new GuiSession();
        var tracePath = Path.Combine(ui.Root, "spec-trace.asc");
        File.WriteAllText(tracePath, TraceWithUndeclaredDid);

        ui.Navigate("nav.mapping");
        ui.Click("mapping.loadCatalog");
        ui.ChooseFile(ui.OdxPath);
        ui.AwaitMappingStatus("from 1 file");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(tracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.uds");
        ui.Click("uds.specVsTrace");
        var window = ui.WindowWithTitle("Spec vs trace");
        AutomationElement Control(string id) =>
            ui.Until(() => window.FindFirstDescendant(cf => cf.ByAutomationId(id)), "spec control " + id);

        ui.Until(() => Control("specTrace.summary").Name.Contains("diagnostic transactions") ? "ready" : null,
            "spec-vs-trace summary");
        Assert.Contains("positive 0x10/0x11/0x27 responses", Control("specTrace.limitations").Name);

        var grid = Control("specTrace.grid");
        var row = ui.Until(() => grid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))
            .FirstOrDefault(item => item.FindAllDescendants(cf => cf.ByName("0xF190")).Length > 0),
            "finding row for the undeclared DID 0xF190");
        row.Press();
        Assert.NotEmpty(row.FindAllDescendants(cf => cf.ByName("DID used, not in catalog")));
        Assert.NotEmpty(row.FindAllDescendants(cf => cf.ByName("9 10")));     // 1-based proving frames

        Assert.True(Control("specTrace.openFrame").IsEnabled);
        Control("specTrace.openFrame").Press();
        Assert.False(ui.Visible("trace.grid").IsOffscreen);                   // jumped to the Trace view
    }

    [Fact]
    public void SpecVsTracePanelExplainsWhenNothingIsLoaded()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.uds");
        ui.Click("uds.specVsTrace");
        var window = ui.WindowWithTitle("Spec vs trace");
        var summary = ui.Until(() => window.FindFirstDescendant(cf => cf.ByAutomationId("specTrace.summary")), "summary");
        Assert.Contains("Nothing to compare yet", summary.Name);
    }
}
