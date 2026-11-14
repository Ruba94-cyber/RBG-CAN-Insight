using RbgCanInsight.Dbc;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Bad or conflicting DBC input must surface through the parse-warning list (the DBC Manager badge)
/// instead of loading silently.</summary>
public sealed class DbcLoadWarningTests
{
    private const string ValidDbc = """
        VERSION ""

        NS_ :
            CM_
            BA_DEF_
            VAL_

        BS_:

        BU_: ECM

        BO_ 256 EngineData: 8 ECM
         SG_ EngCoolantTemp : 0|8@1+ (1,-40) [-40|215] "degC" Vector__XXX
         SG_ EngineLoad : 8|8@1+ (0.4,0) [0|100] "%" Vector__XXX

        CM_ SG_ 256 EngineLoad "Calculated load";
        """;

    private static DbcDatabase Parse(string text, string name = "t") => new DbcParser().Parse(name, new StringReader(text));

    private const string RealWorldDbc = """
        VERSION "1.0"

        NS_ :
        NS_DESC_
        CM_
        BA_DEF_
        BA_
        VAL_
        CAT_DEF_
        CAT_
        FILTER
        BA_DEF_DEF_
        EV_DATA_
        ENVVAR_DATA_
        SGTYPE_
        SGTYPE_VAL_
        BA_DEF_SGTYPE_
        BA_SGTYPE_
        SIG_TYPE_REF_
        VAL_TABLE_
        SIG_GROUP_
        SIG_VALTYPE_
        SIGTYPE_VALTYPE_
        BO_TX_BU_
        BA_DEF_REL_
        BA_REL_
        BA_DEF_DEF_REL_
        BU_SG_REL_
        BU_EV_REL_
        BU_BO_REL_
        SG_MUL_VAL_

        BS_:

        BU_: ECM GW

        VAL_TABLE_ Gear 2 "Drive" 1 "Neutral" 0 "Park" ;

        BO_ 256 EngineData: 8 ECM
         SG_ EngCoolantTemp : 0|8@1+ (1,-40) [-40|215] "degC" GW
         SG_ EngineLoad : 8|8@1+ (0.4,0) [0|100] "%" GW

        BO_ 2566844926 J1939Msg: 8 ECM
         SG_ Speed : 0|16@1+ (0.00390625,0) [0|250] "km/h" GW

        BO_TX_BU_ 256 : ECM,GW;

        EV_ EnvVar1: 0 [0|100] "" 0 1 DUMMY_NODE_VECTOR0 Vector__XXX;
        ENVVAR_DATA_ EnvVar1: 8;
        CAT_DEF_ 1 "Cat" 1;
        CAT_ SG_ 256 EngineLoad 1;
        FILTER "f1";
        SGTYPE_ SType: 8@1+ (1,0) [0|255] "" 0, Gear;
        SIG_GROUP_ 256 Group1 1 : EngCoolantTemp EngineLoad;
        SIG_VALTYPE_ 2566844926 Speed : 1;

        BA_DEF_ "BusType" STRING ;
        BA_DEF_ BO_ "GenMsgCycleTime" INT 0 65535;
        BA_DEF_REL_ BU_SG_REL_ "GenSigTimeout" INT 0 65535;
        BA_DEF_DEF_ "BusType" "CAN";
        BA_DEF_DEF_REL_ "GenSigTimeout" 0;
        BA_ "BusType" "CAN";
        BA_ "GenMsgCycleTime" BO_ 256 100;
        BA_REL_ "GenSigTimeout" BU_SG_REL_ GW SG_ 256 EngineLoad 10;

        CM_ "Network comment";
        CM_ BO_ 256 "Engine data
        spans several lines
        BO_ 999 not a real message;
        and ends here";
        CM_ SG_ 256 EngineLoad "Calculated load";
        VAL_ 256 EngineLoad 255 "Error" 254 "NA" ;
        """;

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void RealWorldDbcWithKnownKeywordsProducesNoWarnings(string newline)
    {
        var text = RealWorldDbc.ReplaceLineEndings(newline);
        foreach (var candidate in new[] { text, "﻿" + text })
        {
            var database = Parse(candidate);
            Assert.Empty(database.ParseWarnings);
            Assert.Equal(2, database.Messages.Count);
            Assert.Contains(database.Messages.Values, m => m.IsExtended);
        }
    }

    [Fact]
    public void ValidDbcProducesNoWarnings()
    {
        var database = Parse(ValidDbc);
        Assert.Empty(database.ParseWarnings);
        Assert.False(DbcLoadChecks.HasNoMessages(database));
    }

    [Fact]
    public void GarbageFileHasNoMessagesAndWarnsThatItIsNotADbc()
    {
        var database = Parse("this is not a dbc\nrandom garbage 12345\n\u0001\u0002 binary-ish\n");

        Assert.True(DbcLoadChecks.HasNoMessages(database));
        Assert.Contains(database.ParseWarnings, w => w.Contains("unrecognised statement"));
        Assert.Contains(database.ParseWarnings, w => w.Contains("No messages were parsed"));
    }

    [Fact]
    public void EmptyFileHasNoMessagesButNoGarbageWarning()
    {
        var database = Parse(string.Empty);
        Assert.True(DbcLoadChecks.HasNoMessages(database));
        Assert.DoesNotContain(database.ParseWarnings, w => w.Contains("No messages were parsed"));
    }

    [Fact]
    public void TruncatedSignalLineIsReportedAsMalformed()
    {
        var database = Parse(ValidDbc[..ValidDbc.IndexOf(" SG_ EngineLoad", StringComparison.Ordinal)] + " SG_ EngineLoad : 8|8@1+ (0.4,0) [0");

        Assert.Single(database.Messages);
        Assert.Contains(database.ParseWarnings, w => w.Contains("malformed signal definition"));
    }

    [Fact]
    public void CommentCutOffAtEndOfFileIsReportedAsTruncated()
    {
        var database = Parse(ValidDbc.TrimEnd().TrimEnd(';') + "\n");
        Assert.Contains(database.ParseWarnings, w => w.Contains("before the end of the file"));
    }

    [Fact]
    public void ManyBadLinesAreSummarisedInsteadOfFloodingTheBadge()
    {
        var garbage = string.Join('\n', Enumerable.Range(0, 20).Select(i => "junk line " + i));
        var database = Parse(ValidDbc + "\n" + garbage);

        Assert.Contains(database.ParseWarnings, w => w.Contains("15 more"));
        Assert.True(database.ParseWarnings.Count < 10);
    }

    [Fact]
    public void SameMessageIdInTwoDatabasesIsFlaggedOnTheSecond()
    {
        var first = Parse(ValidDbc, "first");
        var second = Parse(ValidDbc, "second");

        var warnings = DbcLoadChecks.FindDuplicateMessageIds(second, [first]);

        var warning = Assert.Single(warnings);
        Assert.Contains("0x100", warning);
        Assert.Contains("'first'", warning);
    }

    [Fact]
    public void DifferentNamesForTheSameIdAreDescribed()
    {
        var first = Parse(ValidDbc, "first");
        var second = Parse(ValidDbc.Replace("EngineData", "MotorData"), "second");

        var warning = Assert.Single(DbcLoadChecks.FindDuplicateMessageIds(second, [first]));
        Assert.Contains("'MotorData'", warning);
        Assert.Contains("'EngineData'", warning);
    }

    [Fact]
    public void ReloadingTheSameDatabaseNameIsNotAConflict()
    {
        var first = Parse(ValidDbc, "same");
        Assert.Empty(DbcLoadChecks.FindDuplicateMessageIds(Parse(ValidDbc, "same"), [first]));
    }

    [Fact]
    public void DatabasesThatCannotShareAChannelAreNotComparedForDuplicateIds()
    {
        var first = Parse(ValidDbc, "first");
        var second = Parse(ValidDbc, "second");

        Assert.Empty(DbcLoadChecks.FindDuplicateMessageIds(second, [first], _ => false));
        Assert.Single(DbcLoadChecks.FindDuplicateMessageIds(second, [first], _ => true));
    }
}
