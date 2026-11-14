using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RbgCanInsight.Analysis.Projects;

public enum ProjectIssueKind
{
    MissingFile,
    HashMismatch,
    LengthMismatch,
    NoIntegrityRecord,
    ExternalPathNotRead,
    PathEscapesProject,
    UnreadableFile
}

/// <summary>A problem found while validating project file references. The file is NOT to be treated as identical.</summary>
public sealed record ProjectIssue(ProjectIssueKind Kind, string Section, string Path, string Message, string? ResolvedPath = null);

public sealed record ProjectLoadResult(AnalysisProject Project, IReadOnlyList<ProjectIssue> Issues)
{
    public bool IsClean => Issues.Count == 0;
}

public sealed class ProjectFormatException : Exception
{
    public ProjectFormatException(string message, Exception? inner = null) : base(message, inner) { }
}

public static class ProjectStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // file is never embedded in HTML
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly StringComparison PathCmp =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // ---------- file references ----------

    /// <summary>
    /// Builds a reference (with SHA-256 and length) for <paramref name="filePath"/>. Files inside the project
    /// directory get a relative '/'-separated path; others get an absolute path and External = true.
    /// </summary>
    public static T CreateFileRef<T>(string projectFilePath, string filePath) where T : ProjectFileRef, new()
    {
        var full = Path.GetFullPath(filePath);
        var projDir = ProjectDir(projectFilePath);
        var r = new T { Length = new FileInfo(full).Length, Sha256 = HashFile(full) };
        if (IsInside(projDir, full))
            r.Path = Path.GetRelativePath(projDir, full).Replace('\\', '/');
        else
        {
            r.Path = full;
            r.External = true;
        }
        return r;
    }

    /// <summary>Resolves a reference to a full path, or null when it is external or escapes the project directory.</summary>
    public static string? ResolvePath(string projectFilePath, ProjectFileRef r)
    {
        if (r.External || string.IsNullOrWhiteSpace(r.Path) || Path.IsPathRooted(r.Path)) return null;
        if (r.Path.IndexOf('\0') >= 0) return null;
        var projDir = ProjectDir(projectFilePath);
        var full = Path.GetFullPath(Path.Combine(projDir, r.Path.Replace('/', Path.DirectorySeparatorChar)));
        return IsInside(projDir, full) ? full : null;
    }

    // ---------- save ----------

    /// <summary>Atomic save: temp file in the same directory, then replace. The old file survives any failure.</summary>
    public static void Save(string path, AnalysisProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.SchemaVersion = AnalysisProject.CurrentSchemaVersion;
        project.Normalize();
        var json = JsonSerializer.Serialize(project, Options).Replace("\r\n", "\n");
        var bytes = new UTF8Encoding(false).GetBytes(json + "\n");

        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (File.Exists(full)) File.Replace(tmp, full, null);
            else File.Move(tmp, full);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    // ---------- load ----------

    /// <exception cref="ProjectFormatException">Malformed JSON, missing schemaVersion, or newer major version.</exception>
    public static ProjectLoadResult Load(string path)
    {
        var full = Path.GetFullPath(path);
        byte[] bytes = File.ReadAllBytes(full);
        AnalysisProject? p;
        try
        {
            using (var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("schemaVersion", out var v) ||
                    v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var ver))
                    throw new ProjectFormatException("Not an analysis project: missing or invalid 'schemaVersion'.");
                if (ver > AnalysisProject.CurrentSchemaVersion)
                    throw new ProjectFormatException(
                        $"Project schema version {ver} is newer than this application supports ({AnalysisProject.CurrentSchemaVersion}). Update the application to open it.");
                if (ver < 1)
                    throw new ProjectFormatException($"Invalid project schema version {ver}.");
            }
            p = JsonSerializer.Deserialize<AnalysisProject>(bytes, Options);
        }
        catch (JsonException ex)
        {
            throw new ProjectFormatException("Project file is not valid JSON: " + ex.Message, ex);
        }
        if (p is null) throw new ProjectFormatException("Project file is empty.");
        p.Normalize();

        var issues = new List<ProjectIssue>();
        foreach (var f in p.TraceFiles) Check(full, "traceFiles", f, issues);
        foreach (var f in p.DbcFiles) Check(full, "dbcFiles", f, issues);
        foreach (var f in p.CatalogFiles) Check(full, "catalogFiles", f, issues);
        return new ProjectLoadResult(p, issues);
    }

    private static void Check(string projectFile, string section, ProjectFileRef r, List<ProjectIssue> issues)
    {
        if (r.External)
        {
            issues.Add(new(ProjectIssueKind.ExternalPathNotRead, section, r.Path,
                "File is outside the project directory and was not read or verified."));
            return;
        }
        var resolved = ResolvePath(projectFile, r);
        if (resolved is null)
        {
            issues.Add(new(ProjectIssueKind.PathEscapesProject, section, r.Path,
                "Path is absolute or escapes the project directory and is not flagged external; refused."));
            return;
        }
        if (!File.Exists(resolved))
        {
            issues.Add(new(ProjectIssueKind.MissingFile, section, r.Path, "File not found.", resolved));
            return;
        }
        try
        {
            if (r.Sha256 is null && r.Length is null)
            {
                issues.Add(new(ProjectIssueKind.NoIntegrityRecord, section, r.Path, "No hash recorded; file identity cannot be verified.", resolved));
                return;
            }
            if (r.Length is long len && new FileInfo(resolved).Length != len)
            {
                issues.Add(new(ProjectIssueKind.LengthMismatch, section, r.Path, "File size differs from the recorded size; file has changed.", resolved));
                return;
            }
            if (r.Sha256 is not null && !string.Equals(HashFile(resolved), r.Sha256, StringComparison.OrdinalIgnoreCase))
                issues.Add(new(ProjectIssueKind.HashMismatch, section, r.Path, "SHA-256 differs from the recorded hash; file has changed.", resolved));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(new(ProjectIssueKind.UnreadableFile, section, r.Path, "File could not be read: " + ex.Message, resolved));
        }
    }

    // ---------- annotation export ----------

    public static string ExportAnnotationsCsv(AnalysisProject project)
    {
        var sb = new StringBuilder();
        sb.Append("Id,Title,RangeKind,Start,End,Tags,Author,Note\r\n");
        foreach (var a in project.Annotations)
        {
            bool time = a.StartSeconds.HasValue || a.EndSeconds.HasValue;
            string kind = time ? "time" : "frame";
            string s = time ? Num(a.StartSeconds) : Num(a.StartFrame);
            string e = time ? Num(a.EndSeconds) : Num(a.EndFrame);
            sb.Append(string.Join(",",
                Csv(a.Id), Csv(a.Title), kind, s, e,
                Csv(string.Join("; ", a.Tags ?? new())), Csv(a.Author), Csv(a.Note))).Append("\r\n");
        }
        return sb.ToString();
    }

    public static string ExportAnnotationsMarkdown(AnalysisProject project)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(Md(project.Name)).Append(" - annotations\n\n");
        foreach (var a in project.Annotations)
        {
            sb.Append("## ").Append(Md(a.Title)).Append("\n\n");
            bool time = a.StartSeconds.HasValue || a.EndSeconds.HasValue;
            sb.Append(time ? $"- Time: {Num(a.StartSeconds)} s to {Num(a.EndSeconds)} s\n"
                           : $"- Frames: {Num(a.StartFrame)} to {Num(a.EndFrame)}\n");
            if (a.Tags is { Count: > 0 }) sb.Append("- Tags: ").Append(Md(string.Join(", ", a.Tags))).Append('\n');
            if (!string.IsNullOrEmpty(a.Author)) sb.Append("- Author: ").Append(Md(a.Author)).Append('\n');
            if (!string.IsNullOrEmpty(a.Note)) sb.Append('\n').Append(Md(a.Note).Replace("\r\n", "\n").Replace("\n", "  \n")).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Num(double? v) => v?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "";
    private static string Num(long? v) => v?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";

    /// <summary>Neutralises spreadsheet formulas (leading = + - @, tab, CR) with a single quote, then RFC 4180 quoting.</summary>
    internal static string Csv(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
        return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static string Md(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '|' or '#') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ---------- helpers ----------

    private static string ProjectDir(string projectFilePath) => Path.GetDirectoryName(Path.GetFullPath(projectFilePath))!;

    private static bool IsInside(string dir, string full)
    {
        var d = dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
        return full.StartsWith(d, PathCmp);
    }

    private static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }
}
