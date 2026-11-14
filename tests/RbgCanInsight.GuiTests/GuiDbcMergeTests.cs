using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Real-desktop acceptance of the non-destructive DBC merge workflow.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiDbcMergeTests
{
    private static string IncomingDbc =>
        Fixtures.Dbc.Replace("VAL_ 256 Mode", "BO_ 999 MergeProbe: 8 ECM\n SG_ MergeSig : 0|8@1+ (1,0) [0|255] \"\" Vector__XXX\n\nVAL_ 256 Mode")
            .Replace(" SG_ Mode : 16|8@1+ (1,0) [0|3] \"\" Vector__XXX",
                     " SG_ Mode : 16|8@1+ (1,0) [0|3] \"\" Vector__XXX\n SG_ Extra : 24|8@1+ (2,0) [0|500] \"V\" Vector__XXX");

    [Fact]
    public void MergeDialogAddsReviewedDefinitionsToANewFileAndNeverTouchesSources()
    {
        using var ui = new GuiSession();
        var incomingPath = Path.Combine(ui.Root, "incoming.dbc");
        File.WriteAllText(incomingPath, IncomingDbc);
        var mergedPath = Path.Combine(ui.Root, "merged.dbc");

        ui.Navigate("nav.dbc");
        ui.Click("dbc.merge");
        var window = ui.WindowWithTitle("Merge DBC versions");
        FlaUI.Core.AutomationElements.AutomationElement Control(string id) =>
            ui.Until(() => window.FindFirstDescendant(cf => cf.ByAutomationId(id)), "merge control " + id);

        Control("dbcMerge.chooseBase").Press();
        ui.ChooseFile(ui.DbcPath);
        Control("dbcMerge.chooseIncoming").Press();
        ui.ChooseFile(incomingPath);

        ui.Until(() => Control("dbcMerge.summary").Name.Contains("additions") ? "planned" : null, "merge plan");
        Assert.Contains("2 additions", Control("dbcMerge.summary").Name);   // message + signal
        Assert.Contains("0 conflicting", Control("dbcMerge.summary").Name);
        var grid = Control("dbcMerge.grid");
        Assert.True(grid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length >= 2);

        Control("dbcMerge.save").Press();
        ui.ChooseFile(mergedPath);
        ui.Until(() => Control("dbcMerge.status").Name.Contains("Merged DBC saved") ? "saved" : null,
            "merge saved", 40);

        var merged = File.ReadAllText(mergedPath);
        Assert.Contains("MergeSig", merged);
        Assert.Contains("SG_ Extra", merged);
        Assert.Equal(Fixtures.Dbc, File.ReadAllText(ui.DbcPath));        // base untouched
        Assert.Equal(IncomingDbc, File.ReadAllText(incomingPath));       // incoming untouched
    }

    [Fact]
    public void MergeDialogRefusesTheSameFileForBaseAndIncoming()
    {
        using var ui = new GuiSession();
        ui.Navigate("nav.dbc");
        ui.Click("dbc.merge");
        var window = ui.WindowWithTitle("Merge DBC versions");
        FlaUI.Core.AutomationElements.AutomationElement Control(string id) =>
            ui.Until(() => window.FindFirstDescendant(cf => cf.ByAutomationId(id)), "merge control " + id);
        Control("dbcMerge.chooseBase").Press();
        ui.ChooseFile(ui.DbcPath);
        Control("dbcMerge.chooseIncoming").Press();
        ui.ChooseFile(ui.DbcPath);
        ui.Until(() => Control("dbcMerge.status").Name.Contains("two different files") ? "refused" : null, "same-file refusal");
        Assert.False(Control("dbcMerge.save").IsEnabled);
    }
}
