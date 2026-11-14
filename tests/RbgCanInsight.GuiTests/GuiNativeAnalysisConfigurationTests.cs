using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

[Trait("Category", "WindowsFlaUI")]
public sealed class GuiNativeAnalysisConfigurationTests
{
    [Fact]
    public void NativeConfigurationRoundTripsPlottedSignalsThroughPreview()
    {
        using var ui = new GuiSession();
        LoadSignals(ui);
        ui.Navigate("nav.signals");
        ui.Click("signals.plotVisible");
        ui.Until(() => ui.Visible("signals.summary").Name.Contains("4 plotted here", StringComparison.OrdinalIgnoreCase)
            ? "four curves plotted" : null, "initial signal curves");

        var configPath = Path.Combine(ui.Root, "roundtrip.rbgconfig");
        ui.Navigate("nav.dashboard");
        OpenUtilities(ui, "utilities.saveAnalysisConfiguration");
        ui.Click("utilities.saveAnalysisConfiguration");
        ui.ChooseFile(configPath);
        ui.AwaitStatus("Saved 4 signal binding(s)");
        Assert.True(File.Exists(configPath));

        ui.Navigate("nav.signals");
        ui.Click("signals.clearPlot");
        ui.Until(() => ui.Visible("signals.summary").Name.Contains("0 plotted here", StringComparison.OrdinalIgnoreCase)
            ? "existing plot cleared for restoration check" : null, "empty plot before config load");

        ui.Navigate("nav.dashboard");
        OpenUtilities(ui, "utilities.loadAnalysisConfiguration");
        ui.Click("utilities.loadAnalysisConfiguration");
        ui.ChooseFile(configPath);
        var preview = ui.WindowWithTitle("Preview analysis configuration");
        Assert.NotNull(preview.FindFirstDescendant(cf => cf.ByAutomationId("analysisConfiguration.preview.grid")));
        var apply = preview.FindFirstDescendant(cf => cf.ByAutomationId("analysisConfiguration.preview.apply"));
        Assert.NotNull(apply);
        apply!.Press();

        ui.AwaitStatus("Applied 4 explicit signal mapping(s)");
        ui.Navigate("nav.signals");
        ui.Until(() => ui.Visible("signals.summary").Name.Contains("4 plotted here", StringComparison.OrdinalIgnoreCase)
            ? "curves restored" : null, "configuration restores plotted signals");
    }

    [Fact]
    public void NativeConfigurationRestoresOnASecondRecordingWithTheSameSignalIdentity()
    {
        using var ui = new GuiSession();
        LoadSignals(ui);
        ui.Navigate("nav.signals");
        ui.Click("signals.plotVisible");
        ui.Until(() => ui.Visible("signals.summary").Name.Contains("4 plotted here", StringComparison.OrdinalIgnoreCase)
            ? "four curves plotted" : null, "initial signal curves");

        var configPath = Path.Combine(ui.Root, "portable.rbgconfig");
        ui.Navigate("nav.dashboard");
        OpenUtilities(ui, "utilities.saveAnalysisConfiguration");
        ui.Click("utilities.saveAnalysisConfiguration");
        ui.ChooseFile(configPath);
        ui.AwaitStatus("Saved 4 signal binding(s)");

        var secondPath = Path.Combine(ui.Root, "second-recording.asc");
        var secondContents = Fixtures.Trace.Replace(
            "0.000000 1 100 Rx d 8 32 20",
            "0.000000 1 100 Rx d 8 42 21",
            StringComparison.Ordinal);
        File.WriteAllText(secondPath, secondContents);
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(secondPath);
        ui.AwaitStatus("Trace loaded:");
        Assert.Equal(secondContents, File.ReadAllText(secondPath));

        ui.Navigate("nav.signals");
        // A trace change may retain previously plotted identities. Clear them
        // explicitly so the following assertion tests restoration by the file.
        ui.Click("signals.clearPlot");
        ui.Until(() => ui.Visible("signals.summary").Name.Contains("0 plotted here", StringComparison.OrdinalIgnoreCase)
            ? "new recording has no inherited plot selection" : null, "empty plot on second recording");
        ui.Navigate("nav.dashboard");
        OpenUtilities(ui, "utilities.loadAnalysisConfiguration");
        ui.Click("utilities.loadAnalysisConfiguration");
        ui.ChooseFile(configPath);
        var preview = ui.WindowWithTitle("Preview analysis configuration");
        var summary = preview.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text));
        Assert.NotNull(summary);
        Assert.Contains("4 exact unique", summary!.Name, StringComparison.OrdinalIgnoreCase);
        var apply = preview.FindFirstDescendant(cf => cf.ByAutomationId("analysisConfiguration.preview.apply"));
        Assert.NotNull(apply);
        apply!.Press();

        ui.AwaitStatus("Applied 4 explicit signal mapping(s)");
        ui.Navigate("nav.signals");
        ui.Until(() => ui.Visible("signals.summary").Name.Contains("4 plotted here", StringComparison.OrdinalIgnoreCase)
            ? "portable curves restored" : null, "signal configuration applied to the second recording");
    }

    [Fact]
    public void MissingBindingCanBeMappedExplicitlyAndSavedForLaterLoads()
    {
        using var ui = new GuiSession();
        LoadSignals(ui);
        ui.Navigate("nav.signals");
        ui.Click("signals.plotVisible");
        ui.Until(() => ui.Visible("signals.summary").Name.Contains("4 plotted here", StringComparison.OrdinalIgnoreCase)
            ? "four curves plotted" : null, "initial signal curves");

        var configPath = Path.Combine(ui.Root, "source.rbgconfig");
        ui.Navigate("nav.dashboard");
        OpenUtilities(ui, "utilities.saveAnalysisConfiguration");
        ui.Click("utilities.saveAnalysisConfiguration");
        ui.ChooseFile(configPath);
        ui.AwaitStatus("Saved 4 signal binding(s)");

        var partialPath = Path.Combine(ui.Root, "partial-recording.asc");
        const string partialTrace = """
            date Tue Jul 07 10:00:00.000 2026
            base hex  timestamps absolute
            internal events logged
            0.000000 1 100 Rx d 8 32 20 00 00 00 00 00 00
            0.100000 1 100 Rx d 8 37 28 00 00 00 00 00 00
            0.200000 1 100 Rx d 8 3C 30 00 00 00 00 00 00
            """;
        File.WriteAllText(partialPath, partialTrace);
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(partialPath);
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.dashboard");
        OpenUtilities(ui, "utilities.loadAnalysisConfiguration");
        ui.Click("utilities.loadAnalysisConfiguration");
        ui.ChooseFile(configPath);
        var preview = ui.WindowWithTitle("Preview analysis configuration");
        var summary = preview.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text));
        Assert.NotNull(summary);
        Assert.Contains("1 missing", summary!.Name, StringComparison.OrdinalIgnoreCase);
        var targetSelectors = preview.FindAllDescendants(cf => cf.ByAutomationId("analysisConfiguration.preview.target"));
        Assert.Equal(4, targetSelectors.Length);
        // SignalTimelineBuilder orders the fixture's three EngineData signals
        // before VehicleData.VehicleSpeed, whose binding is therefore row 4.
        targetSelectors[3].AsComboBox().Select(1); // explicitly map it to EngineData.EngineLoad

        var apply = preview.FindFirstDescendant(cf => cf.ByAutomationId("analysisConfiguration.preview.apply"));
        Assert.NotNull(apply);
        apply!.Press();
        var mappedPath = Path.Combine(ui.Root, "source-mapped.rbgconfig");
        ui.ChooseFile(mappedPath);
        ui.AwaitStatus("Manual mappings saved to a new configuration");
        Assert.True(File.Exists(mappedPath));

        // The explicit remap is now an exact binding on this trace and should
        // load without another missing row or manual choice.
        ui.Navigate("nav.dashboard");
        OpenUtilities(ui, "utilities.loadAnalysisConfiguration");
        ui.Click("utilities.loadAnalysisConfiguration");
        ui.ChooseFile(mappedPath);
        var remappedPreview = ui.WindowWithTitle("Preview analysis configuration");
        var remappedSummary = remappedPreview.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text));
        Assert.NotNull(remappedSummary);
        Assert.Contains("4 exact unique", remappedSummary!.Name, StringComparison.OrdinalIgnoreCase);
        remappedPreview.FindFirstDescendant(cf => cf.ByAutomationId("analysisConfiguration.preview.apply"))!.Press();
        ui.AwaitStatus("Applied 4 explicit signal mapping(s)");
    }

    [Fact]
    public void CfgCandidateIsReportedUnsupportedWithoutChangingSource()
    {
        using var ui = new GuiSession();
        var cfgPath = Path.Combine(ui.Root, "candidate.cfg");
        const string contents = "opaque Vector configuration candidate; probe must not parse this text";
        File.WriteAllText(cfgPath, contents);

        ui.Navigate("nav.dashboard");
        OpenUtilities(ui, "utilities.loadAnalysisConfiguration");
        ui.Click("utilities.loadAnalysisConfiguration");
        ui.ChooseFile(cfgPath);
        var report = ui.WindowWithTitle("Vector configuration not imported");
        var ok = report.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button));
        Assert.NotNull(ok);
        ok!.Patterns.Invoke.Pattern.Invoke();
        ui.Until(() => ui.IsWindowOpen("Vector configuration not imported") ? null : "closed",
            "unsupported configuration report closed");

        ui.AwaitStatus("unsupported; no contents were read");
        Assert.Equal(contents, File.ReadAllText(cfgPath));
    }

    private static void LoadSignals(GuiSession ui)
    {
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");

        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
    }

    private static void OpenUtilities(GuiSession ui, string buttonId)
    {
        ui.Visible("utilities.panel").Patterns.ExpandCollapse.Pattern.Expand();
        var button = ui.Until(() => ui.Find(buttonId), "visible utility button " + buttonId);
        if (button.IsOffscreen)
            button.Patterns.ScrollItem.Pattern.ScrollIntoView();
        Assert.False(button.IsOffscreen);
    }
}
