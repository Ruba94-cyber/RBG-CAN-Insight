using RbgCanInsight.Analysis.Projects;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class ProjectAssemblerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rbgasm-" + Guid.NewGuid().ToString("N"));

    public ProjectAssemblerTests() => Directory.CreateDirectory(Path.Combine(_root, "work"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Make(string rel, string content)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void CaptureThenPlanRoundTripsFilesOptionsAndKeepsAnnotations()
    {
        var proj = Path.Combine(_root, "work", "a.rbgproj");
        var trace = Make("work/t.asc", "trace");
        var dbc = Make("work/x.dbc", "VERSION \"\"");
        var cdd = Make("work/c.cdd", "<cdd/>");
        var existing = new AnalysisProject { Name = "Keep" };
        existing.Annotations.Add(new ProjectAnnotation { Id = "A1", Title = "n", StartSeconds = 1, EndSeconds = 2 });

        var cap = ProjectAssembler.Capture(proj, existing, trace,
            [new ProjectDbcSource(dbc, 1, true), new ProjectDbcSource(Path.Combine(_root, "gone.dbc"), null, false)],
            [cdd], [new ProjectMarker { FrameIndex = 3, TimestampSeconds = 0.3, Category = "Manual" }]);
        Assert.Single(cap.SkippedFiles);
        ProjectStore.Save(proj, cap.Project);

        var loaded = ProjectStore.Load(proj);
        Assert.True(loaded.IsClean);
        Assert.Single(loaded.Project.Annotations);
        Assert.Equal("CDD", loaded.Project.CatalogFiles[0].Kind);
        var plan = ProjectAssembler.Plan(proj, loaded, openChangedFiles: false);
        Assert.Equal(Path.GetFullPath(trace), Path.GetFullPath(plan.TracePaths[0]), ignoreCase: true);
        Assert.Equal(1, plan.Dbcs[0].BusChannel);
        Assert.True(plan.Dbcs[0].J1939Pgn);
        Assert.Single(plan.CatalogPaths);
        Assert.Single(loaded.Project.Markers);
    }

    [Fact]
    public void ChangedFilesLoadOnlyWhenOpenAnywayAndMissingFilesNever()
    {
        var proj = Path.Combine(_root, "work", "b.rbgproj");
        var trace = Make("work/t.asc", "trace");
        var dbc = Make("work/x.dbc", "one");
        var cap = ProjectAssembler.Capture(proj, null, trace, [new ProjectDbcSource(dbc, null, false)], [], []);
        ProjectStore.Save(proj, cap.Project);
        File.WriteAllText(dbc, "two!"); // same-size change is not guaranteed; size differs here
        File.Delete(trace);

        var loaded = ProjectStore.Load(proj);
        Assert.True(ProjectAssembler.HasChangedFiles(loaded));
        var strict = ProjectAssembler.Plan(proj, loaded, false);
        Assert.Empty(strict.TracePaths);
        Assert.Empty(strict.Dbcs);
        Assert.Equal(2, strict.Skipped.Count);
        var anyway = ProjectAssembler.Plan(proj, loaded, true);
        Assert.Empty(anyway.TracePaths);          // missing: never loaded
        Assert.Single(anyway.Dbcs);               // changed: loaded when the user insists
        Assert.Contains(loaded.Issues, i => i.Kind == ProjectIssueKind.MissingFile);
        Assert.All(loaded.Issues, i => Assert.False(string.IsNullOrWhiteSpace(ProjectAssembler.Describe(i))));
    }

    [Fact]
    public void TagsAreSplitTrimmedAndDeduplicated()
    {
        Assert.Equal(["a", "b c"], ProjectAssembler.ParseTags(" a, b c ;A,, "));
        var p = new AnalysisProject();
        p.Annotations.Add(new ProjectAnnotation { Id = "A1" });
        Assert.Equal("A2", ProjectAssembler.NewAnnotationId(p));
    }

    [Fact]
    public void AnnotationExportContainsTitlesAndNeutralisesFormulas()
    {
        var p = new AnalysisProject { Name = "P" };
        p.Annotations.Add(new ProjectAnnotation { Id = "A1", Title = "=cmd", StartSeconds = 1, EndSeconds = 2, Tags = ["x"] });
        Assert.Contains("'=cmd", ProjectStore.ExportAnnotationsCsv(p));
        Assert.Contains("cmd", ProjectStore.ExportAnnotationsMarkdown(p));
    }
}

public sealed class TraceEditPlanTests
{
    private static List<CanFrame> Frames() =>
        new[] { 0.0, 0.1, 0.2, 0.3, 5.0, 5.1 }
            .Select((t, i) => new CanFrame(0, 0x100, new byte[] { (byte)i }, TraceEditPlan.ToNs(t), sequence: i)).ToList();

    [Fact]
    public void CutKeepsBothEndsInclusiveAndDoesNotTouchTheInput()
    {
        var src = Frames();
        var o = TraceEditPlan.Apply(src, new TraceEditRequest(TraceEditKind.Cut, FromSeconds: 0.1, ToSeconds: 0.3));
        Assert.Equal(3, o.FrameCount);
        Assert.Equal(0.1, o.FirstSeconds!.Value, 9);
        Assert.Equal(0.3, o.LastSeconds!.Value, 9);
        Assert.Equal(6, src.Count);
    }

    [Fact]
    public void ShiftMovesAllFramesAndRejectsNegativeResult()
    {
        var o = TraceEditPlan.Apply(Frames(), new TraceEditRequest(TraceEditKind.Shift, ShiftSeconds: 2));
        Assert.Equal(2.0, o.FirstSeconds!.Value, 9);
        Assert.Throws<ArgumentException>(() =>
            TraceEditPlan.Apply(Frames(), new TraceEditRequest(TraceEditKind.Shift, ShiftSeconds: -1)));
    }

    [Fact]
    public void SplitAtGapsMakesTwoSegmentsAndNamesFiles()
    {
        var o = TraceEditPlan.Apply(Frames(), new TraceEditRequest(TraceEditKind.SplitAtGaps, GapSeconds: 1));
        Assert.Equal([4, 2], o.Segments.Select(s => s.Count).ToArray());
        Assert.EndsWith("out_part02.asc", TraceEditPlan.SegmentPath(Path.Combine("d", "out.asc"), 1, 2));
        Assert.Equal("out.asc", TraceEditPlan.SegmentPath("out.asc", 0, 1));
        Assert.Throws<ArgumentException>(() =>
            TraceEditPlan.Apply(Frames(), new TraceEditRequest(TraceEditKind.SplitAtGaps, GapSeconds: 0)));
    }

    [Fact]
    public void RemapChangesOnlyTheSourceChannelUsingOneBasedNumbers()
    {
        var src = Frames();
        var o = TraceEditPlan.Apply(src, new TraceEditRequest(TraceEditKind.RemapChannel, FromChannel: 1, ToChannel: 3));
        Assert.All(o.Segments[0], f => Assert.Equal(2, f.Channel));
        Assert.All(src, f => Assert.Equal(0, f.Channel));
    }

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData(" 2,25 ", 2.25)]
    public void NumbersAcceptDotOrComma(string text, double expected)
    {
        Assert.True(TraceEditPlan.TryParseNumber(text, out var v));
        Assert.Equal(expected, v);
        Assert.False(TraceEditPlan.TryParseNumber("abc", out _));
        Assert.False(TraceEditPlan.TryParseNumber("NaN", out _));
    }
}
