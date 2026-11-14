using RbgCanInsight.Dbc;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DbcConsistencyCheckerTests
{
    private const string Faulty = """
        VERSION ""

        NS_ :
            BA_DEF_
            BA_
            VAL_

        BS_:

        BU_: ECU_A ECU_B ECU_UNUSED

        BO_ 256 Engine: 8 ECU_A
         SG_ Speed : 0|16@1+ (0.1,0) [0|9999] "km/h" ECU_B,ECU_GHOST
         SG_ Mode : 16|2@1+ (1,0) [0|0] "" ECU_B
         SG_ Overlap : 8|8@1+ (1,0) [0|255] "" ECU_B
         SG_ Beyond : 60|8@1+ (1,0) [0|255] "" ECU_B

        BO_ 400 DupA: 8 ECU_A
         SG_ X : 0|8@1+ (1,0) [0|255] "" ECU_B

        BO_ 400 DupB: 8 ECU_A
         SG_ X : 0|8@1+ (1,0) [0|255] "" ECU_B

        BO_ 300 Engine: 8 ECU_PHANTOM
         SG_ Y : 0|8@1+ (1,0) [0|255] "" ECU_B

        BO_ 301 NoSender: 8 Vector__XXX
         SG_ Z : 0|8@1+ (2,0) [0|100] "" ECU_B

        BA_DEF_ BO_ "GenMsgCycleTime" INT 0 65535;
        BA_DEF_ SG_ "Quality" INT 0 10;
        BA_DEF_ "BusType" STRING ;
        BA_DEF_ BO_ "NoDefault" INT 0 1;
        BA_DEF_DEF_ "GenMsgCycleTime" 10;
        BA_DEF_DEF_ "Quality" 99;
        BA_DEF_DEF_ "Undefined" 1;
        BA_ "GenMsgCycleTime" BO_ 256 20;
        BA_ "Quality" SG_ 256 Speed 55;
        BA_ "Quality" BO_ 300 1;
        BA_ "GenMsgCycleTime" BO_ 9999 20;
        VAL_ 256 Mode 0 "Off" 1 "On" 7 "TooBig";
        VAL_ 256 Nope 0 "x";
        VAL_ 777 Speed 0 "x";
        """;

    private static string[] Ids(DbcConsistencyReport r) => r.Findings.Select(f => f.RuleId).Distinct().Order().ToArray();

    [Fact]
    public void EveryRuleHasUniqueIdTitleAndDescription()
    {
        Assert.Equal(DbcConsistencyChecker.Rules.Count, DbcConsistencyChecker.Rules.Select(r => r.Id).Distinct().Count());
        Assert.All(DbcConsistencyChecker.Rules, r =>
        {
            Assert.Matches(@"^DBC-\d{3}$", r.Id);
            Assert.False(string.IsNullOrWhiteSpace(r.Title));
            Assert.False(string.IsNullOrWhiteSpace(r.Description));
        });
    }

    [Fact]
    public void FaultyDatabaseTriggersTheDocumentedRules()
    {
        var report = DbcConsistencyChecker.CheckText("faulty", Faulty);
        var ids = Ids(report);

        foreach (var expected in new[]
                 { "DBC-001", "DBC-002", "DBC-011", "DBC-012", "DBC-013", "DBC-020", "DBC-022", "DBC-030", "DBC-031",
                   "DBC-032", "DBC-033", "DBC-040", "DBC-041", "DBC-042", "DBC-043", "DBC-044", "DBC-045" })
            Assert.Contains(expected, ids);
        Assert.Contains(report.Findings, f => f.RuleId == "DBC-031" && f.Detail.Contains("ECU_GHOST"));
        Assert.Contains(report.Findings, f => f.RuleId == "DBC-030" && f.Detail.Contains("ECU_PHANTOM"));
        Assert.Contains(report.Findings, f => f.RuleId == "DBC-032" && f.Subject == "ECU_UNUSED");
        Assert.Contains(report.Findings, f => f.RuleId == "DBC-041" && f.Subject == "Quality");
        Assert.Contains(report.Findings, f => f.RuleId == "DBC-042" && f.Subject == "Quality" && f.Signal == "Speed");
        Assert.Contains(report.Findings, f => f.RuleId == "DBC-044" && f.Subject == "Quality");
        Assert.Contains(report.Findings, f => f.RuleId == "DBC-043" && f.Subject == "NoDefault");
        Assert.Contains(report.Findings, f => f.RuleId == "DBC-013" && f.Signal == "Speed"); // 0.1 * 65535 = 6553.5 < 9999
        Assert.Contains(report.Findings, f => f.RuleId == "DBC-012" && f.Signal == "Beyond");
        Assert.Equal(DbcEngineeringSeverity.Error, report.Findings.First(f => f.RuleId == "DBC-001").Severity);
        Assert.Empty(report.RulesSkipped.Where(r => r.NeedsSourceText));
    }

    [Fact]
    public void WithoutSourceTextOnlyModelRulesRunAndTheRestIsReportedSkipped()
    {
        var db = new DbcParser().Parse("faulty", new StringReader(Faulty));
        var report = DbcConsistencyChecker.Check(db);

        Assert.DoesNotContain(report.Findings, f => f.Rule.NeedsSourceText);
        Assert.Contains(report.RulesSkipped, r => r.Id == "DBC-001");
        Assert.Contains(report.RulesSkipped, r => r.Id == "DBC-051");
        Assert.Contains("skipped", report.ToSummaryText());
        Assert.Contains(report.Findings, f => f.RuleId == "DBC-002"); // duplicate name needs only the model
    }

    [Fact]
    public void CleanDatabaseHasNoErrorsOrWarnings()
    {
        const string clean = """
            VERSION ""
            NS_ :
            BS_:
            BU_: A B
            BO_ 100 Msg: 8 A
             SG_ Temp : 0|8@1+ (1,-40) [-40|215] "degC" B
            BA_DEF_ BO_ "GenMsgCycleTime" INT 0 65535;
            BA_DEF_DEF_ "GenMsgCycleTime" 0;
            BA_ "GenMsgCycleTime" BO_ 100 20;
            """;
        var report = DbcConsistencyChecker.CheckText("clean", clean);
        Assert.Equal(0, report.Count(DbcEngineeringSeverity.Error));
        Assert.Equal(0, report.Count(DbcEngineeringSeverity.Warning));
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void CycleTimeIsComparedWithObservedAverage()
    {
        const string text = """
            VERSION ""
            NS_ :
            BS_:
            BU_: A B
            BO_ 100 Msg: 8 A
             SG_ Temp : 0|8@1+ (1,-40) [-40|215] "degC" B
            BA_DEF_ BO_ "GenMsgCycleTime" INT 0 65535;
            BA_DEF_DEF_ "GenMsgCycleTime" 0;
            BA_ "GenMsgCycleTime" BO_ 100 20;
            """;
        var db = new DbcParser().Parse("c", new StringReader(text));
        var near = DbcConsistencyChecker.Check(db, new DbcConsistencyOptions
        { ObservedCycleMilliseconds = new Dictionary<DbcMessageIdentity, double> { [new(100, false)] = 21 } });
        Assert.DoesNotContain(near.Findings, f => f.RuleId == "DBC-051");

        var far = DbcConsistencyChecker.Check(db, new DbcConsistencyOptions
        { ObservedCycleMilliseconds = new Dictionary<DbcMessageIdentity, double> { [new(100, false)] = 100 } });
        var finding = Assert.Single(far.Findings, f => f.RuleId == "DBC-051");
        Assert.Contains("observed average 100", finding.Detail);
        Assert.DoesNotContain(far.RulesSkipped, r => r.Id == "DBC-051");
    }

    [Fact]
    public void MultiplexorWithoutBranchesAndOverlappingExtendedRangesAreReported()
    {
        const string text = """
            VERSION ""
            NS_ :
            BS_:
            BU_: A B
            BO_ 100 Mux: 8 A
             SG_ Sel M : 0|8@1+ (1,0) [0|255] "" B
             SG_ Data m0 : 8|8@1+ (1,0) [0|255] "" B
            BO_ 101 Alone: 8 A
             SG_ Sel2 M : 0|8@1+ (1,0) [0|255] "" B
             SG_ Plain : 8|8@1+ (1,0) [0|255] "" B
            BO_ 102 Ext: 8 A
             SG_ S M : 0|8@1+ (1,0) [0|255] "" B
             SG_ D m0M : 8|8@1+ (1,0) [0|255] "" B
            SG_MUL_VAL_ 102 D S 0-5, 3-8;
            """;
        var report = DbcConsistencyChecker.CheckText("mux", text);
        Assert.Contains(report.Findings, f => f.RuleId == "DBC-061" && f.Signal == "Sel2");
        Assert.DoesNotContain(report.Findings, f => f.RuleId == "DBC-061" && f.Signal == "Sel");
        Assert.Contains(report.Findings, f => f.RuleId == "DBC-062" && f.Signal == "D");
    }

    [Fact]
    public void J1939RulesFlagFrameFormatTransportAndDuplicatePgn()
    {
        // 0x80000000 marks a 29-bit ID in DBC files. EEC1 = prio 6 PGN 0xF004 SA 0; the "Dup" has prio 3.
        var eec1 = 0x98F00400u;
        var eec1Other = 0x8CF00400u;
        var pdu1 = 0x98EF2580u; // PF 0xEF < 240 with fixed destination 0x25, SA 0x80
        var text = $"""
            VERSION ""
            NS_ :
            BS_:
            BU_: A
            BO_ {eec1} EEC1: 8 A
             SG_ Rpm : 0|16@1+ (1,0) [0|0] "rpm" Vector__XXX
            BO_ {eec1Other} EEC1Dup: 8 A
             SG_ Rpm2 : 0|16@1+ (1,0) [0|0] "rpm" Vector__XXX
            BO_ {pdu1} Pdu1: 16 A
             SG_ Raw : 0|8@1+ (1,0) [0|0] "" Vector__XXX
            BO_ 200 Std: 8 A
             SG_ Zz : 0|8@1+ (1,0) [0|0] "" Vector__XXX
            BA_DEF_ BO_ "VFrameFormat" ENUM "StandardCAN","ExtendedCAN","J1939PG";
            BA_DEF_DEF_ "VFrameFormat" "StandardCAN";
            BA_ "VFrameFormat" BO_ {eec1} 2;
            BA_ "VFrameFormat" BO_ {eec1Other} 2;
            BA_ "VFrameFormat" BO_ {pdu1} 2;
            BA_ "VFrameFormat" BO_ 200 2;
            """;
        var report = DbcConsistencyChecker.CheckText("j", text);
        Assert.Contains(report.Findings, f => f.RuleId == "DBC-070" && f.Subject == "Std");
        Assert.Contains(report.Findings, f => f.RuleId == "DBC-072" && f.Subject == "Pdu1");
        Assert.Contains(report.Findings, f => f.RuleId == "DBC-071" && f.Subject == "Pdu1");
        Assert.Contains(report.Findings, f => f.RuleId == "DBC-073" && f.Subject.Contains("EEC1Dup"));
        Assert.DoesNotContain(report.Findings, f => f.RuleId is "DBC-071" or "DBC-072" && f.Subject == "EEC1");
    }

    [Fact]
    public void CsvExportQuotesFieldsAndKeepsOneRowPerFinding()
    {
        var report = DbcConsistencyChecker.CheckText("faulty", Faulty);
        var lines = report.ToCsv().TrimEnd().Split("\r\n");
        Assert.Equal("Rule,Title,Severity,Category,Subject,Signal,Detail", lines[0]);
        Assert.True(lines.Length >= report.Findings.Count + 1);
        Assert.Contains("\"", report.ToCsv()); // details with commas are quoted
    }

    [Fact]
    public void RealOpenDbcFilesRunWithoutExceptionsAndKeepFindingsBounded()
    {
        var dir = FindDir();
        if (dir is null) return; // git-ignored real files are optional
        var files = Directory.GetFiles(dir, "*.dbc").Take(6).ToArray();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var report = DbcConsistencyChecker.CheckText(Path.GetFileName(file), text);
            Assert.True(report.Findings.Count <= 2000);
            Assert.All(report.Findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Detail)));
        }
    }

    private static string? FindDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "artifacts", "online-samples", "opendbc");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
