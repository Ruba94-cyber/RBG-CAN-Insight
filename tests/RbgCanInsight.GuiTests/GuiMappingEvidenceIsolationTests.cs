using Xunit;

namespace RbgCanInsight.GuiTests;

[Trait("Category", "WindowsFlaUI")]
public sealed class GuiMappingEvidenceIsolationTests
{
    [Fact]
    public void MultifileMappingDisclosesMissingSourceQualifiedDidEvidence()
    {
        using var ui = new GuiSession();
        var other = Path.Combine(ui.Root, "other.asc");
        File.Copy(ui.TracePath, other);
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFiles(ui.TracePath, other);
        var mode = ui.WindowWithTitle("Merge mode");
        mode.FindFirstDescendant(cf => cf.ByAutomationId("merge.ok"))!
            .Patterns.Invoke.Pattern.Invoke();
        ui.AwaitStatus("Trace loaded:");
        ui.Navigate("nav.mapping");
        ui.Click("mapping.loadCatalog");
        ui.ChooseFile(ui.OdxPath);
        ui.AwaitMappingStatus("DID trace evidence is unavailable with multiple recordings");
        ui.Click("mapping.correlate");
        ui.AwaitMappingStatus("DID trace evidence unavailable for multiple recordings");
        Assert.DoesNotContain("Load a trace to add on-bus evidence", ui.Visible("mapping.status").Name);
    }

    [Fact]
    public void ChangingTraceRequiresFreshCorrelationBeforeSavingMap()
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
        ui.Navigate("nav.mapping");
        ui.Click("mapping.loadCatalog");
        ui.ChooseFile(ui.OdxPath);
        ui.AwaitMappingStatus("from 1 file");
        ui.Click("mapping.correlate");
        ui.AwaitMappingStatus("Auto-correlation done");

        var other = Path.Combine(ui.Root, "replacement.asc");
        File.Copy(ui.TracePath, other);
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(other);
        ui.AwaitStatus("Trace loaded:");
        ui.Navigate("nav.mapping");
        ui.Click("mapping.save");
        ui.AwaitMappingStatus("Run Auto-correlate before saving");
        ui.Click("mapping.correlate");
        ui.AwaitMappingStatus("Auto-correlation done");
        var mapPath = Path.Combine(ui.Root, "recomputed.rbgmap.json");
        ui.Click("mapping.save");
        ui.ChooseFile(mapPath);
        ui.Until(() => File.Exists(mapPath) ? "saved" : null, "recomputed map saved");
    }
}
