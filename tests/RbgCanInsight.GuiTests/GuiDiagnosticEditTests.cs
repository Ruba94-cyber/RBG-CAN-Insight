using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Real-desktop acceptance of the safe CDD/ODX description editor.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiDiagnosticEditTests
{
    [Fact]
    public void EditedDescriptionIsSavedToANewFileAndTheSourceStaysUnchanged()
    {
        using var ui = new GuiSession();
        var destination = Path.Combine(ui.Root, "fixture-edited.odx");

        ui.Navigate("nav.mapping");
        ui.Click("mapping.loadCatalog");
        ui.ChooseFile(ui.OdxPath);
        ui.AwaitMappingStatus("from 1 file");
        ui.Navigate("nav.uds");
        ui.Click("uds.openCatalog");
        var grid = ui.Visible("diagnostics.catalogGrid");
        var row = ui.Until(() => grid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))
            .FirstOrDefault(item => item.FindAllDescendants(cf => cf.ByName("0xF405")).Length > 0),
            "catalog row for DID 0xF405");
        row.Press();

        ui.Click("diagnostics.edit");
        var window = ui.WindowWithTitle("Edit diagnostic text");
        AutomationElement Control(string id) =>
            ui.Until(() => window.FindFirstDescendant(cf => cf.ByAutomationId(id)), "edit control " + id);

        Assert.Contains("0xF405", Control("diagnosticEdit.header").Name);
        Assert.False(Control("diagnosticEdit.save").IsEnabled);                  // nothing changed yet
        Control("diagnosticEdit.longName").AsTextBox().Text = "Engine coolant temperature (edited)";
        Control("diagnosticEdit.description").AsTextBox().Text = "Edited through the UI";
        ui.Until(() => Control("diagnosticEdit.save").IsEnabled ? "enabled" : null, "save enabled after a change");

        Control("diagnosticEdit.save").Press();
        ui.ChooseFile(destination);
        ui.Until(() => Control("diagnosticEdit.status").Name.Contains("Edited copy saved") ? "saved" : null,
            "edited copy saved", 40);

        var edited = File.ReadAllText(destination);
        Assert.Contains("<LONG-NAME>Engine coolant temperature (edited)</LONG-NAME>", edited);
        Assert.Contains("Edited through the UI", edited);
        Assert.Contains("<SHORT-NAME>Read_EngineCoolantTemperature</SHORT-NAME>", edited);   // rest of the service kept
        Assert.Equal(Fixtures.Odx, File.ReadAllText(ui.OdxPath));                             // source untouched
    }

    [Fact]
    public void EditIsRefusedForPackagesAndExplainsWhy()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.uds");
        ui.Click("uds.openCatalog");
        ui.Click("diagnostics.edit");
        ui.Until(() => ui.Status.Contains("Select a DID definition") ? "refused" : null,
            "edit without a selected definition is refused");
    }
}
