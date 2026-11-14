using System.Security.Cryptography;
using System.Text.Json;

namespace RbgCanInsight.Analysis;

/// <summary>One source file an analysis was built on. The hash and length let a later reopen detect a file that was
/// replaced or edited: a saved analysis is never silently re-bound to different data.</summary>
public sealed record StudioSourceRef(string Kind, string Path, string Sha256, long Length);

/// <summary>
/// A named, reusable Data Studio analysis: which recordings/DBC/catalogs it used, the Trace filters, the analysis tabs and the
/// section the user was in. It stores recipes only: no CAN frames, no decoded results, no copies of the source files.
/// Local JSON, no cloud.
/// </summary>
public sealed record StudioSavedAnalysis(
    int SchemaVersion,
    string Id,
    string Name,
    DateTime SavedUtc,
    IReadOnlyList<StudioSourceRef> Sources,
    string MergeMode,
    string TraceSearchText,
    string TraceRuleText,
    int TraceViewModeIndex,
    int SectionIndex,
    IReadOnlyList<TraceNotebookPage> Pages,
    string? ActiveTabId);

public enum StudioSourceState { Ok, Missing, Changed }

public sealed record StudioSourceCheck(StudioSourceRef Source, StudioSourceState State);

public sealed class StudioSavedAnalysisException(string message) : IOException(message);

/// <summary>
/// Folder of saved analyses, one atomic file each (<c>&lt;id&gt;.rbganalysis.json</c>). Unreadable or newer-version files are
/// reported, never deleted or overwritten. Names are unique (case-insensitive). The id is a GUID, so a file name never comes from
/// user text.
/// </summary>
public sealed class StudioSavedAnalysisStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxNameChars = 80;
    public const int MaxAnalyses = 200;
    private const string Extension = ".rbganalysis.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _directory;

    public StudioSavedAnalysisStore(string directory) => _directory = directory;

    public static string NormalizeName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) throw new StudioSavedAnalysisException("Give the analysis a name.");
        return trimmed.Length > MaxNameChars ? trimmed[..MaxNameChars].TrimEnd() : trimmed;
    }

    private string PathFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new StudioSavedAnalysisException("Invalid analysis id.");
        return Path.Combine(_directory, id + Extension);
    }

    /// <summary>Saved analyses, newest first. Files that cannot be used are listed in <paramref name="problems"/>.</summary>
    public IReadOnlyList<StudioSavedAnalysis> List(out IReadOnlyList<string> problems)
    {
        var found = new List<StudioSavedAnalysis>();
        var issues = new List<string>();
        if (Directory.Exists(_directory))
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*" + Extension))
            {
                try { found.Add(ReadFile(file)); }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    issues.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        problems = issues;
        return found.OrderByDescending(a => a.SavedUtc).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public StudioSavedAnalysis? Load(string id)
    {
        var path = PathFor(id);
        return File.Exists(path) ? ReadFile(path) : null;
    }

    private static StudioSavedAnalysis ReadFile(string file)
    {
        var analysis = JsonSerializer.Deserialize<StudioSavedAnalysis>(File.ReadAllText(file), Json)
            ?? throw new StudioSavedAnalysisException("Empty analysis file.");
        if (analysis.SchemaVersion > CurrentSchemaVersion)
            throw new StudioSavedAnalysisException(
                $"Saved with a newer version of the app (format {analysis.SchemaVersion}); it was left untouched.");
        if (analysis.SchemaVersion < 1 || !Guid.TryParseExact(analysis.Id, "N", out _) || string.IsNullOrWhiteSpace(analysis.Name))
            throw new StudioSavedAnalysisException("Not a valid saved analysis.");
        return analysis with { Sources = analysis.Sources ?? [], Pages = analysis.Pages ?? [] };
    }

    /// <summary>Saves a new analysis. A name already in use is refused unless <paramref name="replaceSameName"/> is true, which
    /// replaces only that one entry.</summary>
    public StudioSavedAnalysis Save(StudioSavedAnalysis analysis, bool replaceSameName = false)
    {
        var name = NormalizeName(analysis.Name);
        var existing = List(out _);
        var same = existing.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (same is not null && !replaceSameName)
            throw new StudioSavedAnalysisException($"An analysis named “{same.Name}” already exists.");
        if (same is null && existing.Count >= MaxAnalyses)
            throw new StudioSavedAnalysisException($"At most {MaxAnalyses} saved analyses are kept; delete one first.");

        var toSave = analysis with
        {
            SchemaVersion = CurrentSchemaVersion,
            Name = name,
            Id = same is not null ? same.Id : analysis.Id,
            Sources = analysis.Sources ?? [],
            Pages = analysis.Pages ?? [],
        };
        WriteAtomic(PathFor(toSave.Id), toSave);
        return toSave;
    }

    public StudioSavedAnalysis Rename(string id, string newName)
    {
        var name = NormalizeName(newName);
        var current = Load(id) ?? throw new StudioSavedAnalysisException("The analysis no longer exists.");
        var clash = List(out _).FirstOrDefault(a => a.Id != id && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (clash is not null) throw new StudioSavedAnalysisException($"An analysis named “{clash.Name}” already exists.");
        var renamed = current with { Name = name };
        WriteAtomic(PathFor(id), renamed);
        return renamed;
    }

    public bool Delete(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private void WriteAtomic(string path, StudioSavedAnalysis analysis)
    {
        Directory.CreateDirectory(_directory);
        var temp = path + ".tmp" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, analysis, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    public static StudioSourceRef DescribeSource(string kind, string path, CancellationToken ct = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var buffer = new byte[1 << 16];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return new StudioSourceRef(kind, Path.GetFullPath(path), Convert.ToHexString(sha.Hash!).ToLowerInvariant(), stream.Length);
    }

    /// <summary>Compares every saved source with the file on disk: missing, same length and hash (Ok), or changed.</summary>
    public static IReadOnlyList<StudioSourceCheck> CheckSources(StudioSavedAnalysis analysis, CancellationToken ct = default)
    {
        var result = new List<StudioSourceCheck>();
        foreach (var source in analysis.Sources)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(source.Path)) { result.Add(new StudioSourceCheck(source, StudioSourceState.Missing)); continue; }
            var length = new FileInfo(source.Path).Length;
            if (length != source.Length) { result.Add(new StudioSourceCheck(source, StudioSourceState.Changed)); continue; }
            var now = DescribeSource(source.Kind, source.Path, ct);
            result.Add(new StudioSourceCheck(source, string.Equals(now.Sha256, source.Sha256, StringComparison.OrdinalIgnoreCase)
                ? StudioSourceState.Ok : StudioSourceState.Changed));
        }
        return result;
    }

    /// <summary>Text for the user when sources are missing or changed; empty when everything matches.</summary>
    public static string DescribeProblems(IEnumerable<StudioSourceCheck> checks)
    {
        var list = checks.ToList();
        var missing = list.Where(c => c.State == StudioSourceState.Missing).Select(c => Path.GetFileName(c.Source.Path)).ToList();
        var changed = list.Where(c => c.State == StudioSourceState.Changed).Select(c => Path.GetFileName(c.Source.Path)).ToList();
        var parts = new List<string>();
        if (missing.Count > 0) parts.Add($"Missing: {string.Join(", ", missing)}.");
        if (changed.Count > 0) parts.Add($"Changed since saved (different content): {string.Join(", ", changed)}.");
        return string.Join(" ", parts);
    }
}
