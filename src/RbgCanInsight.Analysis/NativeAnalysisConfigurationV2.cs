using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RbgCanInsight.Analysis;

/// <summary>Portable reference to a source file (trace, DBC, catalog, mapping). The file itself is never embedded:
/// only its name, an optional relative path, its length and (when computed) its SHA-256, so a later load can
/// tell "same file", "file changed" and "file missing" apart.</summary>
public sealed record NativeFileReference(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] string FileName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RelativePath = null,
    [property: JsonRequired] long Length = 0,
    // Lowercase hex SHA-256, or null when the file was too large to hash at save time (length-only check).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sha256 = null);

/// <summary>Colour of one curve, addressed by the binding name (never by display order).</summary>
public sealed record NativeCurveStyle([property: JsonRequired] string BindingName, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Color = null);

/// <summary>Limits, scale and unit of an axis shared by the listed bindings. Null limits mean "auto".</summary>
public sealed record NativeAxisSetting(
    [property: JsonRequired] string Name,
    [property: JsonRequired] IReadOnlyList<string> BindingNames,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Min = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Max = null,
    string Scale = "linear",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unit = null);

/// <summary>A plot panel and the bindings drawn in it; <see cref="HeightWeight"/> is its relative height.</summary>
public sealed record NativePanel(
    [property: JsonRequired] string Title,
    [property: JsonRequired] IReadOnlyList<string> BindingNames,
    double HeightWeight = 1.0);

/// <summary>Search/rule text and channel/identifier restrictions of the trace and signal views.</summary>
public sealed record NativeTraceFilters(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TraceSearchText = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TraceRuleText = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SignalsSearchText = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? Channels = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Identifiers = null);

/// <summary>A frame marker by exact timestamp. The category is the application's marker category name.</summary>
public sealed record NativeMarker(
    [property: JsonRequired] long TimestampNanoseconds,
    [property: JsonRequired] string Category,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label = null);

/// <summary>Analysis cursors on the shared time axis (nanoseconds). Null = not set.</summary>
public sealed record NativeCursors([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CursorANanoseconds = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CursorBNanoseconds = null);

/// <summary>Which view the analyst was on when the configuration was saved.</summary>
public sealed record NativeViewState([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActiveView = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SelectedNavIndex = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? WindowStartSeconds = null);

/// <summary>A named comparison between two source recordings (by <see cref="NativeFileReference.FileName"/>).</summary>
public sealed record NativeComparison(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string LeftSource,
    [property: JsonRequired] string RightSource,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? BindingNames = null);

/// <summary>
/// Sections added in schema 2. Every section is optional and a missing section means "keep the current behaviour",
/// so a schema 1 file read into this shape loses nothing and a schema 2 file with empty sections behaves like schema 1.
/// </summary>
public sealed record NativeAnalysisExtras(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<NativeFileReference>? Sources = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<NativeCurveStyle>? Curves = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<NativeAxisSetting>? Axes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<NativePanel>? Panels = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NativeTraceFilters? Filters = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<NativeMarker>? Markers = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NativeCursors? Cursors = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NativeViewState? View = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<NativeComparison>? Comparisons = null)
{
    public static NativeAnalysisExtras Empty { get; } = new();

    [JsonIgnore] public IReadOnlyList<NativeFileReference> SourceList => Sources ?? [];
    [JsonIgnore] public IReadOnlyList<NativeCurveStyle> CurveList => Curves ?? [];
    [JsonIgnore] public IReadOnlyList<NativeAxisSetting> AxisList => Axes ?? [];
    [JsonIgnore] public IReadOnlyList<NativePanel> PanelList => Panels ?? [];
    [JsonIgnore] public IReadOnlyList<NativeMarker> MarkerList => Markers ?? [];
    [JsonIgnore] public IReadOnlyList<NativeComparison> ComparisonList => Comparisons ?? [];
}

public enum NativeFileState { Ok, Missing, LengthMismatch, HashMismatch }

public sealed record NativeFileCheck(NativeFileReference Reference, NativeFileState State, string? ResolvedPath)
{
    public string Describe() => State switch
    {
        NativeFileState.Ok => $"{Reference.FileName}: same file",
        NativeFileState.Missing => $"{Reference.FileName}: not found (looked for the saved name)",
        NativeFileState.LengthMismatch => $"{Reference.FileName}: found but its size differs from the saved one — it was modified",
        _ => $"{Reference.FileName}: found but its content (SHA-256) differs from the saved one — it was modified",
    };
}

/// <summary>Describes and re-checks source files referenced by a configuration. Hashing is streamed; files above
/// <see cref="MaxHashedBytes"/> are length-checked only and reported as such (null SHA-256), never silently skipped.</summary>
public static class NativeSourceReferenceChecker
{
    public const long MaxHashedBytes = 512L * 1024 * 1024;
    public const string KindTrace = "trace";
    public const string KindDbc = "dbc";
    public const string KindCatalog = "catalog";
    public const string KindMapping = "mapping";

    public static NativeFileReference Describe(string kind, string path, string? baseDirectory = null)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Source file was not found.", path);
        string? hash = null;
        if (info.Length <= MaxHashedBytes)
        {
            using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        string? relative = null;
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            var candidate = Path.GetRelativePath(baseDirectory, info.FullName);
            if (!candidate.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(candidate)) relative = candidate;
        }
        return new NativeFileReference(kind, info.Name, relative, info.Length, hash);
    }

    /// <summary>Looks each reference up in <paramref name="searchDirectories"/> (relative path first, then file name) and in
    /// <paramref name="knownPaths"/> (files already open, matched by file name). Never guesses by similarity.</summary>
    public static IReadOnlyList<NativeFileCheck> Check(IReadOnlyList<NativeFileReference> references,
        IEnumerable<string> searchDirectories, IEnumerable<string>? knownPaths = null)
    {
        var directories = searchDirectories.Where(d => !string.IsNullOrWhiteSpace(d)).ToArray();
        var known = (knownPaths ?? []).Where(File.Exists).ToArray();
        var results = new List<NativeFileCheck>(references.Count);
        foreach (var reference in references)
        {
            var path = Locate(reference, directories, known);
            if (path is null) { results.Add(new NativeFileCheck(reference, NativeFileState.Missing, null)); continue; }
            var info = new FileInfo(path);
            if (info.Length != reference.Length) { results.Add(new NativeFileCheck(reference, NativeFileState.LengthMismatch, path)); continue; }
            if (reference.Sha256 is null) { results.Add(new NativeFileCheck(reference, NativeFileState.Ok, path)); continue; }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            results.Add(new NativeFileCheck(reference, string.Equals(actual, reference.Sha256, StringComparison.Ordinal)
                ? NativeFileState.Ok : NativeFileState.HashMismatch, path));
        }
        return results;
    }

    private static string? Locate(NativeFileReference reference, string[] directories, string[] known)
    {
        foreach (var directory in directories)
        {
            if (!string.IsNullOrWhiteSpace(reference.RelativePath))
            {
                var viaRelative = Path.GetFullPath(Path.Combine(directory, reference.RelativePath));
                if (File.Exists(viaRelative)) return viaRelative;
            }
            var byName = Path.Combine(directory, reference.FileName);
            if (File.Exists(byName)) return byName;
        }
        return known.FirstOrDefault(p => string.Equals(Path.GetFileName(p), reference.FileName, StringComparison.OrdinalIgnoreCase));
    }
}

internal static class NativeAnalysisExtrasValidator
{
    public const int MaxSources = 64, MaxCurves = 4096, MaxAxes = 64, MaxPanels = 64, MaxMarkers = 5000, MaxComparisons = 64,
        MaxListItems = 256, MaxText = 512;
    private static readonly Regex Color = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] Kinds = [NativeSourceReferenceChecker.KindTrace, NativeSourceReferenceChecker.KindDbc,
        NativeSourceReferenceChecker.KindCatalog, NativeSourceReferenceChecker.KindMapping];

    public static void Validate(NativeAnalysisExtras extras, IReadOnlySet<string> bindingNames)
    {
        if (extras.SourceList.Count > MaxSources || extras.CurveList.Count > MaxCurves || extras.AxisList.Count > MaxAxes ||
            extras.PanelList.Count > MaxPanels || extras.MarkerList.Count > MaxMarkers || extras.ComparisonList.Count > MaxComparisons)
            throw new InvalidDataException("Configuration extras exceed the allowed number of entries.");

        foreach (var source in extras.SourceList)
        {
            if (source is null || !Kinds.Contains(source.Kind) || !Text(source.FileName) || source.Length < 0 ||
                (source.RelativePath is not null && !Text(source.RelativePath)) ||
                (source.Sha256 is not null && !Regex.IsMatch(source.Sha256, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)))
                throw new InvalidDataException("A source file reference is invalid (kind, name, size or SHA-256).");
        }
        var curveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var curve in extras.CurveList)
        {
            if (curve is null || !bindingNames.Contains(curve.BindingName) || !curveNames.Add(curve.BindingName) ||
                (curve.Color is not null && !Color.IsMatch(curve.Color)))
                throw new InvalidDataException("A curve style refers to an unknown or repeated binding, or has an invalid #RRGGBB colour.");
        }
        foreach (var axis in extras.AxisList)
        {
            if (axis is null || !Text(axis.Name) || axis.BindingNames is null || axis.BindingNames.Count > MaxListItems ||
                axis.BindingNames.Any(n => !bindingNames.Contains(n)) ||
                (axis.Min is { } min && !double.IsFinite(min)) || (axis.Max is { } max && !double.IsFinite(max)) ||
                (axis.Min is { } lo && axis.Max is { } hi && lo >= hi) ||
                axis.Scale is not ("linear" or "log") ||
                (axis.Scale == "log" && (axis.Min is <= 0 || axis.Max is <= 0)) ||
                (axis.Unit is not null && axis.Unit.Length > 128))
                throw new InvalidDataException("An axis setting is invalid (unknown binding, limits, scale or unit).");
        }
        foreach (var panel in extras.PanelList)
        {
            if (panel is null || !Text(panel.Title) || panel.BindingNames is null || panel.BindingNames.Count > MaxListItems ||
                panel.BindingNames.Any(n => !bindingNames.Contains(n)) || !double.IsFinite(panel.HeightWeight) || panel.HeightWeight is <= 0 or > 100)
                throw new InvalidDataException("A plot panel is invalid (unknown binding or height weight).");
        }
        if (extras.Filters is { } f)
        {
            if ((f.TraceSearchText?.Length ?? 0) > MaxText || (f.TraceRuleText?.Length ?? 0) > 4096 || (f.SignalsSearchText?.Length ?? 0) > MaxText ||
                (f.Channels?.Count ?? 0) > MaxListItems || (f.Channels?.Any(c => c < 0) ?? false) ||
                (f.Identifiers?.Count ?? 0) > MaxListItems || (f.Identifiers?.Any(i => !Text(i)) ?? false))
                throw new InvalidDataException("Trace filters are invalid or too long.");
        }
        foreach (var marker in extras.MarkerList)
        {
            if (marker is null || marker.TimestampNanoseconds < 0 || !Text(marker.Category) || (marker.Label is not null && marker.Label.Length > 120))
                throw new InvalidDataException("A marker has an invalid timestamp, category or label.");
        }
        if (extras.Cursors is { } c && ((c.CursorANanoseconds ?? 0) < 0 || (c.CursorBNanoseconds ?? 0) < 0))
            throw new InvalidDataException("A cursor position cannot be negative.");
        if (extras.View is { } v && ((v.ActiveView?.Length ?? 0) > 120 || (v.SelectedNavIndex ?? 0) < 0 ||
            (v.WindowStartSeconds is { } w && (!double.IsFinite(w) || w < 0))))
            throw new InvalidDataException("The saved view state is invalid.");
        foreach (var cmp in extras.ComparisonList)
        {
            if (cmp is null || !Text(cmp.Name) || !Text(cmp.LeftSource) || !Text(cmp.RightSource) ||
                (cmp.BindingNames?.Count ?? 0) > MaxListItems || (cmp.BindingNames?.Any(n => !bindingNames.Contains(n)) ?? false))
                throw new InvalidDataException("A saved comparison is invalid.");
        }
    }

    private static bool Text(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaxText && !value.Any(char.IsControl);
}
