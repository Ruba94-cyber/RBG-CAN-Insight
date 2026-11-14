using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Interactive Windows-only checks for the actual DBC editor controls.
/// All input files and copied output use synthetic data.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiDbcUndoRedoTests
{
    [Fact]
    public void UndoAndRedoStagedSignalChangeUpdateButtonsAndNeverModifySourceFile()
    {
        using var ui = OpenEditor();
        var original = ui.Text("dbc.editorFactor");
        Assert.False(ui.Visible("dbc.undo").IsEnabled);
        Assert.False(ui.Visible("dbc.redo").IsEnabled);
        ui.Fill("dbc.editorFactor", "2.5");
        ui.Click("dbc.stageEdit");
        ui.Until(() => ui.Find("dbc.undo") is { IsEnabled: true } ? "ready" : null,
            "undo enabled after stage");
        Assert.True(ui.Visible("dbc.saveCopy").IsEnabled);

        ui.Click("dbc.undo");
        ui.Until(() => ui.Text("dbc.editorFactor") == original ? "restored" : null,
            "original scale restored in DBC editor");
        Assert.False(ui.Visible("dbc.saveCopy").IsEnabled);
        Assert.True(ui.Visible("dbc.redo").IsEnabled);

        ui.Click("dbc.redo");
        ui.Until(() => ui.Text("dbc.editorFactor") == "2.5" ? "redone" : null,
            "edited scale re-applied");
        Assert.True(ui.Visible("dbc.saveCopy").IsEnabled);
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
    }

    [Fact]
    public void NewStagedChangeAfterUndoInvalidatesRedoBranch()
    {
        using var ui = OpenEditor();
        ui.Fill("dbc.editorFactor", "2");
        ui.Click("dbc.stageEdit");
        ui.Until(() => ui.Find("dbc.undo") is { IsEnabled: true } ? "staged" : null,
            "undo enabled");
        ui.Click("dbc.undo");
        Assert.True(ui.Visible("dbc.redo").IsEnabled);
        ui.Fill("dbc.editorFactor", "3");
        ui.Click("dbc.stageEdit");
        ui.Until(() => ui.Find("dbc.redo") is { IsEnabled: false } ? "invalidated" : null,
            "redo invalidated by new stage");
        Assert.Equal("3", ui.Text("dbc.editorFactor"));
        Assert.True(ui.Visible("dbc.saveCopy").IsEnabled);
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
    }

    [Fact]
    public void UndoRedoHistorySurvivesNavigatingAwayAndReturningToDbcEditor()
    {
        using var ui = OpenEditor();
        ui.Fill("dbc.editorFactor", "4");
        ui.Click("dbc.stageEdit");
        ui.Until(() => ui.Find("dbc.undo") is { IsEnabled: true } ? "staged" : null,
            "undo after stage");
        ui.Navigate("nav.trace");
        ui.Navigate("nav.dbc");
        ui.Visible("dbc.editorTab").AsTabItem().Select();
        Assert.True(ui.Visible("dbc.undo").IsEnabled);
        ui.Click("dbc.undo");
        Assert.False(ui.Visible("dbc.saveCopy").IsEnabled);
        Assert.True(ui.Visible("dbc.redo").IsEnabled);
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));
    }

    private static GuiSession OpenEditor()
    {
        var ui = new GuiSession();
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");
        var message = ui.Until(() => ui.Visible("dbc.messagesGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "first DBC message");
        message.Press();
        var signal = ui.Until(() => ui.Visible("dbc.signalsGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "first signal");
        signal.Press();
        ui.Visible("dbc.editorTab").AsTabItem().Select();
        ui.Visible("dbc.editorFactor");
        ui.Visible("dbc.undo");
        return ui;
    }
}
