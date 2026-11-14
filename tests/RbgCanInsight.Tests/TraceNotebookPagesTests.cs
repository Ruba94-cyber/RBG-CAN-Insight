using System.Text.Json;
using RbgCanInsight.Analysis;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class TraceNotebookPagesTests
{

    [Fact]
    public void DiagnosticNotebookRecipeRoundTripsAndRebindsOnlyExactOriginalLogger()
    {
        const string oldFile = @"C:\captures\logger-A.blf";
        const string moved = @"D:\study\logger-A.blf";
        var recipe = new TraceNotebookDiagnosticRecipe("P0123", 3,
            "0xF405 Temp", true, oldFile);
        var page = TraceNotebookPages.New("DTC / DID review", "uds", 0, 0)
            with { DiagnosticRecipe = recipe };
        var json = JsonSerializer.Serialize(page);
        var restored = TraceNotebookPages.Normalize(
            JsonSerializer.Deserialize<TraceNotebookPage>(json)!);
        Assert.Equal(recipe, restored.DiagnosticRecipe);
        Assert.DoesNotContain("CanFrame", json);
        Assert.DoesNotContain("DtcStatusTransition", json);

        var rebound = Assert.Single(TraceNotebookPages.RebindSources([restored],
            new Dictionary<string, string> { [oldFile] = moved }));
        Assert.Equal(moved, rebound.DiagnosticRecipe!.OriginalSourcePath);
        var unresolved = Assert.Single(TraceNotebookPages.RebindSources([restored],
            new Dictionary<string, string>()));
        Assert.Equal(oldFile, unresolved.DiagnosticRecipe!.OriginalSourcePath);
        Assert.Equal(-1, TraceNotebookPages.FindSource(
            unresolved.DiagnosticRecipe.OriginalSourcePath, [moved]));
    }

    [Fact]
    public void UntrustedDiagnosticNotebookFiltersAreBoundedWithoutLosingLegacyTabs()
    {
        var page = TraceNotebookPages.New("Diagnostics", "", 0, 0) with
        {
            DiagnosticRecipe = new TraceNotebookDiagnosticRecipe(
                new string('X', 5000), 120, new string('Y', 3000), true,
                "source-1.asc")
        };
        var actual = TraceNotebookPages.Normalize(page);
        Assert.Equal(TraceNotebookPages.MaxDiagnosticSearchChars,
            actual.DiagnosticRecipe!.DtcSearchText.Length);
        Assert.Equal(TraceNotebookPages.MaxDiagnosticSearchChars,
            actual.DiagnosticRecipe.MappingSearchText.Length);
        Assert.Equal(3, actual.DiagnosticRecipe.DtcStateFilterIndex);
        var legacy = TraceNotebookPages.New("Legacy", "id:100", 0, 0);
        Assert.Null(TraceNotebookPages.Normalize(legacy).DiagnosticRecipe);
    }

    [Fact]
    public void NotebookRecipesRoundTripThroughJsonWithoutCopyingFrames()
    {
        var source = Path.Combine("C:", "captures", "logger-a.blf");
        var pages = new[]
        {
            TraceNotebookPages.New("UDS errors", "uds -ch2", 0, 1, source, "Verify ISO-TP endpoints"),
            TraceNotebookPages.New("Latest engine", "id:100..1FF", 1, 3)
        };
        var json = JsonSerializer.Serialize(pages);
        var parsed = TraceNotebookPages.NormalizeMany(
            JsonSerializer.Deserialize<TraceNotebookPage[]>(json));
        Assert.Equal(2, parsed.Count);
        Assert.Equal("UDS errors", parsed[0].Title);
        Assert.Equal("uds -ch2", parsed[0].Query);
        Assert.Equal(source, parsed[0].SourcePath);
        Assert.Equal("Verify ISO-TP endpoints", parsed[0].Notes);
        Assert.Equal(1, parsed[1].ViewModeIndex);
        Assert.Null(parsed[1].SourcePath);
        Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UntrustedPagesAreBoundedClampedAndDeduplicated()
    {
        var first = new TraceNotebookPage("repeated", new string('T', 150),
            new string('F', 5000), -100, 900, "trace.blf", new string('N', 10000));
        var many = Enumerable.Repeat(first, 50).Append(
            new TraceNotebookPage("different", "", "", 10, -5));
        var actual = TraceNotebookPages.NormalizeMany(many);
        Assert.Equal(2, actual.Count);
        Assert.Equal(TraceNotebookPages.MaxTitleChars, actual[0].Title.Length);
        Assert.Equal(TraceNotebookPages.MaxQueryChars, actual[0].Query.Length);
        Assert.Equal(TraceNotebookPages.MaxNotesChars, actual[0].Notes.Length);
        Assert.Equal(0, actual[0].ViewModeIndex);
        Assert.Equal(3, actual[0].TimeDisplayModeIndex);
        Assert.Equal("Untitled analysis", actual[1].Title);
        Assert.Equal(2, actual[1].ViewModeIndex);
        Assert.Equal(0, actual[1].TimeDisplayModeIndex);
        Assert.Equal(TraceNotebookPages.MaxPages,
            TraceNotebookPages.NormalizeMany(Enumerable.Range(0, 100).Select(i =>
                TraceNotebookPages.New($"Tab {i}", "id:123", 0, 0))).Count);
    }

    [Fact]
    public void FileScopeRequiresAnExactSourcePathNotASameNamedDifferentFile()
    {
        var paths = new[] { @"C:\logger1\run.blf", @"D:\logger2\run.blf" };
        Assert.Equal(1, TraceNotebookPages.FindSource(@"D:\logger2\run.blf", paths));
        Assert.Equal(0, TraceNotebookPages.FindSource(@"c:\LOGGER1\RUN.BLF", paths));
        Assert.Equal(-1, TraceNotebookPages.FindSource(@"E:\logger3\run.blf", paths));
        Assert.Equal(-1, TraceNotebookPages.FindSource(null, paths));
    }

    [Fact]
    public void RelocationUsesExplicitResolvedMappingOnly()
    {
        const string oldFile = @"C:\old\raw.blf";
        const string moved = @"D:\study\raw.blf";
        const string absent = @"C:\old\missing.blf";
        var pages = new[]
        {
            TraceNotebookPages.New("Logger A", "ch1", 0, 0, oldFile),
            TraceNotebookPages.New("Logger B", "ch2", 0, 0, absent),
            TraceNotebookPages.New("Combined", "uds", 0, 0)
        };
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [oldFile] = moved
        };
        var rebound = TraceNotebookPages.RebindSources(pages, map);
        Assert.Equal(moved, rebound[0].SourcePath);
        Assert.Equal(absent, rebound[1].SourcePath);
        Assert.Null(rebound[2].SourcePath);
        Assert.NotEqual(pages[0].SourcePath, rebound[0].SourcePath);
    }

    [Fact]
    public void AggregateCellRecipesRoundTripAsBoundedMetadataNotCachedResults()
    {
        var cells = new[]
        {
            TraceNotebookPages.NewCell(TraceAggregateKind.ByIdentifier, "CAN traffic"),
            TraceNotebookPages.NewCell(TraceAggregateKind.BySource, "Recordings")
        };
        var page = TraceNotebookPages.New("Logger A", "ch1", 0, 1, "logger-a.asc")
            with { Cells = cells };
        var json = JsonSerializer.Serialize(page);
        var parsed = TraceNotebookPages.Normalize(
            JsonSerializer.Deserialize<TraceNotebookPage>(json)!);
        Assert.Equal(2, parsed.Cells!.Count);
        Assert.Equal(TraceAggregateKind.ByIdentifier, parsed.Cells[0].Kind);
        Assert.Equal(TraceAggregateKind.BySource, parsed.Cells[1].Kind);
        Assert.Equal("logger-a.asc", parsed.SourcePath);
        Assert.DoesNotContain("FrameCountText", json);
        Assert.DoesNotContain("FirstFrameIndex", json);
        Assert.DoesNotContain("LastFrameIndex", json);
    }


    [Fact]
    public void TrafficBurstCellsPersistAsRecipesAndRemainBackwardCompatible()
    {
        var cell = TraceNotebookPages.NewCell(TraceAggregateKind.BySecond);
        Assert.Contains("second", cell.Title, StringComparison.OrdinalIgnoreCase);
        var page = TraceNotebookPages.New("Peak traffic", "", 0, 0, "logger.asc")
            with { Cells = new[] { cell } };
        var restored = TraceNotebookPages.Normalize(
            JsonSerializer.Deserialize<TraceNotebookPage>(JsonSerializer.Serialize(page))!);
        Assert.Equal(TraceAggregateKind.BySecond, Assert.Single(restored.Cells!).Kind);
        Assert.Equal("logger.asc", restored.SourcePath);
        Assert.DoesNotContain("FirstFrameIndex", JsonSerializer.Serialize(restored));
        Assert.Equal(TraceAggregateKind.BySource,
            TraceNotebookPages.NewCell(TraceAggregateKind.BySource).Kind);
    }

    [Fact]
    public void MalformedAndExcessiveCellsAreNormalizedWithoutChangingSourceRecipe()
    {
        var baseCell = TraceNotebookPages.NewCell(TraceAggregateKind.BySource, "Known");
        var invalid = new TraceNotebookCell("bad", "Invalid kind", (TraceAggregateKind)99);
        var repeated = Enumerable.Repeat(baseCell, 4);
        var unique = Enumerable.Range(0, 30)
            .Select(index => TraceNotebookPages.NewCell(
                TraceAggregateKind.ByIdentifier, new string('T', 140) + index));
        var page = TraceNotebookPages.New("Data", "id:100", 0, 0, "original.asc") with
        {
            Cells = repeated.Concat(new[] { invalid }).Concat(unique).ToArray()
        };
        var normalized = TraceNotebookPages.Normalize(page);
        Assert.Equal(TraceNotebookPages.MaxCellsPerPage, normalized.Cells!.Count);
        Assert.Equal(normalized.Cells.Count, normalized.Cells.Select(c => c.Id).Distinct().Count());
        Assert.All(normalized.Cells, cell =>
        {
            Assert.InRange(cell.Title.Length, 1, TraceNotebookPages.MaxCellTitleChars);
            Assert.True(Enum.IsDefined(cell.Kind));
        });
        Assert.Equal("original.asc", normalized.SourcePath);
        Assert.Equal("id:100", normalized.Query);
    }

    [Fact]
    public void SourceRelocationRetainsEveryNestedCellRecipe()
    {
        const string old = @"C:\logs\source.asc";
        const string moved = @"D:\tests\source.asc";
        var page = TraceNotebookPages.New("A", "ch2", 2, 1, old)
            with { Cells = new[] { TraceNotebookPages.NewCell(TraceAggregateKind.BySource) } };
        var result = TraceNotebookPages.RebindSources(new[] { page },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [old] = moved });
        var relocated = Assert.Single(result);
        Assert.Equal(moved, relocated.SourcePath);
        Assert.Equal(TraceAggregateKind.BySource, Assert.Single(relocated.Cells!).Kind);
    }
}
