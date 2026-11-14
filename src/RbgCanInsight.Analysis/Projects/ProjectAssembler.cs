namespace RbgCanInsight.Analysis.Projects;

/// <summary>Source file described by the running application when a project is saved.</summary>
public sealed record ProjectDbcSource(string Path, int? BusChannel, bool J1939Pgn);

/// <summary>Result of <see cref="ProjectAssembler.Capture"/>.</summary>
public sealed record ProjectCaptureResult(AnalysisProject Project, IReadOnlyList<string> SkippedFiles);

/// <summary>A DBC file the app should load, with the options recorded in the project.</summary>
public sealed record ProjectDbcLoad(string Path, int? BusChannel, bool J1939Pgn);

/// <summary>What the application should load after opening a project (UI-independent, testable).</summary>
public sealed record ProjectOpenPlan(
    IReadOnlyList<string> TracePaths,
    IReadOnlyList<ProjectDbcLoad> Dbcs,
    IReadOnlyList<string> CatalogPaths,
    IReadOnlyList<ProjectIssue> Skipped)
{
    public bool HasNothingToLoad => TracePaths.Count == 0 && Dbcs.Count == 0 && CatalogPaths.Count == 0;
}

public static class ProjectAssembler
{
    /// <summary>
    /// Builds the project to save. Annotations, filters, plotted signals, layout and unknown JSON properties of
    /// <paramref name="existing"/> are kept; file references and markers are replaced by the current state.
    /// Files that no longer exist are skipped and reported, never written with a made-up hash.
    /// </summary>
    public static ProjectCaptureResult Capture(
        string projectFilePath,
        AnalysisProject? existing,
        string? tracePath,
        IEnumerable<ProjectDbcSource> dbcs,
        IEnumerable<string> catalogPaths,
        IEnumerable<ProjectMarker> markers)
    => Capture(projectFilePath, existing,
        string.IsNullOrWhiteSpace(tracePath) ? Array.Empty<string>() : new[] { tracePath },
        dbcs, catalogPaths, markers);

    /// <summary>Capture all source logs in their original session order, not just the first one.</summary>
    public static ProjectCaptureResult Capture(
        string projectFilePath,
        AnalysisProject? existing,
        IEnumerable<string> tracePaths,
        IEnumerable<ProjectDbcSource> dbcs,
        IEnumerable<string> catalogPaths,
        IEnumerable<ProjectMarker> markers,
        string traceMergeMode = "ByTime")
    {
        var project = existing ?? new AnalysisProject { CreatedUtc = DateTimeOffset.UtcNow };
        if (string.IsNullOrWhiteSpace(project.Name))
            project.Name = Path.GetFileNameWithoutExtension(projectFilePath);
        var skipped = new List<string>();

        project.TraceMergeMode = string.IsNullOrWhiteSpace(traceMergeMode) ? "ByTime" : traceMergeMode;
        project.TraceFiles = new();
        foreach (var tracePath in tracePaths.Where(p => !string.IsNullOrWhiteSpace(p))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(tracePath))
                project.TraceFiles.Add(ProjectStore.CreateFileRef<TraceFileRef>(projectFilePath, tracePath));
            else
                skipped.Add(tracePath);
        }

        project.DbcFiles = new();
        foreach (var d in dbcs)
        {
            if (!File.Exists(d.Path)) { skipped.Add(d.Path); continue; }
            var r = ProjectStore.CreateFileRef<DbcFileRef>(projectFilePath, d.Path);
            r.BusChannel = d.BusChannel;
            r.J1939Pgn = d.J1939Pgn;
            project.DbcFiles.Add(r);
        }

        project.CatalogFiles = new();
        foreach (var c in catalogPaths)
        {
            if (!File.Exists(c)) { skipped.Add(c); continue; }
            var r = ProjectStore.CreateFileRef<CatalogFileRef>(projectFilePath, c);
            r.Kind = CatalogKind(c);
            project.CatalogFiles.Add(r);
        }

        project.Markers = markers.ToList();
        project.Touch();
        return new ProjectCaptureResult(project, skipped);
    }

    public static string CatalogKind(string path) =>
        string.Equals(Path.GetExtension(path), ".cdd", StringComparison.OrdinalIgnoreCase) ? "CDD" : "ODX";

    /// <summary>
    /// Decides which referenced files may be loaded. Files with a changed hash or size are loaded only when
    /// <paramref name="openChangedFiles"/> is true. Missing, external, unreadable or unverifiable files are never loaded.
    /// </summary>
    public static ProjectOpenPlan Plan(string projectFilePath, ProjectLoadResult loaded, bool openChangedFiles)
    {
        var blocked = new Dictionary<string, ProjectIssue>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in loaded.Issues)
        {
            bool changed = i.Kind is ProjectIssueKind.HashMismatch or ProjectIssueKind.LengthMismatch;
            if (changed && openChangedFiles) continue;
            blocked[i.Section + "|" + i.Path] = i;
        }
        var skipped = new List<ProjectIssue>();

        string? Resolve(string section, ProjectFileRef r)
        {
            if (blocked.TryGetValue(section + "|" + r.Path, out var issue)) { skipped.Add(issue); return null; }
            return ProjectStore.ResolvePath(projectFilePath, r);
        }

        var traces = loaded.Project.TraceFiles.Select(f => Resolve("traceFiles", f)).OfType<string>().ToList();
        var dbcs = new List<ProjectDbcLoad>();
        foreach (var f in loaded.Project.DbcFiles)
            if (Resolve("dbcFiles", f) is { } p) dbcs.Add(new ProjectDbcLoad(p, f.BusChannel, f.J1939Pgn));
        var cats = loaded.Project.CatalogFiles.Select(f => Resolve("catalogFiles", f)).OfType<string>().ToList();
        return new ProjectOpenPlan(traces, dbcs, cats, skipped);
    }

    public static bool HasChangedFiles(ProjectLoadResult loaded) =>
        loaded.Issues.Any(i => i.Kind is ProjectIssueKind.HashMismatch or ProjectIssueKind.LengthMismatch);

    /// <summary>Plain-language sentence for an issue.</summary>
    public static string Describe(ProjectIssue i) => i.Kind switch
    {
        ProjectIssueKind.MissingFile => $"{i.Path}: the file was not found. It will be skipped.",
        ProjectIssueKind.HashMismatch => $"{i.Path}: the file has changed since the project was saved (its checksum differs).",
        ProjectIssueKind.LengthMismatch => $"{i.Path}: the file has changed since the project was saved (its size differs).",
        ProjectIssueKind.NoIntegrityRecord => $"{i.Path}: the project holds no checksum for this file, so it cannot be verified. It will be skipped.",
        ProjectIssueKind.ExternalPathNotRead => $"{i.Path}: the file is outside the project folder and is not opened automatically. Open it manually.",
        ProjectIssueKind.PathEscapesProject => $"{i.Path}: the path points outside the project folder and was refused.",
        ProjectIssueKind.UnreadableFile => $"{i.Path}: the file could not be read. It will be skipped.",
        _ => $"{i.Path}: {i.Message}",
    };

    // ---------- annotations ----------

    public static List<string> ParseTags(string? text) =>
        (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static string NewAnnotationId(AnalysisProject project)
    {
        var n = project.Annotations.Count + 1;
        while (project.Annotations.Any(a => a.Id == "A" + n)) n++;
        return "A" + n;
    }
}
