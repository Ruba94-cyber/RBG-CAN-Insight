using RbgCanInsight.Correlation;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class CorrelationEvidencePolicyTests
{
    [Theory]
    [InlineData(0.99, false, 0.50)]
    [InlineData(0.80, false, 0.50)]
    [InlineData(0.42, false, 0.42)]
    [InlineData(0.99, true, 0.99)]
    [InlineData(0.42, true, 0.42)]
    [InlineData(-0.10, false, 0)]
    [InlineData(2.0, false, 0.50)]
    [InlineData(2.0, true, 1.0)]
    public void AutomaticEvidenceNeverPromotesToReviewedIdentity(double raw, bool reviewed, double expected)
    {
        Assert.Equal(expected,
            CorrelationEvidencePolicy.AssociationIndicator(raw, reviewed), precision: 8);
    }

    [Fact]
    public void NonFiniteScoresCannotRenderAsStrongAssociations()
    {
        Assert.Equal(0, CorrelationEvidencePolicy.AssociationIndicator(double.NaN, false));
        Assert.Equal(0, CorrelationEvidencePolicy.AssociationIndicator(double.PositiveInfinity, true));
        Assert.Equal(0, CorrelationEvidencePolicy.AssociationIndicator(double.NegativeInfinity, false));
    }
}
