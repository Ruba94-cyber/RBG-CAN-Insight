using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Offline-only real Windows/UIA checks for ambiguous UDS evidence.
/// No OEM source data, active diagnostic traffic or ECU connection is used.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiUdsSourceEvidenceTests
{
    [Fact]
    public void AmbiguousNegativeResponseIsVisibleAsUnassignedAndKeepsOriginalFrameEvidence()
    {
        using var ui = new GuiSession();
        const string trace = """
            date Tue Jul 07 10:00:00.000 2026
            base hex  timestamps absolute
            internal events logged
            0.000000 1 7E0 Rx d 8 03 22 F1 90 00 00 00 00
            0.001000 1 7E0 Rx d 8 03 22 F1 87 00 00 00 00
            0.002000 1 7E8 Rx d 8 03 7F 22 31 00 00 00 00
            """;
        File.WriteAllText(ui.TracePath, trace);
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        ui.Navigate("nav.uds");
        ui.Click("uds.viewTransactions");

        // The user must see that the negative reply cannot uniquely identify
        // either DID. Filtering by the exact UX terminology must reveal just
        // the original response and the evidence panel, not a guessed ECU.
        ui.Fill("uds.search", "Unassigned NRC");
        var grid = ui.Visible("uds.transactionsGrid");
        var row = ui.Until(() =>
        {
            var visible = grid.FindAllDescendants(
                cf => cf.ByControlType(ControlType.DataItem));
            return visible.Length == 1 ? visible[0] : null;
        }, "one unassigned NRC instead of a falsely attributed negative request");
        Assert.False(row.IsOffscreen);
        ui.Until(() => grid.FindFirstDescendant(
            cf => cf.ByName("Unassigned NRC · multiple possible requests")),
            "accessible ambiguous NRC outcome");
        row.Press();

        var evidence = ui.Visible("uds.selectedSourceEvidence");
        ui.Until(() => evidence.Name.Contains("Ambiguous", StringComparison.Ordinal)
                     && evidence.Name.Contains("3", StringComparison.Ordinal)
                     ? "reason and original third source frame are visible"
                     : null,
                 "ambiguous NRC retains original response frame number");
        Assert.Equal(trace, File.ReadAllText(ui.TracePath));
    }
}
