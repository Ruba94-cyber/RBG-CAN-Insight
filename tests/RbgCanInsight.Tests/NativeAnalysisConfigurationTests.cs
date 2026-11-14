using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class NativeAnalysisConfigurationTests
{
    private static readonly NativePlotSettings PlotSettings = new(30, 0, true, false);
    private static SignalTimeline Timeline(uint id = 0x123, int channel = 0, int logger = 0,
        string database = "Powertrain", string signalName = "Speed",
        uint? definitionId = null, string unit = "km/h")
    {
        var signal = new DbcSignal(signalName, 0, 16, DbcByteOrder.Intel, false,
            0.5, 0, 0, 100, unit, "ECU");
        var message = new DbcMessage(definitionId ?? id, "VehicleStatus", 8, "ECU", false, [signal]);
        return new SignalTimeline(message, signal, database, channel, id, false,
            logger, $"recording-{logger}.asc");
    }

    [Fact]
    public void PortableConfigurationRoundTripsExactIdentityAndResolvesIt()
    {
        using var sandbox = new Sandbox();
        var timeline = Timeline();
        var configuration = new NativeAnalysisConfiguration(1, "Fleet profile", PlotSettings,
            [new NativeSignalBinding("Vehicle speed", NativeSignalReference.FromTimeline(timeline),
                IsPlotted: true, IsWatched: false, LineStyle: PlotLineStyle.Dots)]);
        var path = Path.Combine(sandbox.Root, "fleet.rbgconfig");
        var store = new NativeAnalysisConfigurationStore(path);

        store.Write(configuration);

        var loaded = new NativeAnalysisConfigurationStore(path).Read();
        Assert.Equal(configuration.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(configuration.Name, loaded.Name);
        Assert.Equal(configuration.PlotSettings, loaded.PlotSettings);
        Assert.Equal(configuration.Bindings, loaded.Bindings);
        var resolved = Assert.Single(NativeAnalysisConfigurationResolver.Resolve(loaded, [timeline]));
        Assert.Equal(NativeBindingStatus.Matched, resolved.Status);
        Assert.Same(timeline, resolved.Timeline);
    }

    [Fact]
    public void NameOnlyNearMatchesAreMissingAndDuplicateExactIdentitiesAreAmbiguous()
    {
        var original = Timeline();
        var binding = new NativeSignalBinding("Speed", NativeSignalReference.FromTimeline(original),
            IsPlotted: true, IsWatched: true);
        var configuration = new NativeAnalysisConfiguration(1, "Strict", PlotSettings, [binding]);

        var nameOnly = Assert.Single(NativeAnalysisConfigurationResolver.Resolve(configuration,
            [Timeline(id: 0x124)]));
        Assert.Equal(NativeBindingStatus.Missing, nameOnly.Status);
        Assert.Null(nameOnly.Timeline);

        var duplicate = Assert.Single(NativeAnalysisConfigurationResolver.Resolve(configuration,
            [original, Timeline()]));
        Assert.Equal(NativeBindingStatus.Ambiguous, duplicate.Status);
        Assert.Equal(2, duplicate.CandidateCount);
        Assert.Null(duplicate.Timeline);
    }

    [Fact]
    public void ChannelAndLoggerArePartOfIdentity()
    {
        var original = Timeline(channel: 0, logger: 0);
        var configuration = new NativeAnalysisConfiguration(1, "Strict", PlotSettings,
            [new NativeSignalBinding("Speed", NativeSignalReference.FromTimeline(original), true, false)]);

        Assert.Equal(NativeBindingStatus.Missing,
            Assert.Single(NativeAnalysisConfigurationResolver.Resolve(configuration,
                [Timeline(channel: 1, logger: 0)])).Status);
        Assert.Equal(NativeBindingStatus.Missing,
            Assert.Single(NativeAnalysisConfigurationResolver.Resolve(configuration,
                [Timeline(channel: 0, logger: 1)])).Status);
    }

    [Fact]
    public void DbcDefinitionShapeAndConversionMetadataArePartOfIdentity()
    {
        var original = Timeline();
        var configuration = new NativeAnalysisConfiguration(1, "Strict", PlotSettings,
            [new NativeSignalBinding("Speed", NativeSignalReference.FromTimeline(original), true, false)]);

        Assert.Equal(NativeBindingStatus.Missing,
            Assert.Single(NativeAnalysisConfigurationResolver.Resolve(configuration,
                [Timeline(id: 0x123, definitionId: 0x124)])).Status);

        var changedConversion = NativeSignalReference.FromTimeline(original) with
        {
            ValueType = DbcSignalValueType.Float32
        };
        var changedConfig = configuration with
        {
            Bindings = [new NativeSignalBinding("Speed", changedConversion, true, false)]
        };
        Assert.Equal(NativeBindingStatus.Missing,
            Assert.Single(NativeAnalysisConfigurationResolver.Resolve(changedConfig, [original])).Status);
    }

    [Fact]
    public void UnsupportedSchemaAndInvalidIdentityAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => NativeAnalysisConfigurationResolver.Resolve(
            new NativeAnalysisConfiguration(3, "Future", PlotSettings, []), []));
        var invalid = NativeSignalReference.FromTimeline(Timeline()) with { Channel = -1 };
        Assert.Throws<InvalidDataException>(() => NativeAnalysisConfigurationResolver.Resolve(
            new NativeAnalysisConfiguration(1, "Invalid", PlotSettings,
                [new NativeSignalBinding("Speed", invalid, true, false)]), []));
        Assert.Throws<InvalidDataException>(() => NativeAnalysisConfigurationResolver.Resolve(
            new NativeAnalysisConfiguration(1, "Invalid plot", PlotSettings with
            {
                TimeWindowSeconds = double.NaN
            }, []), []));
    }

    [Fact]
    public void UnsupportedPlotLineStyleIsRejected()
    {
        var timeline = Timeline();
        var binding = new NativeSignalBinding("Speed", NativeSignalReference.FromTimeline(timeline),
            IsPlotted: true, IsWatched: false, LineStyle: (PlotLineStyle)99);
        var configuration = new NativeAnalysisConfiguration(1, "Invalid style", PlotSettings, [binding]);

        Assert.Throws<InvalidDataException>(() =>
            NativeAnalysisConfigurationResolver.Resolve(configuration, [timeline]));
    }

    [Fact]
    public void ReadRejectsOmittedValueTypeIdentityAndDuplicateJsonProperties()
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(sandbox.Root);
        var path = Path.Combine(sandbox.Root, "bad.rbgconfig");
        var store = new NativeAnalysisConfigurationStore(path);
        File.WriteAllText(path, """
            {"SchemaVersion":1,"Name":"Incomplete","PlotSettings":{"TimeWindowSeconds":30,"YAxisModeIndex":0,"ShowInlineLegend":true,"LinearInterpolation":false},"Bindings":[{"Name":"Speed","IsPlotted":true,"IsWatched":false,"Signal":{
              "DatabaseName":"Powertrain","Channel":0,"OriginalSourceIndex":0,
              "SourceIdentifier":291,"MessageName":"VehicleStatus","MessageIdentifier":291,
              "MessageIsExtended":false,"MessageLength":8,"SignalName":"Speed",
              "StartBit":0,"BitLength":16,"ByteOrder":1,"IsSigned":false,
              "Factor":0.5,"Offset":0,"Unit":"km/h","MultiplexValue":null,
              "MultiplexConditionsIdentity":"[]","ValueType":0,"ScaleSegmentsIdentity":"[]",
              "PhysicalConversionUnsupported":false,"CompleteSignalDefinitionIdentity":"{}"}}]}
            """);
        Assert.Throws<System.Text.Json.JsonException>(() => store.Read());

        File.WriteAllText(path, """
            {"SchemaVersion":1,"SchemaVersion":1,"Name":"Duplicate","Bindings":[]}
            """);
        Assert.Throws<InvalidDataException>(() => store.Read());
    }

    [Fact]
    public void WritesLeaveNoSharedTemporaryFileBehind()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Root, "portable.rbgconfig");
        var store = new NativeAnalysisConfigurationStore(path);
        store.Write(new NativeAnalysisConfiguration(1, "Atomic", PlotSettings,
            [new NativeSignalBinding("Speed", NativeSignalReference.FromTimeline(Timeline()), false, true)]));

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(sandbox.Root, "*.tmp"));
    }

    [Fact]
    public void DimensionlessSignalWithEmptyUnitIsValid()
    {
        var timeline = Timeline(unit: "");
        var configuration = new NativeAnalysisConfiguration(1, "Dimensionless", PlotSettings,
            [new NativeSignalBinding("Counter", NativeSignalReference.FromTimeline(timeline), false, true)]);
        var result = Assert.Single(NativeAnalysisConfigurationResolver.Resolve(configuration, [timeline]));
        Assert.Equal(NativeBindingStatus.Matched, result.Status);
    }

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rbgconfig-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
