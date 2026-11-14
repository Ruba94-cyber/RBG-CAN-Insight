using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Guardrails for engineering-language and source-context claims
/// in the CAN relationship map. A numeric value alone isn't a proven
/// influence and fields from the same CAN ID don't make independent inputs.</summary>
public sealed class SignalRelationshipReviewTests
{
    private static (SignalTimeline A, SignalTimeline B, SignalTimeline C) Source()
    {
        var dbc = new DbcParser().Parse("review", new StringReader(
            """
            BO_ 256 SourceMessage: 8 ECM
             SG_ TorqueRequest : 0|8@1+ (1,0) [0|255] "Nm" ECM
             SG_ TorqueActual : 8|8@1+ (1,0) [0|255] "Nm" ECM
            BO_ 257 OtherMessage: 8 ECM
             SG_ EngineSpeed : 0|8@1+ (1,0) [0|255] "rpm" ECM
            """));
        var frames = new List<CanFrame>();
        for (var i = 0; i < 20; i++)
        {
            var t = 1_000_000_000L + i * 100_000_000L;
            frames.Add(new CanFrame(0, 256,
                [(byte)(10 + i), (byte)(20 + i), 0, 0, 0, 0, 0, 0], t));
            frames.Add(new CanFrame(0, 257,
                [(byte)(50 + i), 0, 0, 0, 0, 0, 0, 0], t + 10_000_000L));
        }
        var curves = SignalTimelineBuilder.Build(frames, [dbc],
            sourceAt: _ => 0, sourcePaths: ["recorded.asc"]);
        SignalTimeline Get(string name) =>
            Assert.Single(curves.Where(item => item.Signal.Name == name));
        return (Get("TorqueRequest"), Get("TorqueActual"), Get("EngineSpeed"));
    }

    private static SignalRelationship Evidence(SignalTimeline a, SignalTimeline b,
        double level, double? changes) => new(
        a, b, 0, level, changes, 20, 15, 14, 0, 1,
        1_000_000_000L, 1_010_000_000L);

    [Fact]
    public void WeakValueChangesAreNotIncorrectlyCalledStrongCoVariation()
    {
        var (a, _, c) = Source();
        var weak = Evidence(a, c, 0.97, 0.1);
        Assert.False(weak.HasStrongChangeAgreement);
        Assert.Contains("change evidence weak", weak.AssociationType);
        Assert.Contains("drive-cycle trend", weak.SuggestedNextCheck);
        Assert.Contains("not a dependency", weak.EvidenceText);

        var strong = Evidence(a, c, -0.91, -0.84);
        Assert.True(strong.HasStrongChangeAgreement);
        Assert.Equal("Values and changes co-vary", strong.AssociationType);
        Assert.Contains("NOT prove influence", strong.EvidenceText);

        var notComputed = Evidence(a, c, 0.91, null);
        Assert.False(notComputed.HasStrongChangeAgreement);
        Assert.Contains("changes unavailable", notComputed.AssociationType);
    }

    [Fact]
    public void SameCanPayloadIsLabeledAsSharedSamplingNotIndependentEngineeringInfluence()
    {
        var (a, b, c) = Source();
        var sameMessage = new SignalMapEdge(Evidence(a, b, 0.93, 0.9));
        var crossMessage = new SignalMapEdge(Evidence(a, c, 0.93, 0.9));

        Assert.True(sameMessage.SameRecordedMessage);
        Assert.False(crossMessage.SameRecordedMessage);
        Assert.Contains("shared sampling", sameMessage.InvestigationHint);
        Assert.Contains("SAME CAN message", sameMessage.EvidenceText);
        Assert.Contains("Different CAN messages", crossMessage.InvestigationHint);
    }

    [Fact]
    public void SuggestedGroupStartingPointPrefersCrossMessageEvidenceOverBusPayloadDensity()
    {
        var (a, b, c) = Source();
        var same = new SignalMapEdge(Evidence(a, b, 0.93, 0.9));
        var cross = new SignalMapEdge(Evidence(b, c, 0.94, 0.86));
        var group = new SignalMapGroup(1, [a, b, c], [same, cross]);
        Assert.Same(b, group.SuggestedStartingSignal);
        Assert.Contains("not proven influence", group.StartingPointText);
        Assert.Contains("not all pairs", group.Warning);
    }
}
