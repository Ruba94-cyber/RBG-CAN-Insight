using System.Text.Json;
using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class NativeAnalysisConfigurationV2Tests : IDisposable
{
    private static readonly NativePlotSettings Plot = new(30, 0, true, false);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rbgconfig-v2-" + Guid.NewGuid().ToString("N"));

    public NativeAnalysisConfigurationV2Tests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static SignalTimeline Timeline(uint id = 0x123, int channel = 0, string signalName = "Speed", int startBit = 0, double factor = 0.5)
    {
        var signal = new DbcSignal(signalName, startBit, 16, DbcByteOrder.Intel, false, factor, 0, 0, 100, "km/h", "ECU");
        var message = new DbcMessage(id, "VehicleStatus", 8, "ECU", false, [signal]);
        return new SignalTimeline(message, signal, "Powertrain", channel, id, false, 0, "rec.asc");
    }

    private static NativeSignalBinding Binding(string name, SignalTimeline t) =>
        new(name, NativeSignalReference.FromTimeline(t), IsPlotted: true, IsWatched: false, LineStyle: PlotLineStyle.Dots);

    private static NativeAnalysisExtras FullExtras() => new(
        Sources:
        [
            new NativeFileReference("trace", "drive.asc", "logs\\drive.asc", 1234, new string('a', 64)),
            new NativeFileReference("dbc", "powertrain.dbc", null, 99, new string('b', 64)),
        ],
        Curves: [new NativeCurveStyle("Speed", "#FF8800")],
        Axes: [new NativeAxisSetting("Speed axis", ["Speed"], Min: 0, Max: 250, Scale: "linear", Unit: "km/h")],
        Panels: [new NativePanel("Main", ["Speed"], 2.0)],
        Filters: new NativeTraceFilters("0x123", "id==0x123", "speed", [0, 1], ["123"]),
        Markers: [new NativeMarker(1_500_000_000, "Bookmark", "start of braking")],
        Cursors: new NativeCursors(1_000_000_000, 2_500_000_000),
        View: new NativeViewState("Signals & Plot", 2, 12.5),
        Comparisons: [new NativeComparison("before/after", "drive.asc", "drive2.asc", ["Speed"])]);

    [Fact]
    public void V2WithEverySectionRoundTripsWithoutLoss()
    {
        var t = Timeline();
        var configuration = new NativeAnalysisConfiguration(2, "Full", Plot, [Binding("Speed", t)], FullExtras());
        var path = Path.Combine(_root, "full.rbgconfig");

        new NativeAnalysisConfigurationStore(path).Write(configuration);
        var loaded = new NativeAnalysisConfigurationStore(path).Read();

        Assert.Equal(2, loaded.SchemaVersion);
        Assert.Equal(configuration.Bindings, loaded.Bindings);
        var extras = loaded.ExtrasOrEmpty;
        Assert.Equal(2, extras.SourceList.Count);
        Assert.Equal("logs\\drive.asc", extras.SourceList[0].RelativePath);
        Assert.Equal("#FF8800", Assert.Single(extras.CurveList).Color);
        var axis = Assert.Single(extras.AxisList);
        Assert.Equal((0d, 250d, "linear", "km/h"), (axis.Min, axis.Max, axis.Scale, axis.Unit));
        Assert.Equal(2.0, Assert.Single(extras.PanelList).HeightWeight);
        Assert.Equal("id==0x123", extras.Filters!.TraceRuleText);
        Assert.Equal(new[] { 0, 1 }, extras.Filters.Channels);
        Assert.Equal(1_500_000_000, Assert.Single(extras.MarkerList).TimestampNanoseconds);
        Assert.Equal(2_500_000_000, extras.Cursors!.CursorBNanoseconds);
        Assert.Equal("Signals & Plot", extras.View!.ActiveView);
        Assert.Equal("drive2.asc", Assert.Single(extras.ComparisonList).RightSource);
        Assert.Equal(NativeBindingStatus.Matched, Assert.Single(NativeAnalysisConfigurationResolver.Resolve(loaded, [t])).Status);
    }

    [Fact]
    public void V1FileIsReadAndUpgradedInMemoryWithoutLosingAnything()
    {
        var t = Timeline();
        var v1 = new NativeAnalysisConfiguration(1, "Legacy", Plot, [Binding("Speed", t)]);
        var path = Path.Combine(_root, "legacy.rbgconfig");
        new NativeAnalysisConfigurationStore(path).Write(v1);
        Assert.DoesNotContain("Extras", File.ReadAllText(path));

        var loaded = new NativeAnalysisConfigurationStore(path).Read();
        Assert.Equal(1, loaded.SchemaVersion);
        var upgraded = loaded.UpgradedToCurrent();

        Assert.Equal(2, upgraded.SchemaVersion);
        Assert.Equal(v1.Bindings, upgraded.Bindings);
        Assert.Equal(v1.PlotSettings, upgraded.PlotSettings);
        Assert.Empty(upgraded.ExtrasOrEmpty.SourceList);

        var path2 = Path.Combine(_root, "legacy-v2.rbgconfig");
        new NativeAnalysisConfigurationStore(path2).Write(upgraded);
        var again = new NativeAnalysisConfigurationStore(path2).Read();
        Assert.Equal(2, again.SchemaVersion);
        Assert.Equal(v1.Bindings, again.Bindings);
    }

    [Fact]
    public void V1CannotCarryV2Sections()
    {
        var t = Timeline();
        var bad = new NativeAnalysisConfiguration(1, "Mixed", Plot, [Binding("Speed", t)], FullExtras());
        Assert.Throws<InvalidDataException>(() => new NativeAnalysisConfigurationStore(Path.Combine(_root, "x.rbgconfig")).Write(bad));
    }

    [Fact]
    public void FutureVersionIsRejectedWithAClearMessageAndTheFileIsUntouched()
    {
        var path = Path.Combine(_root, "future.rbgconfig");
        const string json = """{"SchemaVersion":3,"Name":"Future","SomethingNew":{"a":1},"Bindings":[]}""";
        File.WriteAllText(path, json);

        var ex = Assert.Throws<InvalidDataException>(() => new NativeAnalysisConfigurationStore(path).Read());

        Assert.Contains("newer version", ex.Message);
        Assert.Contains("1–2", ex.Message);
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void VersionZeroIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => NativeAnalysisConfigurationResolver.Resolve(
            new NativeAnalysisConfiguration(0, "Zero", Plot, []), []));
    }

    [Theory]
    [InlineData("curveUnknownBinding")]
    [InlineData("badColor")]
    [InlineData("axisMinNotBelowMax")]
    [InlineData("logAxisWithZero")]
    [InlineData("badScale")]
    [InlineData("negativeMarker")]
    [InlineData("badSourceHash")]
    [InlineData("badSourceKind")]
    [InlineData("panelUnknownBinding")]
    [InlineData("zeroHeight")]
    public void InvalidSectionsAreRejected(string which)
    {
        var t = Timeline();
        var extras = which switch
        {
            "curveUnknownBinding" => new NativeAnalysisExtras(Curves: [new NativeCurveStyle("Nope", "#112233")]),
            "badColor" => new NativeAnalysisExtras(Curves: [new NativeCurveStyle("Speed", "red")]),
            "axisMinNotBelowMax" => new NativeAnalysisExtras(Axes: [new NativeAxisSetting("A", ["Speed"], 10, 10)]),
            "logAxisWithZero" => new NativeAnalysisExtras(Axes: [new NativeAxisSetting("A", ["Speed"], 0, 10, "log")]),
            "badScale" => new NativeAnalysisExtras(Axes: [new NativeAxisSetting("A", ["Speed"], 0, 10, "sqrt")]),
            "negativeMarker" => new NativeAnalysisExtras(Markers: [new NativeMarker(-1, "Bookmark")]),
            "badSourceHash" => new NativeAnalysisExtras(Sources: [new NativeFileReference("trace", "a.asc", null, 1, "xyz")]),
            "badSourceKind" => new NativeAnalysisExtras(Sources: [new NativeFileReference("exe", "a.asc", null, 1, null)]),
            "panelUnknownBinding" => new NativeAnalysisExtras(Panels: [new NativePanel("P", ["Nope"])]),
            _ => new NativeAnalysisExtras(Panels: [new NativePanel("P", ["Speed"], 0)]),
        };
        var configuration = new NativeAnalysisConfiguration(2, "Bad", Plot, [Binding("Speed", t)], extras);

        Assert.Throws<InvalidDataException>(() => NativeAnalysisConfigurationResolver.Resolve(configuration, [t]));
    }

    [Fact]
    public void ModifiedDbcIsReportedAsDefinitionChangedWithCandidatesAndNeverApplied()
    {
        var saved = Timeline(startBit: 0, factor: 0.5);
        var changed = Timeline(startBit: 8, factor: 0.25);    // same message/signal name, different definition
        var configuration = new NativeAnalysisConfiguration(2, "Cfg", Plot, [Binding("Speed", saved)]);

        var result = Assert.Single(NativeAnalysisConfigurationResolver.Resolve(configuration, [changed]));

        Assert.Equal(NativeBindingStatus.Missing, result.Status);
        Assert.Null(result.Timeline);
        Assert.Equal(NativeMissingReason.DefinitionChanged, result.Reason);
        Assert.Same(changed, Assert.Single(result.SameNameCandidates!));
    }

    [Fact]
    public void SignalAbsentFromTheTraceIsNotInTraceAndDifferentChannelIsNotACandidate()
    {
        var saved = Timeline(channel: 0);
        var otherChannel = Timeline(channel: 1);
        var configuration = new NativeAnalysisConfiguration(2, "Cfg", Plot, [Binding("Speed", saved)]);

        var absent = Assert.Single(NativeAnalysisConfigurationResolver.Resolve(configuration, []));
        var elsewhere = Assert.Single(NativeAnalysisConfigurationResolver.Resolve(configuration, [otherChannel]));

        Assert.Equal(NativeMissingReason.NotInTrace, absent.Reason);
        Assert.Equal(NativeMissingReason.NotInTrace, elsewhere.Reason);
        Assert.Null(elsewhere.SameNameCandidates);
    }

    [Fact]
    public void AmbiguousSignalStaysAmbiguousInV2()
    {
        var t1 = Timeline();
        var t2 = Timeline();
        var configuration = new NativeAnalysisConfiguration(2, "Cfg", Plot, [Binding("Speed", t1)]);

        var result = Assert.Single(NativeAnalysisConfigurationResolver.Resolve(configuration, [t1, t2]));

        Assert.Equal(NativeBindingStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.CandidateCount);
        Assert.Null(result.Timeline);
    }

    [Fact]
    public void SourceFileCheckDistinguishesSameMissingResizedAndModified()
    {
        var trace = Path.Combine(_root, "drive.asc");
        var dbc = Path.Combine(_root, "powertrain.dbc");
        File.WriteAllText(trace, "frames-v1");
        File.WriteAllText(dbc, "VERSION \"1\"");
        var refTrace = NativeSourceReferenceChecker.Describe("trace", trace, _root);
        var refDbc = NativeSourceReferenceChecker.Describe("dbc", dbc, _root);
        var refGone = new NativeFileReference("catalog", "gone.cdd", null, 5, new string('0', 64));
        Assert.Equal("drive.asc", refTrace.RelativePath);
        Assert.Equal(64, refTrace.Sha256!.Length);

        // same length, different content -> hash mismatch; different length -> length mismatch
        File.WriteAllText(dbc, "VERSION \"2\"");
        File.WriteAllText(trace, "frames-v1-extended");

        var checks = NativeSourceReferenceChecker.Check([refTrace, refDbc, refGone], [_root]);

        Assert.Equal(NativeFileState.LengthMismatch, checks[0].State);
        Assert.Equal(NativeFileState.HashMismatch, checks[1].State);
        Assert.Equal(NativeFileState.Missing, checks[2].State);
        Assert.Null(checks[2].ResolvedPath);
        Assert.Contains("modified", checks[1].Describe());
    }

    [Fact]
    public void SourceFileCheckFindsTheSameFileInAnotherFolderAndAmongOpenFilesByName()
    {
        var original = Path.Combine(_root, "a", "drive.asc");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.WriteAllText(original, "same-bytes");
        var reference = NativeSourceReferenceChecker.Describe("trace", original, Path.Combine(_root, "a"));
        var moved = Path.Combine(_root, "b", "drive.asc");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(original, moved);

        var viaDirectory = Assert.Single(NativeSourceReferenceChecker.Check([reference], [Path.Combine(_root, "b")]));
        var viaOpenFile = Assert.Single(NativeSourceReferenceChecker.Check([reference], [], [moved]));
        var notFound = Assert.Single(NativeSourceReferenceChecker.Check([reference], [Path.Combine(_root, "a")]));

        Assert.Equal(NativeFileState.Ok, viaDirectory.State);
        Assert.Equal(NativeFileState.Ok, viaOpenFile.State);
        Assert.Equal(NativeFileState.Missing, notFound.State);
    }

    [Fact]
    public void ReadRejectsDuplicatePropertiesAndUnknownMembersInV2()
    {
        var path = Path.Combine(_root, "dup.rbgconfig");
        File.WriteAllText(path, """{"SchemaVersion":2,"Name":"x","Name":"y","PlotSettings":{"TimeWindowSeconds":30,"YAxisModeIndex":0,"ShowInlineLegend":true,"LinearInterpolation":false},"Bindings":[]}""");
        Assert.Throws<InvalidDataException>(() => new NativeAnalysisConfigurationStore(path).Read());

        File.WriteAllText(path, """{"SchemaVersion":2,"Name":"x","PlotSettings":{"TimeWindowSeconds":30,"YAxisModeIndex":0,"ShowInlineLegend":true,"LinearInterpolation":false},"Bindings":[],"Extras":{"Unknown":1}}""");
        Assert.Throws<JsonException>(() => new NativeAnalysisConfigurationStore(path).Read());
    }
}
