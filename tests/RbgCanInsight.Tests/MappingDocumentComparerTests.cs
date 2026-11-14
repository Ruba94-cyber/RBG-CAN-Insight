using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class MappingDocumentComparerTests
{
    private static string Fingerprint(char hexDigit) => "sem-v1:" + new string(hexDigit, 64);

    private static DidSignalMapping Mapping(
        int did = 0xF405, string ecu = "ECM", string name = "CoolantTemp",
        MappingStatus status = MappingStatus.Confirmed,
        string? didHash = null, string? canHash = null,
        uint? canId = 0x100, string signal = "EngCoolantTemp", string notes = "") =>
        new(did, name, ecu, "powertrain", "Engine", signal, 0.91,
            "synthetic source", status, notes, canId, false,
            didHash ?? Fingerprint('A'), canHash ?? Fingerprint('B'));

    private static MappingDocument Doc(params DidSignalMapping[] rows) =>
        new("Synthetic map", DateTimeOffset.UnixEpoch, rows);

    [Fact]
    public void UnchangedReviewedAssociationRetainsMatchedEvidence()
    {
        var item = Mapping(notes: "inspected");
        var row = Assert.Single(MappingDocumentComparer.Compare(Doc(item), Doc(item)).Rows);
        Assert.Equal(MappingDiffKind.Unchanged, row.Kind);
        Assert.Equal(MappingDefinitionEvidence.Same, row.DidDefinition);
        Assert.Equal(MappingDefinitionEvidence.Same, row.CanDefinition);
        Assert.False(row.RequiresReview);
    }

    [Fact]
    public void ChangedBitLayoutOrDiagnosticDefinitionRequiresReReview()
    {
        var initial = Mapping();
        var updated = initial with { CanSignalFingerprint = Fingerprint('C') };
        var can = Assert.Single(MappingDocumentComparer.Compare(Doc(initial), Doc(updated)).Rows);
        Assert.Equal(MappingDiffKind.Changed, can.Kind);
        Assert.Equal(MappingDefinitionEvidence.Changed, can.CanDefinition);
        Assert.True(can.RequiresReview);
        Assert.Contains("DBC signal fingerprint changed", can.Explanation);
        var did = Assert.Single(MappingDocumentComparer.Compare(Doc(initial),
            Doc(initial with { DidDefinitionFingerprint = Fingerprint('D') })).Rows);
        Assert.Equal(MappingDefinitionEvidence.Changed, did.DidDefinition);
    }

    [Fact]
    public void NoFingerprintsNeverSilentlyProduceAnUnchangedVerifiedRow()
    {
        var old = Mapping() with
        {
            CanSignalFingerprint = null, DidDefinitionFingerprint = null
        };
        var current = Mapping() with
        {
            CanSignalFingerprint = null, DidDefinitionFingerprint = null
        };
        var row = Assert.Single(MappingDocumentComparer.Compare(Doc(old), Doc(current)).Rows);
        Assert.Equal(MappingDiffKind.Unverifiable, row.Kind);
        Assert.True(row.RequiresReview);
        Assert.Contains("semantic equality cannot be established", row.Explanation);
    }

    [Fact]
    public void IdenticalMalformedOrUnknownFutureHashesAreNotTrusted()
    {
        var baseline = Mapping() with
        {
            DidDefinitionFingerprint = "sem-v1:NOT_A_SHA256",
            CanSignalFingerprint = "sem-v2:" + new string('A', 64)
        };
        var comparison = MappingDocumentComparer.Compare(Doc(baseline), Doc(baseline));
        var result = Assert.Single(comparison.Rows);
        Assert.Equal(MappingDiffKind.Unverifiable, result.Kind);
        Assert.Equal(MappingDefinitionEvidence.Unverifiable, result.DidDefinition);
        Assert.Equal(MappingDefinitionEvidence.Unverifiable, result.CanDefinition);
        Assert.True(result.RequiresReview);
    }

    [Fact]
    public void UnmappedDidRowsDoNotGenerateThousandsOfFalseCanReviewWarnings()
    {
        var didOnly = Mapping() with
        {
            Signal = "", Message = "", Database = "", MessageIdentifier = null,
            IsExtended = null, CanSignalFingerprint = null,
            Status = MappingStatus.Unmatched
        };
        var report = MappingDocumentComparer.Compare(Doc(didOnly), Doc(didOnly));
        var row = Assert.Single(report.Rows);
        Assert.Equal(MappingDiffKind.Unchanged, row.Kind);
        Assert.Equal(MappingDefinitionEvidence.NotApplicable, row.CanDefinition);
        Assert.False(row.RequiresReview);
        Assert.Equal(0, report.RequiresReview);
    }

    [Fact]
    public void LegacyUnmappedRowsMayBeUnverifiableWithoutRequiringSignalReview()
    {
        var didOnly = Mapping() with
        {
            Signal = "", Message = "", Database = "",
            MessageIdentifier = null, IsExtended = null,
            DidDefinitionFingerprint = null, CanSignalFingerprint = null,
            Status = MappingStatus.Unmatched
        };
        var row = Assert.Single(MappingDocumentComparer.Compare(
            Doc(didOnly), Doc(didOnly)).Rows);
        Assert.Equal(MappingDiffKind.Unverifiable, row.Kind);
        Assert.False(row.RequiresReview);
    }

    [Fact]
    public void AssociatingPreviouslyUnmappedDidIsARealIdentityChange()
    {
        var assigned = Mapping();
        var noMapping = assigned with
        {
            Signal = "", Message = "", Database = "",
            MessageIdentifier = null, IsExtended = null,
            CanSignalFingerprint = null, Status = MappingStatus.Unmatched
        };
        var result = Assert.Single(MappingDocumentComparer.Compare(
            Doc(noMapping), Doc(assigned)).Rows);
        Assert.Equal(MappingDiffKind.Changed, result.Kind);
        Assert.True(result.RequiresReview);
        Assert.Contains("CAN signal identity changed", result.Explanation);
    }

    [Fact]
    public void ChangedSignalFormatOrIdIsNotMergedByIdenticalSignalNames()
    {
        var baseline = Mapping();
        var next = baseline with { MessageIdentifier = 0x200, IsExtended = true };
        var row = Assert.Single(MappingDocumentComparer.Compare(Doc(baseline), Doc(next)).Rows);
        Assert.Equal(MappingDiffKind.Changed, row.Kind);
        Assert.True(row.RequiresReview);
        Assert.Contains("CAN signal identity changed", row.Explanation);
        Assert.Contains("29-bit", row.CurrentSignal);
    }

    [Fact]
    public void AddedAndRemovedMappingsAreSeparateAndDeterministicallyOrdered()
    {
        var baseline = Mapping(did: 0xF190);
        var current = Mapping(did: 0xF405);
        var report = MappingDocumentComparer.Compare(Doc(baseline), Doc(current));
        Assert.Collection(report.Rows,
            row =>
            {
                Assert.Equal(0xF190, row.Did);
                Assert.Equal(MappingDiffKind.Removed, row.Kind);
                Assert.Equal(1, row.BaselineCount);
                Assert.Equal(0, row.CurrentCount);
            },
            row =>
            {
                Assert.Equal(0xF405, row.Did);
                Assert.Equal(MappingDiffKind.Added, row.Kind);
                Assert.Equal(0, row.BaselineCount);
                Assert.Equal(1, row.CurrentCount);
            });
    }

    [Fact]
    public void DuplicateEcuDidVariantsAreFlaggedNotPairedByInsertionOrder()
    {
        var baseline = Doc(
            Mapping(name: "EngineA"),
            Mapping(name: "EngineB", canId: 0x200));
        var current = Doc(Mapping(name: "EngineB", canId: 0x200));
        var row = Assert.Single(MappingDocumentComparer.Compare(baseline, current).Rows);
        Assert.Equal(MappingDiffKind.Ambiguous, row.Kind);
        Assert.True(row.RequiresReview);
        Assert.Equal(2, row.BaselineCount);
        Assert.Equal(1, row.CurrentCount);
    }

    [Fact]
    public void DifferentEcusWithSameDidNeverCrossPollinate()
    {
        var baseline = Doc(Mapping(ecu: "ECM"));
        var current = Doc(Mapping(ecu: "BCM"));
        var report = MappingDocumentComparer.Compare(baseline, current);
        Assert.Equal(1, report.Added);
        Assert.Equal(1, report.Removed);
        Assert.DoesNotContain(report.Rows, row => row.Kind == MappingDiffKind.Unchanged);
    }

    [Fact]
    public void NotesOnlyAndReviewOnlyChangesRemainVisibleWithoutFalseDefinitionDrift()
    {
        var baseline = Mapping(status: MappingStatus.Confirmed);
        var after = baseline with { Notes = "needs peer review", Status = MappingStatus.ToVerify };
        var row = Assert.Single(MappingDocumentComparer.Compare(Doc(baseline), Doc(after)).Rows);
        Assert.Equal(MappingDiffKind.Changed, row.Kind);
        Assert.Equal(MappingDefinitionEvidence.Same, row.DidDefinition);
        Assert.Equal(MappingDefinitionEvidence.Same, row.CanDefinition);
        Assert.Contains("Analyst notes changed", row.Explanation);
        Assert.Contains("Review status", row.Explanation);
        Assert.True(row.RequiresReview);
    }
}
