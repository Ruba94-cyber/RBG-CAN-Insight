using System.Text.Json;
using System.Text.Json.Serialization;
using RbgCanInsight.Dbc;

namespace RbgCanInsight.Analysis;

/// <summary>Portable, exact reference to a decoded signal. CAN identity is the observed wire
/// identifier; DBC names alone are never sufficient to bind a saved configuration.</summary>
public sealed record NativeSignalReference(
    [property: JsonRequired] string DatabaseName,
    [property: JsonRequired] int Channel,
    [property: JsonRequired] int OriginalSourceIndex,
    [property: JsonRequired] uint SourceIdentifier,
    [property: JsonRequired] bool SourceIsExtended,
    [property: JsonRequired] string MessageName,
    [property: JsonRequired] uint MessageIdentifier,
    [property: JsonRequired] bool MessageIsExtended,
    [property: JsonRequired] int MessageLength,
    [property: JsonRequired] string SignalName,
    [property: JsonRequired] int StartBit,
    [property: JsonRequired] int BitLength,
    [property: JsonRequired] DbcByteOrder ByteOrder,
    [property: JsonRequired] bool IsSigned,
    [property: JsonRequired] double Factor,
    [property: JsonRequired] double Offset,
    [property: JsonRequired] string Unit,
    [property: JsonRequired] bool IsMultiplexor,
    [property: JsonRequired] int? MultiplexValue,
    [property: JsonRequired] string MultiplexConditionsIdentity,
    [property: JsonRequired] DbcSignalValueType ValueType,
    [property: JsonRequired] string ScaleSegmentsIdentity,
    [property: JsonRequired] bool PhysicalConversionUnsupported,
    [property: JsonRequired] string CompleteSignalDefinitionIdentity)
{
    public static NativeSignalReference FromTimeline(SignalTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var signal = timeline.Signal;
        return new NativeSignalReference(timeline.DatabaseName, timeline.Channel,
            timeline.OriginalSourceIndex, timeline.SourceIdentifier, timeline.SourceIsExtended,
            timeline.Message.Name, timeline.Message.Identifier, timeline.Message.IsExtended,
            timeline.Message.Length, signal.Name, signal.StartBit, signal.BitLength, signal.ByteOrder,
            signal.IsSigned, signal.Factor, signal.Offset, signal.Unit,
            signal.IsMultiplexor, signal.MultiplexValue,
            JsonSerializer.Serialize(signal.MultiplexConditions ?? []), signal.ValueType,
            JsonSerializer.Serialize(signal.ScaleSegments ?? []), signal.PhysicalConversionUnsupported,
            JsonSerializer.Serialize(signal));
    }
}

/// <summary>A named, explicit mapping in a portable analysis configuration.</summary>
public sealed record NativeSignalBinding(
    [property: JsonRequired] string Name,
    [property: JsonRequired] NativeSignalReference Signal,
    [property: JsonRequired] bool IsPlotted,
    [property: JsonRequired] bool IsWatched,
    [property: JsonRequired] PlotLineStyle LineStyle = PlotLineStyle.Auto);

/// <summary>Versioned native analysis settings. It contains signal identities and explicit
/// user mappings only; it does not embed traces, DBC files, or OEM source data.</summary>
public sealed record NativeAnalysisConfiguration(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string Name,
    [property: JsonRequired] NativePlotSettings PlotSettings,
    [property: JsonRequired] IReadOnlyList<NativeSignalBinding> Bindings,
    // Schema 2 sections (sources, curve colours, axes, panels, filters, markers, cursors, view, comparisons).
    // Absent in schema 1 files; a schema 1 file cannot carry them.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NativeAnalysisExtras? Extras = null)
{
    /// <summary>The same configuration in the current schema, in memory. A schema 1 file loses nothing: its bindings and
    /// plot settings are kept and the new sections are empty. Writing the result produces a schema 2 file.</summary>
    public NativeAnalysisConfiguration UpgradedToCurrent() =>
        SchemaVersion >= NativeAnalysisConfigurationStore.CurrentSchemaVersion
            ? this
            : this with { SchemaVersion = NativeAnalysisConfigurationStore.CurrentSchemaVersion, Extras = Extras ?? NativeAnalysisExtras.Empty };

    [JsonIgnore]
    public NativeAnalysisExtras ExtrasOrEmpty => Extras ?? NativeAnalysisExtras.Empty;
}

/// <summary>Portable plot preferences that apply to any loaded recording.</summary>
public sealed record NativePlotSettings(
    [property: JsonRequired] double TimeWindowSeconds,
    [property: JsonRequired] int YAxisModeIndex,
    [property: JsonRequired] bool ShowInlineLegend,
    [property: JsonRequired] bool LinearInterpolation);

public enum NativeBindingStatus { Matched, Missing, Ambiguous }

/// <summary>Why a binding is <see cref="NativeBindingStatus.Missing"/>. Diagnostic only: it never changes the status
/// and never selects a signal.</summary>
public enum NativeMissingReason
{
    None,
    /// <summary>Nothing in the loaded recordings/DBCs resembles the saved signal.</summary>
    NotInTrace,
    /// <summary>A signal with the same message and signal name (and channel) exists but its definition differs — the DBC was
    /// modified (start bit, scaling, byte order, multiplexing, ID…). It is offered as a candidate, never applied automatically.</summary>
    DefinitionChanged
}

public sealed record NativeBindingResolution(
    NativeSignalBinding Binding, NativeBindingStatus Status, SignalTimeline? Timeline,
    int CandidateCount, NativeMissingReason Reason = NativeMissingReason.None,
    IReadOnlyList<SignalTimeline>? SameNameCandidates = null);

/// <summary>Resolves each binding by exact declared identity. A signal name match is never used
/// as a fallback, and duplicate exact candidates are surfaced as ambiguous.</summary>
public static class NativeAnalysisConfigurationResolver
{
    public static IReadOnlyList<NativeBindingResolution> Resolve(
        NativeAnalysisConfiguration configuration, IReadOnlyList<SignalTimeline> timelines)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(timelines);
        NativeAnalysisConfigurationStore.Validate(configuration);
        var index = new Dictionary<NativeSignalReference, (SignalTimeline? Timeline, int Count)>();
        foreach (var timeline in timelines)
        {
            if (timeline is null)
                throw new ArgumentException("Timeline collection cannot contain null entries.", nameof(timelines));
            var identity = NativeSignalReference.FromTimeline(timeline);
            if (index.TryGetValue(identity, out var existing))
                index[identity] = (existing.Timeline, existing.Count + 1);
            else
                index.Add(identity, (timeline, 1));
        }

        return configuration.Bindings.Select(binding =>
        {
            if (!index.TryGetValue(binding.Signal, out var match))
            {
                // Diagnostic only: same message + signal name + channel, different definition = "DBC modified".
                var similar = timelines.Where(t => t.Channel == binding.Signal.Channel &&
                        string.Equals(t.Message.Name, binding.Signal.MessageName, StringComparison.Ordinal) &&
                        string.Equals(t.Signal.Name, binding.Signal.SignalName, StringComparison.Ordinal)).ToArray();
                return similar.Length == 0
                    ? new NativeBindingResolution(binding, NativeBindingStatus.Missing, null, 0, NativeMissingReason.NotInTrace)
                    : new NativeBindingResolution(binding, NativeBindingStatus.Missing, null, 0,
                        NativeMissingReason.DefinitionChanged, similar);
            }
            return match.Count switch
            {
                1 => new NativeBindingResolution(binding, NativeBindingStatus.Matched, match.Timeline, 1),
                _ => new NativeBindingResolution(binding, NativeBindingStatus.Ambiguous, null, match.Count)
            };
        }).ToArray();
    }
}

/// <summary>Read/write support for a single portable `.rbgconfig` file.</summary>
public sealed class NativeAnalysisConfigurationStore
{
    /// <summary>Schema 2 adds optional sections (<see cref="NativeAnalysisExtras"/>); schema 1 files are still read.</summary>
    public const int CurrentSchemaVersion = 2;
    public const int MinimumSupportedSchemaVersion = 1;
    public const int MaxFileBytes = 1_048_576;
    public const int MaxBindings = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _path;

    public NativeAnalysisConfigurationStore(string path) =>
        _path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("A configuration path is required.", nameof(path))
            : Path.GetFullPath(path);

    public NativeAnalysisConfiguration Read()
    {
        var info = new FileInfo(_path);
        if (!info.Exists) throw new FileNotFoundException("Analysis configuration was not found.", _path);
        if (info.Length > MaxFileBytes)
            throw new InvalidDataException("Analysis configuration exceeds the 1 MiB safety limit.");
        var json = File.ReadAllText(_path);
        RejectDuplicateProperties(json);
        RejectNewerSchema(json);
        var configuration = JsonSerializer.Deserialize<NativeAnalysisConfiguration>(json, JsonOptions)
            ?? throw new InvalidDataException("Analysis configuration is empty.");
        Validate(configuration);
        return configuration;
    }

    public void Write(NativeAnalysisConfiguration configuration)
    {
        Validate(configuration);
        var json = JsonSerializer.Serialize(configuration, JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
            throw new InvalidDataException("Analysis configuration exceeds the 1 MiB safety limit.");
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                writer.Write(json);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static void Validate(NativeAnalysisConfiguration configuration)
    {
        if (configuration.SchemaVersion > CurrentSchemaVersion)
            throw new InvalidDataException($"Analysis configuration version {configuration.SchemaVersion} was created by a newer version of RBG CAN Insight " +
                $"(this one reads versions {MinimumSupportedSchemaVersion}–{CurrentSchemaVersion}). Update the application to open it; nothing was changed.");
        if (configuration.SchemaVersion < MinimumSupportedSchemaVersion)
            throw new InvalidDataException($"Analysis configuration version {configuration.SchemaVersion} is not supported.");
        if (configuration.SchemaVersion < 2 && configuration.Extras is not null)
            throw new InvalidDataException("A version 1 analysis configuration cannot contain version 2 sections.");
        if (string.IsNullOrWhiteSpace(configuration.Name) || configuration.Name.Length > 120 ||
            configuration.Name.Any(char.IsControl))
            throw new InvalidDataException("Configuration name must contain 1–120 printable characters.");
        if (configuration.PlotSettings is null ||
            !double.IsFinite(configuration.PlotSettings.TimeWindowSeconds) ||
            configuration.PlotSettings.TimeWindowSeconds is < 0.05 or > 86_400 ||
            configuration.PlotSettings.YAxisModeIndex is < 0 or > 2)
            throw new InvalidDataException("Plot settings contain an invalid time window or Y axis mode.");
        if (configuration.Bindings is null || configuration.Bindings.Count > MaxBindings)
            throw new InvalidDataException("Configuration has an invalid number of signal bindings.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in configuration.Bindings)
        {
            if (binding is null || string.IsNullOrWhiteSpace(binding.Name) || binding.Name.Length > 120 ||
                binding.Name.Any(char.IsControl) || !names.Add(binding.Name))
                throw new InvalidDataException("Signal binding names must be unique, printable, and 1–120 characters.");
            if (!Enum.IsDefined(binding.LineStyle))
                throw new InvalidDataException("Signal binding contains an unsupported plot line style.");
            var signal = binding.Signal;
            if (signal is null || string.IsNullOrWhiteSpace(signal.DatabaseName) ||
                string.IsNullOrWhiteSpace(signal.MessageName) || string.IsNullOrWhiteSpace(signal.SignalName) ||
                signal.Unit is null || signal.DatabaseName.Length > 512 ||
                signal.MessageName.Length > 512 || signal.SignalName.Length > 512 || signal.Unit.Length > 128 ||
                string.IsNullOrWhiteSpace(signal.MultiplexConditionsIdentity) ||
                string.IsNullOrWhiteSpace(signal.ScaleSegmentsIdentity) ||
                string.IsNullOrWhiteSpace(signal.CompleteSignalDefinitionIdentity) ||
                signal.Channel < 0 || signal.OriginalSourceIndex < -1 ||
                signal.SourceIdentifier > (signal.SourceIsExtended ? 0x1FFF_FFFFu : 0x7FFu) ||
                signal.MessageIdentifier > (signal.MessageIsExtended ? 0x1FFF_FFFFu : 0x7FFu) ||
                signal.MessageLength is < 1 or > 64 ||
                signal.StartBit < 0 || signal.BitLength is < 1 or > 64 ||
                !Enum.IsDefined(signal.ByteOrder) || !double.IsFinite(signal.Factor) ||
                !double.IsFinite(signal.Offset) ||
                signal.MultiplexConditionsIdentity.Length > 32_768 ||
                signal.ScaleSegmentsIdentity.Length > 32_768 ||
                signal.CompleteSignalDefinitionIdentity.Length > 65_536 ||
                !Enum.IsDefined(signal.ValueType))
                throw new InvalidDataException("Signal binding contains an invalid or incomplete physical identity.");
        }
        if (configuration.Extras is { } extras) NativeAnalysisExtrasValidator.Validate(extras, names);
    }

    /// <summary>A file from a newer release may contain properties this release does not know: say so clearly instead of
    /// failing on the first unknown member.</summary>
    private static void RejectNewerSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("SchemaVersion", out var version) &&
            version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number > CurrentSchemaVersion)
            throw new InvalidDataException($"Analysis configuration version {number} was created by a newer version of RBG CAN Insight " +
                $"(this one reads versions {MinimumSupportedSchemaVersion}–{CurrentSchemaVersion}). Update the application to open it; nothing was changed.");
    }

    private static void RejectDuplicateProperties(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        Check(document.RootElement);

        static void Check(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidDataException($"Duplicate JSON property '{property.Name}' is not allowed.");
                    Check(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) Check(item);
            }
        }
    }
}
