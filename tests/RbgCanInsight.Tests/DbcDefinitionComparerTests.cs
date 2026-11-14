using RbgCanInsight.Dbc;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DbcDefinitionComparerTests
{
    private static DbcDatabase Db(string text) =>
        new DbcParser().Parse("network", new StringReader(text));

    private const string Basic = """
        VERSION ""
        BO_ 256 Engine: 8 ECU
         SG_ Speed : 0|16@1+ (0.01,0) [0|250] "km/h" Dash
         SG_ State : 16|8@1+ (1,0) [0|7] "" Dash
        VAL_ 256 State 0 "Off" 1 "On" ;
        """;

    [Fact]
    public void ReorderedSourceStatementsDoNotProduceFalseSignalChanges()
    {
        var before = Db(Basic);
        var after = Db("""
            VERSION ""
            BO_ 256 Engine: 8 ECU
             SG_ State : 16|8@1+ (1,0) [0|7] "" Dash
             SG_ Speed : 0|16@1+ (0.01,0) [0|250] "km/h" Dash
            VAL_ 256 State 1 "On" 0 "Off" ;
            """);
        var report = DbcDefinitionComparer.Compare(before, after);
        Assert.Equal(3, report.Rows.Count);
        Assert.Equal(3, report.Unchanged);
        Assert.Equal(0, report.RequiresReview);
    }

    [Fact]
    public void BitOrderAndScalingChangesAreClassifiedAsDecodingImpact()
    {
        var original = Db(Basic);
        var changed = Db("""
            BO_ 256 Engine: 8 ECU
             SG_ Speed : 8|16@0+ (0.02,0) [0|250] "km/h" Dash
             SG_ State : 16|8@1+ (1,0) [0|7] "" Dash
            VAL_ 256 State 0 "Off" 1 "On" ;
            """);
        var row = Assert.Single(DbcDefinitionComparer.Compare(original, changed).Rows
            .Where(item => item.Scope == "Signal" && item.Signal == "Speed"));
        Assert.Equal(DbcDefinitionChange.Changed, row.Change);
        Assert.Equal(DbcDefinitionImpact.Decoding, row.Impact);
        Assert.Contains("Start bit", row.Differences);
        Assert.Contains("Byte order", row.Differences);
        Assert.Contains("Factor", row.Differences);
        Assert.True(row.RequiresReview);
    }

    [Fact]
    public void DifferentRawValueMeaningsAreNotSilentlyIgnored()
    {
        var changed = Db(Basic.Replace("1 \"On\"", "1 \"Fault\""));
        var row = Assert.Single(DbcDefinitionComparer.Compare(Db(Basic), changed).Rows
            .Where(item => item.Scope == "Signal" && item.Signal == "State"));
        Assert.Equal(DbcDefinitionImpact.Decoding, row.Impact);
        Assert.Contains("Value meanings", row.Differences);
        Assert.Contains("Fault", row.CurrentDefinition);
    }

    [Fact]
    public void MessageRenameOrCycleChangeDoesNotPretendThePayloadChanged()
    {
        var after = Db(Basic.Replace("Engine: 8", "EngineUpdated: 8"));
        var rows = DbcDefinitionComparer.Compare(Db(Basic), after).Rows;
        var message = Assert.Single(rows.Where(r => r.Scope == "Message"));
        Assert.Equal(DbcDefinitionChange.Changed, message.Change);
        Assert.Equal(DbcDefinitionImpact.Metadata, message.Impact);
        Assert.Contains("Message name", message.Differences);
        Assert.All(rows.Where(r => r.Scope == "Signal"),
            signal => Assert.Equal(DbcDefinitionChange.Unchanged, signal.Change));
    }

    [Fact]
    public void AddedAndRemovedSignalsAreReportedWithoutMisidentification()
    {
        var current = Db("""
            BO_ 256 Engine: 8 ECU
             SG_ Speed : 0|16@1+ (0.01,0) [0|250] "km/h" Dash
             SG_ NewSignal : 24|8@1+ (1,0) [0|10] "" Dash
            """);
        var report = DbcDefinitionComparer.Compare(Db(Basic), current);
        Assert.Single(report.Rows.Where(x => x.Change == DbcDefinitionChange.Removed && x.Signal == "State"));
        Assert.Single(report.Rows.Where(x => x.Change == DbcDefinitionChange.Added && x.Signal == "NewSignal"));
    }

    [Fact]
    public void StandardAndExtendedWithSameNumericIdAreNotTheSameFrame()
    {
        var standard = Db("""
            BO_ 256 Standard: 8 ECU
             SG_ Speed : 0|8@1+ (1,0) [0|200] "" Dash
            """);
        var extended = Db("""
            BO_ 2147483904 Extended: 8 ECU
             SG_ Speed : 0|8@1+ (1,0) [0|200] "" Dash
            """);
        var comparison = DbcDefinitionComparer.Compare(standard, extended);
        Assert.Equal(4, comparison.Rows.Count);
        Assert.Equal(2, comparison.Added);
        Assert.Equal(2, comparison.Removed);
        Assert.Contains(comparison.Rows, x => x.IsExtended && x.Change == DbcDefinitionChange.Added &&
            x.Scope == "Signal" && x.Signal == "Speed");
        Assert.Contains(comparison.Rows, x => !x.IsExtended && x.Change == DbcDefinitionChange.Removed &&
            x.Scope == "Message");
    }

    [Fact]
    public void AddedWholeMessageEnumeratesEverySignalWithoutHidingTheirDefinitions()
    {
        var before = Db("""
            BO_ 256 Engine: 8 ECU
             SG_ Speed : 0|16@1+ (1,0) [0|100] "km/h" Dash
            """);
        var after = Db("""
            BO_ 256 Engine: 8 ECU
             SG_ Speed : 0|16@1+ (1,0) [0|100] "km/h" Dash
            BO_ 512 Battery: 8 BMS
             SG_ Voltage : 0|16@1+ (0.1,0) [0|6553.5] "V" ECU
             SG_ Current : 16|16@1- (0.1,0) [-3000|3000] "A" ECU
            """);
        var report = DbcDefinitionComparer.Compare(before, after);
        var inserted = report.Rows.Where(row => row.Identifier == 512).ToArray();
        Assert.Equal(3, inserted.Length);
        Assert.All(inserted, row => Assert.Equal(DbcDefinitionChange.Added, row.Change));
        Assert.Contains(inserted, row => row.Scope == "Signal" &&
            row.Signal == "Voltage" && row.CurrentDefinition.Contains("factor=0.1"));
        Assert.Contains(inserted, row => row.Scope == "Signal" &&
            row.Signal == "Current" && row.CurrentDefinition.Contains("signed=True"));
    }

    [Fact]
    public void MessageDlcChangeIsADecodingRisk()
    {
        var changed = Db(Basic.Replace("Engine: 8", "Engine: 4"));
        var row = Assert.Single(DbcDefinitionComparer.Compare(Db(Basic), changed).Rows
            .Where(x => x.Scope == "Message"));
        Assert.Equal(DbcDefinitionImpact.Decoding, row.Impact);
        Assert.Contains("DLC", row.Differences);
    }

    [Fact]
    public void ChangingMultiplexConditionRangeChangesDecodingMeaning()
    {
        var before = Db("""
            BO_ 256 SelectorFrame: 8 ECU
             SG_ Selector M : 0|8@1+ (1,0) [0|7] "" ECU
             SG_ Value m1 : 8|8@1+ (1,0) [0|255] "" ECU
            SG_MUL_VAL_ 256 Value Selector 1-3;
            """);
        var after = Db("""
            BO_ 256 SelectorFrame: 8 ECU
             SG_ Selector M : 0|8@1+ (1,0) [0|7] "" ECU
             SG_ Value m1 : 8|8@1+ (1,0) [0|255] "" ECU
            SG_MUL_VAL_ 256 Value Selector 2-4;
            """);
        var row = Assert.Single(DbcDefinitionComparer.Compare(before, after).Rows
            .Where(item => item.Scope == "Signal" && item.Signal == "Value"));
        Assert.Equal(DbcDefinitionChange.Changed, row.Change);
        Assert.Equal(DbcDefinitionImpact.Decoding, row.Impact);
        Assert.Contains("Mux conditions", row.Differences);
        Assert.Contains("[1,3]", row.BaselineDefinition);
        Assert.Contains("[2,4]", row.CurrentDefinition);
    }

    [Fact]
    public void SourceAttributeEditsAreReportedWithoutInventingNewBitLayout()
    {
        var before = Db("""
            BO_ 256 Engine: 8 ECU
             SG_ Speed : 0|8@1+ (1,0) [0|250] "km/h" ECU
            BA_ "GenSigStartValue" SG_ 256 Speed 20;
            """);
        var after = Db("""
            BO_ 256 Engine: 8 ECU
             SG_ Speed : 0|8@1+ (1,0) [0|250] "km/h" ECU
            BA_ "GenSigStartValue" SG_ 256 Speed 30;
            """);
        var row = Assert.Single(DbcDefinitionComparer.Compare(before, after).Rows
            .Where(item => item.Scope == "Signal" && item.Signal == "Speed"));
        Assert.Equal(DbcDefinitionChange.Changed, row.Change);
        Assert.Equal(DbcDefinitionImpact.Metadata, row.Impact);
        Assert.Contains("Attributes", row.Differences);
        Assert.DoesNotContain("Start bit", row.Differences);
    }

    [Fact]
    public void UnchangedRecordsStillHaveFullExplainableDefinitions()
    {
        var report = DbcDefinitionComparer.Compare(Db(Basic), Db(Basic));
        var speed = Assert.Single(report.Rows.Where(x => x.Signal == "Speed"));
        Assert.Contains("start=0", speed.BaselineDefinition);
        Assert.Contains("factor=0.01", speed.CurrentDefinition);
        Assert.Contains("11-bit", speed.FrameFormat);
    }
}
