using System.Text;
using RbgCanInsight.Analysis.Projects;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class AnalysisProjectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rbgproj-" + Guid.NewGuid().ToString("N"));

    public AnalysisProjectTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Make(string rel, string content)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    private (string proj, AnalysisProject p) Build()
    {
        var proj = Path.Combine(_root, "work", "a.rbgproj");
        Directory.CreateDirectory(Path.GetDirectoryName(proj)!);
        var trace = Make("work/data/t.blf", "trace-bytes");
        var dbc = Make("work/dbc/x.dbc", "VERSION \"\"");
        var cdd = Make("work/cat/c.cdd", "<cdd/>");
        var p = new AnalysisProject
        {
            Name = "Demo",
            CreatedUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            ModifiedUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 6, TimeSpan.Zero),
            TraceColumnLayout = "Diagnostics",
        };
        p.TraceFiles.Add(ProjectStore.CreateFileRef<TraceFileRef>(proj, trace));
        var d = ProjectStore.CreateFileRef<DbcFileRef>(proj, dbc); d.BusChannel = 2; d.J1939Pgn = true;
        p.DbcFiles.Add(d);
        var c = ProjectStore.CreateFileRef<CatalogFileRef>(proj, cdd); c.Kind = "CDD";
        p.CatalogFiles.Add(c);
        p.Filters.Add(new SavedFilter { Name = "f1", SearchText = "0x123" });
        p.Markers.Add(new ProjectMarker { FrameIndex = 42, TimestampSeconds = 1.5, Text = "hit", Category = "fault" });
        p.Annotations.Add(new ProjectAnnotation { Id = "a1", StartSeconds = 1.0, EndSeconds = 2.0, Title = "T", Note = "N", Tags = { "x", "y" }, Author = "me" });
        p.PlottedSignals.Add(new PlottedSignal { Message = "M", Signal = "S", Channel = 1, DbcName = "x.dbc", Identifier = 0x18FEF100, Extended = true });
        return (proj, p);
    }

    [Fact]
    public void CaptureMultipleTraces_RoundTripsEverySourceAndAlignment()
    {
        var projectPath = Path.Combine(_root, "work", "session.rbgproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        var first = Make("work/a.asc", "frame-a");
        var second = Make("work/b.asc", "frame-b");
        var capture = ProjectAssembler.Capture(
            projectPath, null, new[] { first, second, first },
            Array.Empty<ProjectDbcSource>(), Array.Empty<string>(),
            Array.Empty<ProjectMarker>(), "SeparateChannels");

        Assert.Empty(capture.SkippedFiles);
        Assert.Equal(2, capture.Project.TraceFiles.Count);
        Assert.Equal("SeparateChannels", capture.Project.TraceMergeMode);

        ProjectStore.Save(projectPath, capture.Project);
        var loaded = ProjectStore.Load(projectPath);
        Assert.True(loaded.IsClean);
        var plan = ProjectAssembler.Plan(projectPath, loaded, false);
        // Plan() returns normalized full paths (native separators); Make() builds
        // them with a '/' segment, so compare the normalized form.
        Assert.Equal(new[] { Path.GetFullPath(first), Path.GetFullPath(second) }, plan.TracePaths);
        Assert.Equal("SeparateChannels", loaded.Project.TraceMergeMode);
    }

    [Fact]
    public void CaptureMissingTrace_DoesNotStoreUnverifiedSource()
    {
        var projectPath = Path.Combine(_root, "work", "missing.rbgproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        var present = Make("work/present.asc", "valid");
        var absent = Path.Combine(_root, "work", "absent.asc");
        var capture = ProjectAssembler.Capture(projectPath, null,
            new[] { present, absent }, Array.Empty<ProjectDbcSource>(),
            Array.Empty<string>(), Array.Empty<ProjectMarker>());
        Assert.Single(capture.Project.TraceFiles);
        Assert.Equal(absent, Assert.Single(capture.SkippedFiles));
    }

    [Fact]
    public void RoundTrip_PreservesEverything_AndIsClean()
    {
        var (proj, p) = Build();
        ProjectStore.Save(proj, p);
        var r = ProjectStore.Load(proj);
        Assert.True(r.IsClean, string.Join("; ", r.Issues.Select(i => i.Message)));
        var q = r.Project;
        Assert.Equal("Demo", q.Name);
        Assert.Equal(p.CreatedUtc, q.CreatedUtc);
        Assert.Equal("Diagnostics", q.TraceColumnLayout);
        Assert.Equal(p.TraceFiles[0].Sha256, q.TraceFiles[0].Sha256);
        Assert.Equal(11, q.TraceFiles[0].Length);
        Assert.Equal(2, q.DbcFiles[0].BusChannel);
        Assert.True(q.DbcFiles[0].J1939Pgn);
        Assert.Equal("CDD", q.CatalogFiles[0].Kind);
        Assert.Equal(42, q.Markers[0].FrameIndex);
        Assert.Equal(new[] { "x", "y" }, q.Annotations[0].Tags);
        Assert.Equal(0x18FEF100u, q.PlottedSignals[0].Identifier);
        Assert.True(q.PlottedSignals[0].Extended);
        Assert.Equal("data/t.blf", q.TraceFiles[0].Path);
        var bytes = File.ReadAllBytes(proj);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public void MovingProjectDirectory_KeepsRelativePathsValid()
    {
        var (proj, p) = Build();
        ProjectStore.Save(proj, p);
        var moved = Path.Combine(_root, "moved");
        Directory.Move(Path.Combine(_root, "work"), moved);
        var r = ProjectStore.Load(Path.Combine(moved, "a.rbgproj"));
        Assert.True(r.IsClean, string.Join("; ", r.Issues.Select(i => i.Message)));
    }

    [Fact]
    public void ChangedFile_ReportsHashMismatch_NeverClean()
    {
        var (proj, p) = Build();
        ProjectStore.Save(proj, p);
        File.WriteAllText(Path.Combine(_root, "work/data/t.blf"), "trace-BYTES"); // same length
        var r = ProjectStore.Load(proj);
        var i = Assert.Single(r.Issues);
        Assert.Equal(ProjectIssueKind.HashMismatch, i.Kind);
        Assert.False(r.IsClean);

        File.WriteAllText(Path.Combine(_root, "work/data/t.blf"), "longer content");
        Assert.Contains(ProjectStore.Load(proj).Issues, x => x.Kind == ProjectIssueKind.LengthMismatch);
    }

    [Fact]
    public void MissingFile_IsReported()
    {
        var (proj, p) = Build();
        ProjectStore.Save(proj, p);
        File.Delete(Path.Combine(_root, "work/dbc/x.dbc"));
        var i = Assert.Single(ProjectStore.Load(proj).Issues);
        Assert.Equal(ProjectIssueKind.MissingFile, i.Kind);
        Assert.Equal("dbcFiles", i.Section);
    }

    [Fact]
    public void ExternalFile_UsesAbsolutePath_AndIsReportedNotRead()
    {
        var (proj, p) = Build();
        var outside = Make("elsewhere/o.dbc", "x");
        var e = ProjectStore.CreateFileRef<DbcFileRef>(proj, outside);
        Assert.True(e.External);
        Assert.True(Path.IsPathRooted(e.Path));
        p.DbcFiles.Add(e);
        ProjectStore.Save(proj, p);
        var r = ProjectStore.Load(proj);
        Assert.Contains(r.Issues, x => x.Kind == ProjectIssueKind.ExternalPathNotRead && x.ResolvedPath is null);
        Assert.Null(ProjectStore.ResolvePath(proj, e));
    }

    [Theory]
    [InlineData("../elsewhere/o.dbc")]
    [InlineData("data/../../elsewhere/o.dbc")]
    public void TraversalPath_NotFlaggedExternal_IsRefused(string rel)
    {
        var (proj, p) = Build();
        Make("elsewhere/o.dbc", "x");
        p.DbcFiles.Add(new DbcFileRef { Path = rel, Sha256 = "00", Length = 1 });
        p.DbcFiles.Add(new DbcFileRef { Path = Path.Combine(_root, "elsewhere", "o.dbc"), Sha256 = "00", Length = 1 });
        ProjectStore.Save(proj, p);
        var issues = ProjectStore.Load(proj).Issues.Where(i => i.Kind == ProjectIssueKind.PathEscapesProject).ToList();
        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Null(i.ResolvedPath));
    }

    [Fact]
    public void UnknownFields_ArePreservedAndDoNotFail()
    {
        var (proj, p) = Build();
        ProjectStore.Save(proj, p);
        var text = File.ReadAllText(proj)
            .Replace("\"name\": \"Demo\"", "\"name\": \"Demo\",\n  \"futureThing\": {\"a\": [1,2]}")
            .Replace("\"frameIndex\": 42", "\"frameIndex\": 42,\n      \"color\": \"red\"");
        File.WriteAllText(proj, text);
        var r = ProjectStore.Load(proj);
        Assert.True(r.IsClean);
        Assert.True(r.Project.ExtensionData!.ContainsKey("futureThing"));
        ProjectStore.Save(proj, r.Project);
        var again = File.ReadAllText(proj);
        Assert.Contains("futureThing", again);
        Assert.Contains("\"color\": \"red\"", again);
    }

    [Fact]
    public void NewerMajorSchema_ThrowsClearError()
    {
        var proj = Path.Combine(_root, "n.rbgproj");
        File.WriteAllText(proj, "{\"schemaVersion\": 99, \"name\": \"x\"}");
        var ex = Assert.Throws<ProjectFormatException>(() => ProjectStore.Load(proj));
        Assert.Contains("newer", ex.Message);
    }

    [Theory]
    [InlineData("{\"name\":\"x\"}")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void MalformedOrVersionless_Throws(string content)
    {
        var proj = Path.Combine(_root, "m.rbgproj");
        File.WriteAllText(proj, content);
        Assert.Throws<ProjectFormatException>(() => ProjectStore.Load(proj));
    }

    [Fact]
    public void Save_IsDeterministic()
    {
        var (proj, p) = Build();
        ProjectStore.Save(proj, p);
        var a = File.ReadAllBytes(proj);
        ProjectStore.Save(proj, p);
        var b = File.ReadAllBytes(proj);
        Assert.Equal(a, b);
        Assert.DoesNotContain((byte)'\r', a);
        var text = Encoding.UTF8.GetString(a);
        Assert.True(text.IndexOf("\"schemaVersion\"") < text.IndexOf("\"name\""));
    }

    [Fact]
    public void FailedSave_LeavesOldFileIntact_AndNoTempFiles()
    {
        if (!OperatingSystem.IsWindows()) return; // relies on Windows sharing semantics for File.Replace
        var (proj, p) = Build();
        ProjectStore.Save(proj, p);
        var before = File.ReadAllBytes(proj);
        p.Name = "Changed";
        using (new FileStream(proj, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => ProjectStore.Save(proj, p));
        }
        Assert.Equal(before, File.ReadAllBytes(proj));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(proj)!, "*.tmp"));
        Assert.Equal("Demo", ProjectStore.Load(proj).Project.Name);
    }

    [Fact]
    public void AnnotationExport_NeutralisesFormulas()
    {
        var p = new AnalysisProject { Name = "n" };
        foreach (var s in new[] { "=1+1", "+cmd", "-2", "@SUM(A1)" })
            p.Annotations.Add(new ProjectAnnotation { Id = "i", StartFrame = 1, EndFrame = 2, Title = s, Note = s, Author = s, Tags = { s } });
        p.Annotations.Add(new ProjectAnnotation { Id = "q", StartSeconds = 1, EndSeconds = 2, Title = "a,\"b\"", Note = "l1\nl2" });
        var csv = ProjectStore.ExportAnnotationsCsv(p);
        Assert.Contains(",'=1+1,", csv);
        Assert.Contains("'+cmd", csv);
        Assert.Contains("'-2", csv);
        Assert.Contains("'@SUM(A1)", csv);
        Assert.DoesNotContain(",=1+1", csv);
        Assert.DoesNotContain(",@SUM", csv);
        Assert.Contains("\"a,\"\"b\"\"\"", csv);
        Assert.Contains("\"l1\nl2\"", csv);
        var md = ProjectStore.ExportAnnotationsMarkdown(p);
        Assert.Contains("## ", md);
        Assert.Contains("Time: 1 s to 2 s", md);
    }
}
