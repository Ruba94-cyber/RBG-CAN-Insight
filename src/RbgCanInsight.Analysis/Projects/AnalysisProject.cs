using System.Text.Json;
using System.Text.Json.Serialization;

namespace RbgCanInsight.Analysis.Projects;

/// <summary>
/// Portable analysis project (JSON). Unknown properties at any level that carries
/// <c>ExtensionData</c> are preserved on load and written back on save.
/// </summary>
public sealed class AnalysisProject
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyOrder(0)] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [JsonPropertyOrder(1)] public string Name { get; set; } = "";
    [JsonPropertyOrder(2)] public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyOrder(3)] public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyOrder(4)] public List<TraceFileRef> TraceFiles { get; set; } = new();
    [JsonPropertyOrder(5)] public List<DbcFileRef> DbcFiles { get; set; } = new();
    [JsonPropertyOrder(6)] public List<CatalogFileRef> CatalogFiles { get; set; } = new();
    [JsonPropertyOrder(7)] public List<SavedFilter> Filters { get; set; } = new();
    [JsonPropertyOrder(8)] public List<ProjectMarker> Markers { get; set; } = new();
    [JsonPropertyOrder(9)] public List<ProjectAnnotation> Annotations { get; set; } = new();
    [JsonPropertyOrder(10)] public List<PlottedSignal> PlottedSignals { get; set; } = new();
    [JsonPropertyOrder(11)] public string? TraceColumnLayout { get; set; }
    // Explicitly persist how multiple traces were aligned. Old projects default to ByTime.
    [JsonPropertyOrder(12)] public string TraceMergeMode { get; set; } = "ByTime";

    [JsonExtensionData] public SortedDictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Updates <see cref="ModifiedUtc"/>; Save never does this implicitly (deterministic output).</summary>
    public void Touch() => ModifiedUtc = DateTimeOffset.UtcNow;

    internal void Normalize()
    {
        Name ??= "";
        TraceMergeMode = string.IsNullOrWhiteSpace(TraceMergeMode) ? "ByTime" : TraceMergeMode;
        TraceFiles ??= new(); DbcFiles ??= new(); CatalogFiles ??= new(); Filters ??= new();
        Markers ??= new(); Annotations ??= new(); PlottedSignals ??= new();
        foreach (var a in Annotations) a.Tags ??= new();
    }
}

/// <summary>Reference to an external file with integrity data.</summary>
public class ProjectFileRef
{
    /// <summary>Relative (forward slashes, inside the project directory) or absolute when <see cref="External"/>.</summary>
    [JsonPropertyOrder(0)] public string Path { get; set; } = "";
    /// <summary>True when the file lives outside the project directory (absolute path; never read on load).</summary>
    [JsonPropertyOrder(1)] public bool External { get; set; }
    [JsonPropertyOrder(2)] public string? Sha256 { get; set; }
    [JsonPropertyOrder(3)] public long? Length { get; set; }

    [JsonExtensionData] public SortedDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class TraceFileRef : ProjectFileRef { }

public sealed class DbcFileRef : ProjectFileRef
{
    [JsonPropertyOrder(10)] public int? BusChannel { get; set; }
    [JsonPropertyOrder(11)] public bool J1939Pgn { get; set; }
}

public sealed class CatalogFileRef : ProjectFileRef
{
    /// <summary>"CDD" or "ODX".</summary>
    [JsonPropertyOrder(10)] public string Kind { get; set; } = "";
}

public sealed class SavedFilter
{
    public string Name { get; set; } = "";
    public string? SearchText { get; set; }
    public string? FilterExpression { get; set; }
    [JsonExtensionData] public SortedDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class ProjectMarker
{
    public long FrameIndex { get; set; }
    public double TimestampSeconds { get; set; }
    public string Text { get; set; } = "";
    public string? Category { get; set; }
    [JsonExtensionData] public SortedDictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Analyst note over a time range or a frame range. All text is author-supplied; nothing is generated.</summary>
public sealed class ProjectAnnotation
{
    public string Id { get; set; } = "";
    public double? StartSeconds { get; set; }
    public double? EndSeconds { get; set; }
    public long? StartFrame { get; set; }
    public long? EndFrame { get; set; }
    public string Title { get; set; } = "";
    public string? Note { get; set; }
    public List<string> Tags { get; set; } = new();
    public string? Author { get; set; }
    [JsonExtensionData] public SortedDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class PlottedSignal
{
    public string Message { get; set; } = "";
    public string Signal { get; set; } = "";
    public int? Channel { get; set; }
    public string? DbcName { get; set; }
    public uint Identifier { get; set; }
    public bool Extended { get; set; }
    [JsonExtensionData] public SortedDictionary<string, JsonElement>? ExtensionData { get; set; }
}
