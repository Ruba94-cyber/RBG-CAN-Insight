using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Real-desktop acceptance of the loaded-trace versus reference-trace signal overlay.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiSignalOverlayTests
{
    // Loaded trace: EngCoolantTemp raw 0x32, 0x37, 0x3C, 0x41  (physical = raw - 40).
    private const string Loaded = """
        date Tue Jul 07 10:00:00.000 2026
        base hex  timestamps absolute
        internal events logged
        0.000000 1 100 Rx d 8 32 20 00 00 00 00 00 00
        0.100000 1 100 Rx d 8 37 20 00 00 00 00 00 00
        0.200000 1 100 Rx d 8 3C 20 00 00 00 00 00 00
        0.300000 1 100 Rx d 8 41 20 00 00 00 00 00 00
        """;

    // Reference recorded 100 s later in its own clock; the third sample differs (0x50 instead of 0x3C).
    private const string Reference = """
        date Tue Jul 07 10:00:00.000 2026
        base hex  timestamps absolute
        internal events logged
        100.000000 1 100 Rx d 8 32 20 00 00 00 00 00 00
        100.100000 1 100 Rx d 8 37 20 00 00 00 00 00 00
        100.200000 1 100 Rx d 8 50 20 00 00 00 00 00 00
        100.300000 1 100 Rx d 8 41 20 00 00 00 00 00 00
        """;

    [Fact]
    public void OverlayAlignsByTraceStartReportsTheDifferingSampleAndOpensItsFrame()
    {
        using var ui = new GuiSession();
        var loadedPath = Path.Combine(ui.Root, "loaded.asc");
        var referencePath = Path.Combine(ui.Root, "reference.asc");
        File.WriteAllText(loadedPath, Loaded);
        File.WriteAllText(referencePath, Reference);

        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(loadedPath);
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.statistics");
        Assert.False(ui.Visible("statistics.overlay").IsEnabled);          // nothing to overlay before a comparison
        ui.Until(() => ui.Main.FindFirstDescendant(cf => cf.ByName("Compare trace…")), "Compare trace button").Press();
        ui.ChooseFile(referencePath);
        ui.Until(() => ui.Visible("statistics.overlay").IsEnabled ? "ready" : null, "overlay available after compare", 60);

        ui.Click("statistics.overlay");
        var window = ui.WindowWithTitle("Signal overlay");
        AutomationElement Control(string id) =>
            ui.Until(() => window.FindFirstDescendant(cf => cf.ByAutomationId(id)), "overlay control " + id);

        ui.Until(() => Control("overlay.summary").Name.Contains("Different") ? "computed" : null, "overlay result");
        var summary = Control("overlay.summary").Name;
        Assert.Contains("max |Δ| 20", summary);                              // 0x3C vs 0x50 → 60 vs 80 raw
        Assert.Contains("No interpolation", Control("overlay.alignment").Name);

        var grid = Control("overlay.grid");
        var row = ui.Until(() => grid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).FirstOrDefault(),
            "largest difference row");
        row.Press();
        Assert.NotEmpty(row.FindAllDescendants(cf => cf.ByName("-20")));      // loaded − reference
        Assert.NotEmpty(row.FindAllDescendants(cf => cf.ByName("3")));        // 1-based frame in the loaded trace

        Assert.True(Control("overlay.openFrame").IsEnabled);
        Control("overlay.openFrame").Press();
        Assert.False(ui.Visible("trace.grid").IsOffscreen);
    }
}
