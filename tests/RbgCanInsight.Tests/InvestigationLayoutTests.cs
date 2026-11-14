using RbgCanInsight.Analysis;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class InvestigationLayoutTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "rbg-layouts-" + Guid.NewGuid().ToString("N"), "layouts.json");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_path)!, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(InvestigationArrangement.Single, 1)]
    [InlineData(InvestigationArrangement.SideBySide, 2)]
    [InlineData(InvestigationArrangement.Stacked, 2)]
    [InlineData(InvestigationArrangement.Quad, 4)]
    public void ArrangementsShowTheExpectedNumberOfPanes(InvestigationArrangement arrangement, int count) =>
        Assert.Equal(count, InvestigationLayouts.VisiblePaneCount(arrangement));

    [Fact]
    public void NormalizePadsMissingSlotsAndReplacesUnknownValues()
    {
        var layout = InvestigationLayouts.Normalize(new InvestigationLayout(
            "  Mine  ", InvestigationArrangement.Quad, [InvestigationPane.Trace, (InvestigationPane)99]));
        Assert.Equal("Mine", layout.Name);
        Assert.Equal(4, layout.Panes.Count);
        Assert.Equal(InvestigationPane.Trace, layout.Panes[0]);
        Assert.All(layout.Panes.Skip(1), pane => Assert.Equal(InvestigationPane.Empty, pane));
    }

    [Fact]
    public void BuiltInLayoutsAreComplete()
    {
        Assert.True(InvestigationLayouts.BuiltIn.Count >= 3);
        Assert.All(InvestigationLayouts.BuiltIn, layout =>
        {
            Assert.Equal(4, layout.Panes.Count);
            Assert.All(layout.Panes.Take(InvestigationLayouts.VisiblePaneCount(layout.Arrangement)),
                pane => Assert.NotEqual(InvestigationPane.Empty, pane));
        });
        Assert.Contains(InvestigationLayouts.BuiltIn, layout => layout.Arrangement == InvestigationArrangement.Quad &&
            layout.Panes.Distinct().Count() == 4);
    }

    [Fact]
    public void SavedLayoutsRoundTripAndReplaceByNameIgnoringCase()
    {
        var store = new InvestigationLayoutStore(_path);
        Assert.Empty(store.Load());
        store.Save(new InvestigationLayout("DTC review", InvestigationArrangement.SideBySide,
            [InvestigationPane.Findings, InvestigationPane.Trace]));
        store.Save(new InvestigationLayout("dtc REVIEW", InvestigationArrangement.Stacked,
            [InvestigationPane.UdsTransactions, InvestigationPane.DecodedSignals]));
        var loaded = Assert.Single(new InvestigationLayoutStore(_path).Load());
        Assert.Equal("dtc REVIEW", loaded.Name);
        Assert.Equal(InvestigationArrangement.Stacked, loaded.Arrangement);
        Assert.Equal(InvestigationPane.UdsTransactions, loaded.Panes[0]);
        Assert.Equal(4, loaded.Panes.Count);
    }

    [Fact]
    public void BuiltInNamesAreReservedAndDeleteRemovesOnlyTheNamedLayout()
    {
        var store = new InvestigationLayoutStore(_path);
        Assert.Throws<InvalidOperationException>(() => store.Save(
            new InvestigationLayout("full investigation", InvestigationArrangement.Single, [InvestigationPane.Trace])));
        Assert.Throws<ArgumentException>(() => store.Save(
            new InvestigationLayout("  ", InvestigationArrangement.Single, [InvestigationPane.Trace])));
        store.Save(new InvestigationLayout("A", InvestigationArrangement.Single, [InvestigationPane.Trace]));
        store.Save(new InvestigationLayout("B", InvestigationArrangement.Single, [InvestigationPane.Findings]));
        Assert.Equal(["B"], store.Delete("a").Select(item => item.Name));
    }

    [Fact]
    public void CorruptFileLoadsAsEmpty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ not json");
        Assert.Empty(new InvestigationLayoutStore(_path).Load());
    }
}
