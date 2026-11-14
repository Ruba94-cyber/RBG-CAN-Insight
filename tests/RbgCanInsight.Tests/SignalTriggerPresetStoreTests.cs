using System.Text.Json;
using RbgCanInsight.Analysis;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class SignalTriggerPresetStoreTests
{
    private static readonly SignalTriggerRule StandardRule = new(
        "synthetic.dbc|ch1|00000123|S|EngineData.Mode",
        SignalTriggerMode.CrossesAbove, Threshold: 1,
        ChangeTolerance: 0, MinimumEventSeparationNanoseconds: 10_000_000);

    [Fact]
    public void MissingConfigLoadsAsEmptyWithoutCreatingAnyFiles()
    {
        using var test = new Sandbox();
        Assert.Empty(test.Store.ReadAll());
        Assert.False(File.Exists(test.PresetsPath));
        Assert.False(test.Store.Remove("Not saved"));
        Assert.False(File.Exists(test.PresetsPath));
    }

    [Fact]
    public void SaveAndReloadPreservesExactChannelScopedSignalAndAllRuleFields()
    {
        using var test = new Sandbox();
        var values = test.Store.Save("Threshold crossing", StandardRule);
        var stored = Assert.Single(values);
        Assert.Equal("Threshold crossing", stored.Name);
        Assert.Equal(StandardRule, stored.Rule);

        var reloaded = new SignalTriggerPresetStore(test.PresetsPath).ReadAll();
        Assert.Equal(stored, Assert.Single(reloaded));
        using var json = JsonDocument.Parse(File.ReadAllText(test.PresetsPath));
        Assert.Equal(1, json.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Single(json.RootElement.GetProperty("Presets").EnumerateArray());
    }

    [Fact]
    public void SavingExistingNameReplacesOnlyThatRuleAndKeepsOtherRules()
    {
        using var test = new Sandbox();
        test.Store.Save("Rule A", StandardRule);
        test.Store.Save("Rule B", StandardRule with
        {
            SignalKey = "different.dbc|ch2|00000200|S|Message.Signal"
        });
        var updated = StandardRule with { Threshold = 4, Mode = SignalTriggerMode.CrossesBelow };
        var items = test.Store.Save("rule a", updated);
        Assert.Equal(2, items.Count);
        Assert.Equal(updated, items.Single(item => item.Name.Equals("rule a",
            StringComparison.OrdinalIgnoreCase)).Rule);
        Assert.Equal("different.dbc|ch2|00000200|S|Message.Signal",
            items.Single(item => item.Name == "Rule B").Rule.SignalKey);
    }

    [Fact]
    public void NamedStateRulePersistsAndIsRemovedWithoutTouchingUnrelatedFiles()
    {
        using var test = new Sandbox();
        var source = Path.Combine(test.Root, "capture.asc");
        File.WriteAllText(source, "synthetic input remains unchanged");
        var rule = StandardRule with
        {
            Mode = SignalTriggerMode.EntersNamedState,
            NamedState = "Running"
        };
        test.Store.Save("Enter running", rule);
        Assert.Equal("Running", Assert.Single(test.Store.ReadAll()).Rule.NamedState);
        Assert.True(test.Store.Remove("enter RUNNING"));
        Assert.Empty(test.Store.ReadAll());
        Assert.Equal("synthetic input remains unchanged", File.ReadAllText(source));
    }

    [Fact]
    public void CorruptConfigurationCannotBeSilentlyOverwrittenBySaveOrDelete()
    {
        using var test = new Sandbox();
        File.WriteAllText(test.PresetsPath, "{\"SchemaVersion\":");
        var bytesBefore = File.ReadAllBytes(test.PresetsPath);
        Assert.Throws<JsonException>(() => test.Store.ReadAll());
        Assert.Throws<JsonException>(() => test.Store.Save("New", StandardRule));
        Assert.Throws<JsonException>(() => test.Store.Remove("New"));
        Assert.Equal(bytesBefore, File.ReadAllBytes(test.PresetsPath));
    }

    [Fact]
    public void UnsupportedFutureSchemaIsPreservedAndRejectedExplicitly()
    {
        using var test = new Sandbox();
        File.WriteAllText(test.PresetsPath, "{\"SchemaVersion\":99,\"Presets\":[]}");
        Assert.Throws<NotSupportedException>(() => test.Store.ReadAll());
        Assert.Throws<NotSupportedException>(() =>
            test.Store.Save("Keep", StandardRule));
        Assert.Contains("\"SchemaVersion\":99", File.ReadAllText(test.PresetsPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Invalid\nLine")]
    public void InvalidRuleNamesCannotBeSaved(string name)
    {
        using var test = new Sandbox();
        Assert.Throws<ArgumentException>(() => test.Store.Save(name, StandardRule));
        Assert.False(File.Exists(test.PresetsPath));
    }

    [Fact]
    public void InvalidNumbersUnknownModesOrMissingNamedStateAreRejected()
    {
        using var test = new Sandbox();
        Assert.Throws<ArgumentException>(() =>
            test.Store.Save("Nan", StandardRule with { Threshold = double.NaN }));
        Assert.Throws<ArgumentException>(() =>
            test.Store.Save("Negative separation", StandardRule with
            { MinimumEventSeparationNanoseconds = -1 }));
        Assert.Throws<ArgumentException>(() =>
            test.Store.Save("Unknown enum", StandardRule with { Mode = (SignalTriggerMode)999 }));
        Assert.Throws<ArgumentException>(() =>
            test.Store.Save("Missing state", StandardRule with
            { Mode = SignalTriggerMode.LeavesNamedState, NamedState = null }));
        Assert.False(File.Exists(test.PresetsPath));
    }

    [Fact]
    public void ExistingConfigIsNeverOverwrittenIfItExceedsSafeSize()
    {
        using var test = new Sandbox();
        File.WriteAllText(test.PresetsPath, new string('x', 1_048_577));
        Assert.Throws<InvalidDataException>(() => test.Store.ReadAll());
        Assert.Throws<InvalidDataException>(() => test.Store.Save("New", StandardRule));
        Assert.Equal(1_048_577L, new FileInfo(test.PresetsPath).Length);
    }

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "rbg-trigger-config-" + Guid.NewGuid().ToString("N"));
        public string PresetsPath => Path.Combine(Root, "rules.json");
        public SignalTriggerPresetStore Store { get; }

        public Sandbox()
        {
            Directory.CreateDirectory(Root);
            Store = new SignalTriggerPresetStore(PresetsPath);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
