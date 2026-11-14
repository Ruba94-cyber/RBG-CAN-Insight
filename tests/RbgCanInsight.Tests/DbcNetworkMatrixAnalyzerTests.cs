using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DbcNetworkMatrixAnalyzerTests
{
    [Fact]
    public void ExactMatchingKeepsDeclaredTransmittersDistinctFromRecordedEvidence()
    {
        var decoder = new DbcDecoder(Database("Primary", Message(0x123, false,
            "EngineData", transmitter: "ECM", receiver: "ABS, BCM")));
        var report = DbcNetworkMatrixAnalyzer.Analyze([decoder],
            [Statistic(0, 0x123, false, 17, 8)]);
        var row = Assert.Single(report.Rows);
        Assert.Equal("Primary", row.Database);
        Assert.Equal("ECM", row.DeclaredTransmitter);
        Assert.Equal("ABS, BCM", row.DeclaredReceivers);
        Assert.Equal(17, row.ObservedFrames);
        Assert.Equal(0, row.ObservedChannel);
        Assert.Equal(17, report.UniquelyMappedFrames);
        Assert.Equal(0, report.UnattributedFrames);
        Assert.Contains("not ECU proof", row.Evidence);
    }

    [Fact]
    public void StandardAndExtendedFramesWithSameNumberHaveSeparateNetworkRows()
    {
        var standard = Message(0x123, false, "Standard");
        var extended = Message(0x123, true, "Extended");
        var db = new DbcDatabase("Mixed",
            new Dictionary<uint, DbcMessage> { [standard.Identifier] = standard },
            MessageIdentities: new Dictionary<DbcMessageIdentity, DbcMessage>
            {
                [new(0x123, false)] = standard,
                [new(0x123, true)] = extended
            });
        var report = DbcNetworkMatrixAnalyzer.Analyze([new DbcDecoder(db)],
            [Statistic(0, 0x123, false, 2, 8), Statistic(0, 0x123, true, 9, 8)]);
        Assert.Equal(2, report.Rows.Count);
        Assert.Equal(2, report.DefinedMessages);
        Assert.Equal(11, report.UniquelyMappedFrames);
        Assert.Equal(2, report.Rows.Single(row => !row.IsExtended).ObservedFrames);
        Assert.Equal(9, report.Rows.Single(row => row.IsExtended).ObservedFrames);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void TwoDBCProfilesClaimingSameIdentityRemainAmbiguousNotAssigned()
    {
        var decoders = new[]
        {
            new DbcDecoder(Database("Left", Message(0x100, false, "A"))),
            new DbcDecoder(Database("Right", Message(0x100, false, "B")))
        };
        var report = DbcNetworkMatrixAnalyzer.Analyze(decoders,
            [Statistic(0, 0x100, false, 42, 8)]);
        Assert.All(report.Rows, row => Assert.Equal(0, row.ObservedFrames));
        Assert.Equal(0, report.UniquelyMappedFrames);
        Assert.Equal(42, report.UnattributedFrames);
        Assert.Equal("AMBIGUOUS_ACTIVE_DBC", Assert.Single(report.Issues).Code);
    }

    [Fact]
    public void DecoderChannelAssignmentPreventsCrossBusFalseCoverage()
    {
        var decoder = new DbcDecoder(Database("Bus2", Message(0x100, false, "Status")),
            new DbcDecoderOptions(Channel: 1));
        var report = DbcNetworkMatrixAnalyzer.Analyze([decoder],
            [Statistic(0, 0x100, false, 7, 8), Statistic(1, 0x100, false, 11, 8)]);
        var row = Assert.Single(report.Rows);
        Assert.Equal(1, row.ObservedChannel);
        Assert.Equal(11, row.ObservedFrames);
        Assert.Equal(7, report.UnattributedFrames);
        Assert.Equal("NO_ACTIVE_DBC", Assert.Single(report.Issues).Code);
    }

    [Fact]
    public void PgnFallbackIsExplicitlyLabeledUnverifiedNotExactOwnership()
    {
        var decoder = new DbcDecoder(Database("J1939",
            Message(0x18FEF100, true, "VehicleSpeed")),
            new DbcDecoderOptions(MatchByPgn: true));
        var report = DbcNetworkMatrixAnalyzer.Analyze([decoder],
            [Statistic(1, 0x18FEF105, true, 19, 8)]);
        var row = Assert.Single(report.Rows);
        Assert.Equal(19, row.ObservedFrames);
        Assert.Contains("Possible J1939 PGN", row.Evidence);
        Assert.Contains("possible J1939", row.PgnText);
    }

    [Fact]
    public void MoreRecordedDataThanDbcDlcIsReportedNotTruncated()
    {
        var decoder = new DbcDecoder(Database("X", Message(0x100, false, "Short", length: 8)));
        var report = DbcNetworkMatrixAnalyzer.Analyze([decoder],
            [Statistic(0, 0x100, false, 3, 12)]);
        Assert.Equal(3, Assert.Single(report.Rows).ObservedFrames);
        Assert.Equal("OBSERVED_DLC_EXCEEDS_DEFINITION", Assert.Single(report.Issues).Code);
    }

    [Fact]
    public void MatrixKeepsUnobservedDefinitionsInsteadOfCallingThemAbsentOnBus()
    {
        var decoder = new DbcDecoder(Database("Reference", Message(0x100, false, "Unseen")));
        var report = DbcNetworkMatrixAnalyzer.Analyze([decoder], []);
        var row = Assert.Single(report.Rows);
        Assert.Null(row.ObservedChannel);
        Assert.Equal("Not observed", row.ChannelText);
        Assert.Contains("not observed or not uniquely attributable", row.Evidence);
        Assert.Equal(0, report.UniquelyMappedFrames);
    }

    [Fact]
    public void UnmatchedMassiveIdentifierSetCapsDetailedIssuesButCountsAllOmissions()
    {
        var observed = Enumerable.Range(0, 25_021).Select(index =>
            Statistic(0, (uint)(0x1000 + index), true, 1, 8)).ToArray();
        var report = DbcNetworkMatrixAnalyzer.Analyze([], observed);
        Assert.Equal(25_000, report.Issues.Count);
        Assert.Equal(25_021, report.TotalIssueCount);
        Assert.Equal(21, report.OmittedIssues);
        Assert.Equal(25_021, report.UnattributedFrames);
        Assert.Contains("omitted", report.Summary);
    }

    private static DbcDatabase Database(string name, DbcMessage message) =>
        new(name, new Dictionary<uint, DbcMessage> { [message.Identifier] = message });

    private static DbcMessage Message(uint id, bool ext, string name,
        string transmitter = "ECM", string receiver = "ABS", int length = 8) =>
        new(id, name, length, transmitter, ext,
        [new DbcSignal("Value", 0, 8, DbcByteOrder.Intel, false, 1, 0,
            0, 255, "", receiver)]);

    private static MessageStatisticsRow Statistic(int channel, uint id,
        bool extended, long count, int length) =>
        new(channel, id, extended, count, 0, 100_000_000, 100, 100, 100,
            length, length);
}
