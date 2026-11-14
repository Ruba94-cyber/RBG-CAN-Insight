using System.IO.Compression;
using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>True WPF/UIA3 checks of declared-versus-observed network semantics.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiNetworkMatrixTests
{
    [Fact]
    public void LoadedDbcNetworkMatrixCanFilterAndOpenExactDefinition()
    {
        using var ui = LoadedNetwork();
        OpenNetworkMatrix(ui);
        ui.Fill("statistics.networkMatrixSearch", "EngineData");
        var row = ui.Until(() => ui.Visible("statistics.networkMatrixGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "EngineData matrix record");
        row.Press();
        Assert.True(ui.Visible("statistics.networkMatrixInspect").IsEnabled);
        ui.Click("statistics.networkMatrixInspect");
        Assert.NotNull(ui.Visible("dbc.messagesGrid"));
        Assert.NotNull(ui.Visible("dbc.signalsGrid"));
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
    }

    [Fact]
    public void NetworkMatrixRemainsSearchableAfterReopeningStatistics()
    {
        using var ui = LoadedNetwork();
        OpenNetworkMatrix(ui);
        ui.Fill("statistics.networkMatrixSearch", "ECM");
        ui.Navigate("nav.trace");
        ui.Navigate("nav.statistics");
        OpenNetworkMatrix(ui);
        Assert.Equal("ECM", ui.Text("statistics.networkMatrixSearch"));
        Assert.NotNull(ui.Visible("statistics.networkMatrixGrid"));
        ui.Fill("statistics.networkMatrixSearch", "");
        Assert.Equal("", ui.Text("statistics.networkMatrixSearch"));
    }

    [Fact]
    public void TwoActiveDbcProfilesClaimingOneCanIdProduceAmbiguousIssue()
    {
        using var ui = LoadedNetwork();
        var duplicate = Path.Combine(ui.Root, "another-source.dbc");
        File.WriteAllText(duplicate, Fixtures.Dbc);
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(duplicate);
        ui.AwaitStatus("2 DBC");
        OpenNetworkMatrix(ui);
        ui.Visible("statistics.networkMatrixIssuesTab").AsTabItem().Select();
        Assert.NotNull(ui.Until(() => ui.Visible("statistics.networkMatrixIssuesGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "unassigned ambiguous CAN ID"));
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(duplicate));
    }

    [Fact]
    public void AnalysisReportIncludesNetworkEvidenceAndUnresolvedIdentityExport()
    {
        using var ui = LoadedNetwork();
        var utilities = ui.Visible("utilities.panel");
        utilities.Patterns.ExpandCollapse.Pattern.Expand();
        var target = Path.Combine(ui.Root, "network-evidence.zip");
        ui.Click("utilities.exportReport");
        ui.ChooseFile(target);
        ui.AwaitStatus("Analysis report exported:");
        ui.AwaitExport(target);
        using var zip = ZipFile.OpenRead(target);
        using var record = new StreamReader(zip.GetEntry("dbc-network-matrix.csv")!.Open());
        var csv = record.ReadToEnd();
        Assert.Contains("DeclaredTX", csv);
        Assert.Contains("EngineData", csv);
        Assert.NotNull(zip.GetEntry("dbc-network-matrix-issues.csv"));
        Assert.NotNull(zip.GetEntry("dbc-network-matrix-coverage.json"));
    }

    private static GuiSession LoadedNetwork()
    {
        var ui = new GuiSession();
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        return ui;
    }

    private static void OpenNetworkMatrix(GuiSession ui)
    {
        ui.Navigate("nav.statistics");
        ui.Visible("statistics.networkMatrixTab").AsTabItem().Select();
        Assert.NotNull(ui.Visible("statistics.networkMatrixSearch"));
        Assert.NotNull(ui.Visible("statistics.networkMatrixGrid"));
    }
}
