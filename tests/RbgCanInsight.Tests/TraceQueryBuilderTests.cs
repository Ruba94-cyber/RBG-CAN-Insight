using RbgCanInsight.Analysis;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class TraceQueryBuilderTests
{
    [Fact]
    public void ComposesEveryFieldIntoTheTraceSearchGrammar()
    {
        var result = TraceQueryBuilder.Compose(
        [
            new(QueryField.Channel, "2"),
            new(QueryField.CanId, "0x1ab"),
            new(QueryField.MessageName, "Engine"),
            new(QueryField.PayloadContains, "aa bb"),
            new(QueryField.UdsText, "vin"),
            new(QueryField.FrameKind, "CanFd")
        ]);
        Assert.True(result.IsValid);
        Assert.Equal("ch2 1AB Engine payload:AABB uds:vin fd", result.Query);
    }

    [Fact]
    public void ExcludedConditionsBecomeStopTerms()
    {
        var result = TraceQueryBuilder.Compose(
        [
            new(QueryField.Channel, "2", Exclude: true),
            new(QueryField.PayloadContains, "AA", Exclude: true),
            new(QueryField.UdsAny, "", Exclude: true)
        ]);
        Assert.Equal("-ch2 -payload:AA -uds", result.Query);
        Assert.Contains(result.Warnings, w => w.Contains("Only exclusions"));
    }

    [Theory]
    [InlineData(QueryField.CanId, "ZZ")]
    [InlineData(QueryField.CanId, "123456789")]
    [InlineData(QueryField.CanId, "")]
    [InlineData(QueryField.Channel, "0")]
    [InlineData(QueryField.Channel, "x")]
    [InlineData(QueryField.PayloadContains, "GG")]
    [InlineData(QueryField.PayloadContains, "")]
    [InlineData(QueryField.MessageName, "two words")]
    [InlineData(QueryField.MessageName, "ABC")]      // would also be read as a hex ID
    [InlineData(QueryField.MessageName, "fd")]       // reserved keyword
    [InlineData(QueryField.MessageName, "ch3")]      // reserved keyword
    [InlineData(QueryField.UdsText, "a b")]
    [InlineData(QueryField.FrameKind, "Nope")]
    public void InvalidValuesAreRejectedInsteadOfChangingMeaning(QueryField field, string value)
    {
        var result = TraceQueryBuilder.Compose([new QueryCondition(field, value)]);
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(string.Empty, result.Query);
    }

    [Fact]
    public void OddPayloadDigitsAreAllowedWithAWarning()
    {
        var result = TraceQueryBuilder.Compose([new(QueryField.PayloadContains, "ABC")]);
        Assert.True(result.IsValid);
        Assert.Equal("payload:ABC", result.Query);
        Assert.Single(result.Warnings);
    }

    [Theory]
    [InlineData("ch1 100 EngineData payload:AABB uds uds:vin fd")]
    [InlineData("-ch2 -payload:AA -uds -EngineTemp")]
    [InlineData("error remote extended standard unmatched")]
    public void ParsingThenComposingRoundTripsTheQuery(string query)
    {
        var conditions = TraceQueryBuilder.Parse(query, out var unrecognized);
        Assert.Empty(unrecognized);
        var composed = TraceQueryBuilder.Compose(conditions);
        Assert.True(composed.IsValid, string.Join("; ", composed.Errors));
        Assert.Equal(query, composed.Query);
    }

    [Fact]
    public void ParseAcceptsAliasesAndNormalizesThem()
    {
        var conditions = TraceQueryBuilder.Parse("errors rtr 0x7e0", out _);
        Assert.Equal("error remote 7E0", TraceQueryBuilder.Compose(conditions).Query);
    }

    [Fact]
    public void DescribeReadsAsPlainEnglish()
    {
        var text = TraceQueryBuilder.Describe(
        [
            new(QueryField.Channel, "1"),
            new(QueryField.CanId, "100"),
            new(QueryField.PayloadContains, "aa", Exclude: true)
        ]);
        Assert.Equal("Show frames where channel is 1 AND CAN ID is 100 AND NOT payload contains AA", text);
        Assert.Equal("Every frame (no conditions)", TraceQueryBuilder.Describe([]));
    }

    [Fact]
    public void BuiltQueryMatchesWhatTheFilterGrammarExpectsForFrameKinds()
    {
        foreach (var name in TraceQueryBuilder.FrameKindNames)
        {
            var kind = Enum.Parse<QueryFrameKind>(name);
            var composed = TraceQueryBuilder.Compose([new QueryCondition(QueryField.FrameKind, name)]);
            Assert.Equal(TraceQueryBuilder.FrameKindTerm(kind), composed.Query);
        }
    }
}
