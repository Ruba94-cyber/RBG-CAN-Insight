using RbgCanInsight.Analysis;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class TraceFilterFeedbackTests
{
    [Fact]
    public void UnfilteredShowsPlainCount() =>
        Assert.Equal("8 frames", TraceFilterFeedback.Format(8, 8, filterApplied: false, editPending: false));

    [Fact]
    public void NoMatchesIsAnExplicitNoResultsState() =>
        Assert.Equal("0 of 8 frames match the filter (Esc clears it)",
            TraceFilterFeedback.Format(0, 8, filterApplied: true, editPending: false));

    [Fact]
    public void PartialMatchKeepsTheFilteredWording() =>
        Assert.Equal("3 of 8 frames (filtered)", TraceFilterFeedback.Format(3, 8, true, false));

    [Fact]
    public void TypedButUnappliedTextAsksForEnter() =>
        Assert.Equal("8 frames · press Enter to apply the search", TraceFilterFeedback.Format(8, 8, false, true));

    [Fact]
    public void FilterThatKeepsEveryFrameSaysSo() =>
        Assert.Equal("8 frames (all match the filter)", TraceFilterFeedback.Format(8, 8, true, false));

    [Fact]
    public void EmptyTraceWithPendingTextStillShowsAHint() =>
        Assert.Equal("0 frames · press Enter to apply the search", TraceFilterFeedback.Format(0, 0, false, true));
}
