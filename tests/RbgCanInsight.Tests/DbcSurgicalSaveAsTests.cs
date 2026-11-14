using RbgCanInsight.Dbc;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DbcSurgicalSaveAsTests
{
    private const string Source =
        "VERSION \"test\"\r\n" +
        "NS_ :\r\n" +
        "BS_:\r\n" +
        "BU_: ECU Tester\r\n" +
        "\r\n" +
        "BO_ 291 Classic: 8 ECU\r\n" +
        " SG_ Speed : 8|16@1+ (0.01,0) [0|200] \"km/h\" Tester\r\n" +
        "BO_ 2147483939 Extended: 8 ECU\r\n" +
        " SG_ State : 0|8@1+ (1,0) [0|255] \"\" Tester\r\n" +
        "VAL_ 291 Speed 0 \"Stopped\" 1 \"Running\" ;\r\n" +
        "CM_ SG_ 291 Speed \"Sensor & <CAN>\";\r\n" +
        "CUSTOM_VENDOR_ \"OEM metadata must not move\";\r\n";

    private static (DbcWorkingCopy Copy, DbcDatabase Database) CreateCopy()
    {
        var database = new DbcParser().Parse("synthetic", new StringReader(Source));
        return (new DbcWorkingCopy(database), database);
    }

    [Fact]
    public void SaveAsPatchesOnlySelectedStandardSignalAndPreservesUnknownText()
    {
        var (copy, source) = CreateCopy();
        Assert.True(source.TryGetMessage(291, false, out var message));
        copy.StageSignal(new(291, false), "Speed",
            message.Signals[0] with { Factor = 0.02, StartBit = 16, Unit = "m/s" });
        var result = DbcSurgicalSaveAs.Generate(Source, copy);
        Assert.Contains(" SG_ Speed : 16|16@1+ (0.02,0) [0|200] \"m/s\" Tester\r\n", result);
        Assert.Contains("CUSTOM_VENDOR_ \"OEM metadata must not move\";\r\n", result);
        Assert.Contains("VAL_ 291 Speed 0 \"Stopped\" 1 \"Running\" ;\r\n", result);
        Assert.Contains("BO_ 2147483939 Extended: 8 ECU\r\n", result);
        Assert.Contains(" SG_ State : 0|8@1+ (1,0) [0|255] \"\" Tester\r\n", result);
        Assert.Equal(Source.Count(character => character == '\n'),
            result.Count(character => character == '\n'));
        var reloaded = new DbcParser().Parse("written", new StringReader(result));
        Assert.True(reloaded.TryGetMessage(291, false, out var updated));
        Assert.Equal(0.02, updated.Signals[0].Factor);
    }

    [Fact]
    public void RefusesStaleSourceOrMissingSignalInsteadOfInventingDefinitions()
    {
        var (copy, db) = CreateCopy();
        var signal = db.MessageIdentities![new(291, false)].Signals[0];
        copy.StageSignal(new(291, false), "Speed", signal with { Offset = 1 });
        Assert.Throws<InvalidDataException>(() =>
            DbcSurgicalSaveAs.Generate(Source.Replace("(0.01,0)", "(0.5,0)"), copy));
        Assert.Throws<InvalidDataException>(() =>
            DbcSurgicalSaveAs.Generate(Source.Replace("SG_ Speed", "SG_ Changed"), copy));
    }

    [Fact]
    public void RejectsOverwritingSourceAndExistingDestination()
    {
        var (copy, db) = CreateCopy();
        var signal = db.MessageIdentities![new(291, false)].Signals[0];
        copy.StageSignal(new(291, false), "Speed", signal with { Unit = "mph" });
        var directory = Path.Combine(Path.GetTempPath(), "rbg-safe-dbc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var originalPath = Path.Combine(directory, "original.dbc");
        var newPath = Path.Combine(directory, "new.dbc");
        try
        {
            File.WriteAllText(originalPath, Source);
            File.WriteAllText(newPath, "do not overwrite");
            Assert.Throws<IOException>(() => DbcSurgicalSaveAs.WriteNewFile(originalPath, originalPath, copy));
            Assert.Throws<IOException>(() => DbcSurgicalSaveAs.WriteNewFile(originalPath, newPath, copy));
            Assert.Equal(Source, File.ReadAllText(originalPath));
            Assert.Equal("do not overwrite", File.ReadAllText(newPath));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SaveAsWritesReparseableNewFileAndKeepsSourceUnchanged()
    {
        var (copy, db) = CreateCopy();
        var signal = db.MessageIdentities![new(291, false)].Signals[0];
        copy.StageSignal(new(291, false), "Speed", signal with { Offset = 2 });
        var directory = Path.Combine(Path.GetTempPath(), "rbg-safe-dbc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.dbc");
            var target = Path.Combine(directory, "edited.dbc");
            File.WriteAllText(source, Source);
            DbcSurgicalSaveAs.WriteNewFile(source, target, copy);
            Assert.Equal(Source, File.ReadAllText(source));
            var edited = new DbcParser().ParseFile(target);
            Assert.Equal(2, edited.MessageIdentities![new(291, false)].Signals[0].Offset);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ValueMeaningsCanBeEditedAndQuotedLabelsSurviveFullReparse()
    {
        var (copy, source) = CreateCopy();
        copy.StageNamedValues(new(291, false), "Speed", new Dictionary<long, string>
        {
            [0] = "Ignition off",
            [1] = "Run \"diagnostics\"",
            [2] = "Reserved \\ test"
        });
        var output = DbcSurgicalSaveAs.Generate(Source, copy);
        Assert.Contains("CUSTOM_VENDOR_ \"OEM metadata must not move\";", output);
        Assert.Contains("VAL_ 291 Speed 0 \"Ignition off\"", output);
        Assert.Contains("1 \"Run \\\"diagnostics\\\"\"", output);
        var parsed = new DbcParser().Parse("updated", new StringReader(output));
        Assert.True(parsed.TryGetMessage(291, false, out var frame));
        var meanings = frame.Signals.Single(signal => signal.Name == "Speed").ValueDescriptions!;
        Assert.Equal("Run \"diagnostics\"", meanings[1]);
        Assert.Equal("Reserved \\ test", meanings[2]);
        Assert.Equal("Stopped", source.MessageIdentities![new(291, false)]
            .Signals[0].ValueDescriptions![0]);
    }

    [Fact]
    public void AppendingExtendedFrameValueTableCannotAlterStandardFrameTable()
    {
        var (copy, _) = CreateCopy();
        copy.StageNamedValues(new(291, true), "State",
            new Dictionary<long, string> { [0] = "Inactive", [2] = "Active" });
        var output = DbcSurgicalSaveAs.Generate(Source, copy);
        Assert.Contains("VAL_ 291 Speed 0 \"Stopped\" 1 \"Running\" ;", output);
        Assert.Contains("VAL_ 2147483939 State 0 \"Inactive\" 2 \"Active\" ;", output);
        var parsed = new DbcParser().Parse("saved", new StringReader(output));
        Assert.True(parsed.TryGetMessage(291, false, out var std));
        Assert.True(parsed.TryGetMessage(291, true, out var ext));
        Assert.Equal("Stopped", std.Signals[0].ValueDescriptions![0]);
        Assert.Equal("Active", ext.Signals[0].ValueDescriptions![2]);
    }

    [Fact]
    public void RemovingAnExistingValueTableDoesNotDeleteAnyOtherLines()
    {
        var (copy, _) = CreateCopy();
        copy.StageNamedValues(new(291, false), "Speed", new Dictionary<long, string>());
        var output = DbcSurgicalSaveAs.Generate(Source, copy);
        Assert.DoesNotContain("VAL_ 291 Speed", output);
        Assert.Contains("CUSTOM_VENDOR_ \"OEM metadata must not move\";", output);
        Assert.Contains("CM_ SG_ 291 Speed \"Sensor & <CAN>\";", output);
        var parsed = new DbcParser().Parse("without values", new StringReader(output));
        Assert.True(parsed.TryGetMessage(291, false, out var message));
        Assert.True(message.Signals[0].ValueDescriptions is not { Count: > 0 });
    }

    [Fact]
    public void MultilineValueTableIsReplacedWithoutSwallowingAdjacentSections()
    {
        var input = Source.Replace(
            "VAL_ 291 Speed 0 \"Stopped\" 1 \"Running\" ;",
            "VAL_ 291 Speed 0 \"Stopped\"\r\n 1 \"Running\" ;");
        var db = new DbcParser().Parse("multi", new StringReader(input));
        var copy = new DbcWorkingCopy(db);
        copy.StageNamedValues(new(291, false), "Speed",
            new Dictionary<long, string> { [0] = "Inactive", [1] = "Active" });
        var output = DbcSurgicalSaveAs.Generate(input, copy);
        Assert.Contains("VAL_ 291 Speed 0 \"Inactive\" 1 \"Active\" ;", output);
        Assert.Contains("CUSTOM_VENDOR_ \"OEM metadata must not move\";", output);
        var roundTrip = new DbcParser().Parse("round", new StringReader(output));
        Assert.Equal("Active",
            roundTrip.MessageIdentities![new(291, false)].Signals[0].ValueDescriptions![1]);
    }

    [Fact]
    public void StageNamedValuesRejectsOutOfRangeRawCodesAndDoesNotChangeDraft()
    {
        var (copy, _) = CreateCopy();
        Assert.Throws<ArgumentException>(() =>
            copy.StageNamedValues(new(291, true), "State",
                new Dictionary<long, string> { [256] = "Too large for 8 bits" }));
        Assert.Throws<ArgumentException>(() =>
            copy.StageNamedValues(new(291, false), "Speed",
                new Dictionary<long, string> { [-1] = "Negative for unsigned" }));
        Assert.False(copy.IsModified);
    }


    [Fact]
    public void RejectsChangesToUnrelatedSignalsAfterDatabaseWasLoaded()
    {
        var (copy, _) = CreateCopy();
        copy.StageNamedValues(new(291, false), "Speed",
            new Dictionary<long, string> { [0] = "Off", [1] = "On" });
        var concurrentlyChanged = Source.Replace(
            "SG_ State : 0|8@1+ (1,0)",
            "SG_ State : 1|8@1+ (1,0)");
        Assert.Throws<InvalidDataException>(() =>
            DbcSurgicalSaveAs.Generate(concurrentlyChanged, copy));
        Assert.Contains("SG_ State : 0|8@1+ (1,0)", Source);
    }

}
