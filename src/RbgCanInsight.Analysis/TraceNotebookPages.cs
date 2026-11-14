namespace RbgCanInsight.Analysis;

/// <summary>
/// A saved, repeatable Trace-grid view on one imported dataset. It is a declarative
/// view recipe, never a copy of source frames or a claim that UDS/plots are scoped.
/// SourcePath is the recorded file identity; null means the complete session.
/// </summary>
/// <summary>A safe, deterministic analysis cell recipe. Its table is
/// recomputed from original frames and is never trusted as persisted evidence.
/// The owning analysis tab supplies the exact source and filter.</summary>
public sealed record TraceNotebookCell(
    string Id, string Title, TraceAggregateKind Kind);

/// <summary>Reproducible, bounded diagnostic investigation parameters.
 /// Contains no source CAN bytes or already-decoded results. A non-null
 /// recording path must resolve to an imported physical file on replay.</summary>
public sealed record TraceNotebookDiagnosticRecipe(
    string DtcSearchText,
    int DtcStateFilterIndex,
    string MappingSearchText,
    bool PairedMappingsOnly,
    string? OriginalSourcePath = null);

public sealed record TraceNotebookPage(
    string Id,
    string Title,
    string Query,
    int ViewModeIndex,
    int TimeDisplayModeIndex,
    string? SourcePath = null,
    string Notes = "",
    IReadOnlyList<TraceNotebookCell>? Cells = null,
    TraceNotebookDiagnosticRecipe? DiagnosticRecipe = null);

/// <summary>
/// Defensive, version-neutral handling for notebook-style saved Trace views.
/// Sessions are user-editable JSON; malformed, duplicated or huge pages must not
/// allocate unbounded controls, inject an invalid view mode or silently bind an
/// old page to an unrelated source file.
/// </summary>
public static class TraceNotebookPages
{
    public const int MaxPages = 24;
    public const int MaxTitleChars = 80;
    public const int MaxQueryChars = 2048;
    public const int MaxNotesChars = 8192;
    public const int MaxCellsPerPage = 12;
    public const int MaxCellTitleChars = 80;
    public const int MaxDiagnosticSearchChars = 256;

    public static TraceNotebookDiagnosticRecipe? NormalizeDiagnosticRecipe(
        TraceNotebookDiagnosticRecipe? recipe)
    {
        if (recipe is null) return null;
        return recipe with
        {
            DtcSearchText = (recipe.DtcSearchText ?? "")[..Math.Min(
                (recipe.DtcSearchText ?? "").Length, MaxDiagnosticSearchChars)],
            DtcStateFilterIndex = Math.Clamp(recipe.DtcStateFilterIndex, 0, 3),
            MappingSearchText = (recipe.MappingSearchText ?? "")[..Math.Min(
                (recipe.MappingSearchText ?? "").Length, MaxDiagnosticSearchChars)],
            OriginalSourcePath = string.IsNullOrWhiteSpace(recipe.OriginalSourcePath)
                ? null : recipe.OriginalSourcePath
        };
    }

    public static TraceNotebookCell NewCell(TraceAggregateKind kind, string? title = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var label = title ?? (kind switch
        {
            TraceAggregateKind.BySource => "Frames by recording",
            TraceAggregateKind.BySecond => "Traffic bursts by second and bus",
            _ => "Frames by CAN identifier"
        });
        return new TraceNotebookCell(Guid.NewGuid().ToString("N"), label, kind);
    }

    public static IReadOnlyList<TraceNotebookCell> NormalizeCells(
        IEnumerable<TraceNotebookCell>? cells)
    {
        if (cells is null) return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<TraceNotebookCell>();
        foreach (var cell in cells)
        {
            if (cell is null || !Enum.IsDefined(cell.Kind)) continue;
            var id = string.IsNullOrWhiteSpace(cell.Id) || cell.Id.Length > 64
                ? Guid.NewGuid().ToString("N") : cell.Id.Trim();
            if (!seen.Add(id)) continue;
            var title = string.IsNullOrWhiteSpace(cell.Title)
                ? NewCell(cell.Kind).Title : cell.Title.Trim();
            if (title.Length > MaxCellTitleChars)
                title = title[..MaxCellTitleChars];
            results.Add(cell with { Id = id, Title = title });
            if (results.Count == MaxCellsPerPage) break;
        }
        return results;
    }

    public static TraceNotebookPage New(string title, string query, int viewMode,
        int timeMode, string? sourcePath = null, string notes = "") =>
        Normalize(new TraceNotebookPage(Guid.NewGuid().ToString("N"), title,
            query, viewMode, timeMode, sourcePath, notes));

    public static TraceNotebookPage Normalize(TraceNotebookPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var id = string.IsNullOrWhiteSpace(page.Id) || page.Id.Length > 64
            ? Guid.NewGuid().ToString("N") : page.Id.Trim();
        var title = string.IsNullOrWhiteSpace(page.Title)
            ? "Untitled analysis" : page.Title.Trim();
        if (title.Length > MaxTitleChars) title = title[..MaxTitleChars];
        var query = page.Query ?? "";
        if (query.Length > MaxQueryChars) query = query[..MaxQueryChars];
        var notes = page.Notes ?? "";
        if (notes.Length > MaxNotesChars) notes = notes[..MaxNotesChars];
        return page with
        {
            Id = id,
            Title = title,
            Query = query,
            ViewModeIndex = Math.Clamp(page.ViewModeIndex, 0, 2),
            TimeDisplayModeIndex = Math.Clamp(page.TimeDisplayModeIndex, 0, 3),
            SourcePath = string.IsNullOrWhiteSpace(page.SourcePath) ? null : page.SourcePath,
            Notes = notes,
            Cells = NormalizeCells(page.Cells),
            DiagnosticRecipe = NormalizeDiagnosticRecipe(page.DiagnosticRecipe)
        };
    }

    public static IReadOnlyList<TraceNotebookPage> NormalizeMany(
        IEnumerable<TraceNotebookPage>? pages)
    {
        if (pages is null) return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TraceNotebookPage>(MaxPages);
        foreach (var page in pages)
        {
            if (page is null) continue;
            var normalized = Normalize(page);
            if (!seen.Add(normalized.Id)) continue;
            result.Add(normalized);
            if (result.Count == MaxPages) break;
        }
        return result;
    }

    /// <summary>Return the exact physical source number, or -1 when the source
    /// is unavailable. Never select a file by display name alone.</summary>
    public static int FindSource(string? sourcePath, IReadOnlyList<string> currentPaths)
    {
        ArgumentNullException.ThrowIfNull(currentPaths);
        if (sourcePath is null) return -1;
        for (var i = 0; i < currentPaths.Count; i++)
            if (string.Equals(currentPaths[i], sourcePath, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>When a session is moved, rebind only through its explicit,
    /// resolved old-path -> new-path pairs. Missing mappings remain unresolved
    /// rather than guessing a same-name file or silently falling back to All.</summary>
    public static IReadOnlyList<TraceNotebookPage> RebindSources(
        IEnumerable<TraceNotebookPage>? pages,
        IReadOnlyDictionary<string, string> explicitlyResolved)
    {
        ArgumentNullException.ThrowIfNull(explicitlyResolved);
        return NormalizeMany(pages).Select(page =>
        {
            var tracePath = page.SourcePath;
            if (tracePath is not null &&
                explicitlyResolved.TryGetValue(tracePath, out var mappedTrace))
                tracePath = mappedTrace;
            var diagnostics = page.DiagnosticRecipe;
            if (diagnostics?.OriginalSourcePath is { } diagnosticPath &&
                explicitlyResolved.TryGetValue(diagnosticPath, out var mappedDiagnostic))
                diagnostics = diagnostics with { OriginalSourcePath = mappedDiagnostic };
            return page with
            {
                SourcePath = tracePath,
                DiagnosticRecipe = diagnostics
            };
        }).ToArray();
    }
}
