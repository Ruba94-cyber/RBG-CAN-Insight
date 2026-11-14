using RbgCanInsight.Dbc;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DbcMergeTests
{
    private const string Base = """
        VERSION "base"

        NS_ :
        	NS_DESC_
        	CM_

        BS_:

        BU_: ECM TCM

        BO_ 256 EngineData: 8 ECM
         SG_ EngSpeed : 0|16@1+ (0.25,0) [0|16383] "rpm" TCM
         SG_ Coolant : 16|8@1+ (1,-40) [-40|215] "degC" TCM

        BO_ 512 Gear: 4 TCM
         SG_ GearPos : 0|4@1+ (1,0) [0|15] "" ECM

        BO_ 1024 BaseOnly: 2 ECM
         SG_ Legacy : 0|8@1+ (1,0) [0|255] "" TCM

        CM_ SG_ 256 EngSpeed "Engine speed";
        CM_ BO_ 1024 "only in the base
        file, multi-line";
        VAL_ 512 GearPos 0 "P" 1 "R" ;
        BA_DEF_ BO_ "GenMsgCycleTime" INT 0 65535;
        BA_ "GenMsgCycleTime" BO_ 256 10;
        """;

    private const string Incoming = """
        VERSION "incoming"

        NS_ :
        	NS_DESC_
        	CM_

        BS_:

        BU_: ECM TCM BCM

        BO_ 256 EngineData: 8 ECM
         SG_ EngSpeed : 0|16@1+ (0.25,0) [0|16383] "rpm" TCM
         SG_ Coolant : 16|8@1+ (0.5,-40) [-40|87] "degK" TCM
         SG_ EngTorque : 32|16@1+ (0.1,0) [0|6553] "Nm" BCM

        BO_ 512 Gear: 6 TCM
         SG_ GearPos : 0|4@1+ (1,0) [0|15] "" ECM

        BO_ 2147484672 BrakeX: 2 BCM
         SG_ BrakePres : 0|12@1+ (0.1,0) [0|400] "bar" ECM

        CM_ SG_ 256 EngTorque "Torque";
        CM_ SG_ 256 Coolant "New coolant text";
        VAL_ 256 EngTorque 0 "Zero" ;
        VAL_ 2147484672 BrakePres 4095 "Invalid" ;
        BA_ "GenMsgCycleTime" BO_ 512 20;
        """;

    [Fact]
    public void PlanListsAdditionsAndConflictsAndNeverProposesDeletion()
    {
        var plan = DbcMerge.Plan(Base, Incoming);
        Assert.Contains(plan.Items, i => i.Kind == DbcMergeKind.AddSignal && i.Signal == "EngTorque" &&
            i.DefaultDecision == DbcMergeDecision.TakeIncoming);
        var addMessage = Assert.Single(plan.Items, i => i.Kind == DbcMergeKind.AddMessage);
        Assert.True(addMessage.Identity.IsExtended);
        Assert.Equal(0x400u + 0u, 0x400u);                    // sanity
        Assert.Contains(plan.Items, i => i.Kind == DbcMergeKind.ChangeSignal && i.Signal == "Coolant" &&
            i.DefaultDecision == DbcMergeDecision.KeepBase);
        Assert.Contains(plan.Items, i => i.Kind == DbcMergeKind.ChangeMessage && i.MessageName == "Gear" &&
            i.DefaultDecision == DbcMergeDecision.KeepBase);
        Assert.Equal(2, plan.KeptBaseOnly);                   // BaseOnly message + its signal
        Assert.DoesNotContain(plan.Items, i => i.MessageName == "BaseOnly");
        // Signals of an added message are covered by the message item, not listed separately.
        Assert.DoesNotContain(plan.Items, i => i.Kind == DbcMergeKind.AddSignal && i.Signal == "BrakePres");
    }

    [Fact]
    public void DefaultMergeAddsNewDefinitionsKeepsBaseDecisionsAndPreservesEverythingElse()
    {
        var plan = DbcMerge.Plan(Base, Incoming);
        var result = DbcMerge.Apply(Base, Incoming, plan);

        Assert.True(result.IsValid, string.Join(" | ", result.Issues));
        var merged = new DbcParser().Parse("m", new StringReader(result.Text));
        Assert.True(merged.TryGetMessage(256, false, out var engine));
        Assert.Contains(engine.Signals, s => s.Name == "EngTorque" && s.Unit == "Nm");
        Assert.Equal(1, engine.Signals.Single(s => s.Name == "Coolant").Factor);   // conflict kept as base
        Assert.True(merged.TryGetMessage(512, false, out var gear));
        Assert.Equal(4, gear.Length);                                              // header kept as base
        Assert.True(merged.TryGetMessage(0x400, true, out var brake));             // extended identity
        Assert.Equal("BrakeX", brake.Name);
        Assert.True(merged.TryGetMessage(1024, false, out _));                     // base-only is never deleted
        Assert.Contains("BU_: ECM TCM BCM", result.Text);                          // new node added
        Assert.Contains("CM_ SG_ 256 EngTorque \"Torque\";", result.Text);
        Assert.Contains("VAL_ 2147484672 BrakePres 4095 \"Invalid\" ;", result.Text);
        Assert.Contains("only in the base\nfile, multi-line", result.Text.Replace("\r\n", "\n"));
        Assert.Contains("\tNS_DESC_", result.Text);                                // untouched block
        Assert.Contains("BA_ \"GenMsgCycleTime\" BO_ 256 10;", result.Text);
        Assert.Contains("CM_ SG_ 256 EngSpeed \"Engine speed\";", result.Text);   // base comment kept by default
        Assert.DoesNotContain("incoming", result.Text);                            // VERSION from base
    }

    [Fact]
    public void ExplicitlyTakingConflictsReplacesOnlyThoseDefinitions()
    {
        var plan = DbcMerge.Plan(Base, Incoming);
        var decisions = plan.Items.ToDictionary(i => i.Id, _ => DbcMergeDecision.TakeIncoming);
        var result = DbcMerge.Apply(Base, Incoming, plan, decisions);

        Assert.True(result.IsValid, string.Join(" | ", result.Issues));
        var merged = new DbcParser().Parse("m", new StringReader(result.Text));
        merged.TryGetMessage(256, false, out var engine);
        var coolant = engine.Signals.Single(s => s.Name == "Coolant");
        Assert.Equal(0.5, coolant.Factor);
        Assert.Equal("degK", coolant.Unit);
        Assert.Contains("CM_ SG_ 256 Coolant \"New coolant text\";", result.Text);
        merged.TryGetMessage(512, false, out var gear);
        Assert.Equal(6, gear.Length);
        Assert.Contains("BA_ \"GenMsgCycleTime\" BO_ 512 20;", result.Text);
        Assert.Equal(1, engine.Signals.Count(s => s.Name == "EngSpeed"));          // unchanged signal not duplicated
        // The incoming version dropped the EngSpeed comment: taking that change is explicit and
        // removes it, whereas the default merge keeps it (asserted in the default-merge test).
        Assert.Contains(plan.Items, i => i.Kind == DbcMergeKind.ChangeSignal && i.Signal == "EngSpeed");
        Assert.DoesNotContain("CM_ SG_ 256 EngSpeed", result.Text);
    }

    [Fact]
    public void KeepBaseForEverythingReproducesTheBaseExactly()
    {
        var plan = DbcMerge.Plan(Base, Incoming);
        var decisions = plan.Items.ToDictionary(i => i.Id, _ => DbcMergeDecision.KeepBase);
        var result = DbcMerge.Apply(Base, Incoming, plan, decisions);
        Assert.True(result.IsValid);
        Assert.Empty(result.Applied);
        Assert.Equal(Base.Replace("\r\n", "\n"), result.Text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void IdenticalFilesProduceAnEmptyPlan()
    {
        var plan = DbcMerge.Plan(Base, Base);
        Assert.Empty(plan.Items);
        Assert.Empty(plan.NotMergeable);
        Assert.Equal(0, plan.KeptBaseOnly);
    }

    [Fact]
    public void MergeIntoCrLfBaseKeepsCrLfLineEndings()
    {
        var crlfBase = Base.Replace("\n", "\r\n");
        var plan = DbcMerge.Plan(crlfBase, Incoming);
        var result = DbcMerge.Apply(crlfBase, Incoming, plan);
        Assert.True(result.IsValid, string.Join(" | ", result.Issues));
        Assert.DoesNotContain("\n", result.Text.Replace("\r\n", string.Empty));
    }

    [Fact]
    public void BaseWithoutNodeDeclarationsStillMergesWithoutInventingBuLines()
    {
        var noNodesBase = "VERSION \"\"\n\nBO_ 256 A: 8 ECM\n SG_ S1 : 0|8@1+ (1,0) [0|255] \"\" Vector__XXX\n";
        var noNodesIncoming = noNodesBase + "\nBO_ 512 B: 8 BCM\n SG_ S2 : 0|8@1+ (1,0) [0|255] \"\" Vector__XXX\n";
        var result = DbcMerge.Apply(noNodesBase, noNodesIncoming, DbcMerge.Plan(noNodesBase, noNodesIncoming));
        Assert.True(result.IsValid, string.Join(" | ", result.Issues));
        Assert.DoesNotContain("BU_", result.Text);
        Assert.Contains("BO_ 512 B: 8 BCM", result.Text);
    }
}