using RbgCanInsight.Correlation;
using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class MappingReviewGuardTests
{
    private static DidParameter SourceDid() =>
        new(0xF190, "CurrentSpeed", "Current speed", "Wheel speed", "km/h",
            0, 250, 0.01, 0, "A_UINT16", 16, "ECM", "synthetic.odx",
            [new DiagnosticFieldDefinition("Speed", "Speed", "", 0, 16,
                "A_UINT16", "km/h", 0, 250, 0.01, 0, false, "LITTLE")]);

    private static (DbcDatabase Database, DbcMessage Message, DbcSignal Signal) SourceCan()
    {
        var database = new DbcParser().Parse("NetworkA", new StringReader(
            "BO_ 256 Vehicle: 8 ECM\n SG_ Speed : 0|16@1+ (0.01,0) [0|250] \"km/h\" ECM\n"));
        var message = database.Messages[256];
        return (database, message, message.Signals[0]);
    }

    private static DidSignalMapping Stored(
        DidParameter did, DbcDatabase db, DbcMessage message, DbcSignal signal) =>
        new(did.Did, did.Name, did.Ecu, db.Name, message.Name, signal.Name, 0.91,
            "user reviewed", MappingStatus.Confirmed, "Check at service",
            MessageIdentifier: message.Identifier,
            IsExtended: message.IsExtended,
            DidDefinitionFingerprint: MappingDefinitionFingerprint.ForDid(did),
            CanSignalFingerprint: MappingDefinitionFingerprint.ForSignal(db.Name, message, signal));

    [Fact]
    public void ExactSameDefinitionsCanRetainManualReviewAcrossReload()
    {
        var did = SourceDid();
        var (db, message, signal) = SourceCan();
        var saved = Stored(did, db, message, signal);
        Assert.True(MappingReviewGuard.Assess(saved, did, db.Name, message, signal).MayRetainReview);
        Assert.Equal(string.Empty, MappingReviewGuard.Assess(
            saved, did, db.Name, message, signal).Reason);
    }

    [Fact]
    public void ChangedSignalBitOrScaleInvalidatesReviewedMapping()
    {
        var did = SourceDid();
        var (db, message, signal) = SourceCan();
        var saved = Stored(did, db, message, signal);
        Assert.False(MappingReviewGuard.Assess(saved, did, db.Name, message,
            signal with { StartBit = 8 }).MayRetainReview);
        var scale = MappingReviewGuard.Assess(saved, did, db.Name, message,
            signal with { Factor = 0.02 });
        Assert.False(scale.MayRetainReview);
        Assert.Contains("DBC signal definition changed", scale.Reason);
    }

    [Fact]
    public void ChangedDidFieldsOrVariantInvalidatesReviewedMapping()
    {
        var did = SourceDid();
        var (db, message, signal) = SourceCan();
        var saved = Stored(did, db, message, signal);
        var fieldChange = did with { FieldDefinitions =
            [did.Fields[0] with { BitOffset = 8 }] };
        Assert.False(MappingReviewGuard.Assess(saved, fieldChange, db.Name,
            message, signal).MayRetainReview);
        Assert.False(MappingReviewGuard.Assess(saved, did with { Ecu = "ABS" },
            db.Name, message, signal).MayRetainReview);
        Assert.False(MappingReviewGuard.Assess(saved, did with { Name = "Other" },
            db.Name, message, signal).MayRetainReview);
    }

    [Fact]
    public void LegacyNameOnlyAssociationLoadsButCannotRetainConfirmation()
    {
        var did = SourceDid();
        var (db, message, signal) = SourceCan();
        var saved = Stored(did, db, message, signal) with {
            DidDefinitionFingerprint = null, CanSignalFingerprint = null,
            MessageIdentifier = null, IsExtended = null
        };
        var assessment = MappingReviewGuard.Assess(saved, did, db.Name, message, signal);
        Assert.False(assessment.MayRetainReview);
        Assert.False(string.IsNullOrWhiteSpace(assessment.Reason));
    }

    [Fact]
    public void SaveMapReplacesExistingFileWithoutLeakingPartials()
    {
        var did = SourceDid();
        var (db, message, signal) = SourceCan();
        var path = Path.Combine(Path.GetTempPath(),
            "rbg-map-staged-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new MappingDocument("First", DateTimeOffset.UtcNow, []).Save(path);
            new MappingDocument("Second", DateTimeOffset.UtcNow,
                [Stored(did, db, message, signal)]).Save(path);
            var loaded = MappingDocument.Load(path);
            Assert.Equal("Second", loaded.Title);
            Assert.Single(loaded.Mappings);
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!,
                Path.GetFileName(path) + ".partial-*"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ReviewedMapPersistsFingerprintsButLegacyJsonStillLoads()
    {
        var did = SourceDid();
        var (db, message, signal) = SourceCan();
        var modern = Stored(did, db, message, signal);
        var path = Path.Combine(Path.GetTempPath(),
            "rbg-map-fingerprint-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new MappingDocument("Fixture", DateTimeOffset.UtcNow, [modern]).Save(path);
            var loaded = Assert.Single(MappingDocument.Load(path).Mappings);
            Assert.Equal(modern.DidDefinitionFingerprint, loaded.DidDefinitionFingerprint);
            Assert.Equal(modern.CanSignalFingerprint, loaded.CanSignalFingerprint);
            Assert.True(MappingReviewGuard.Assess(
                loaded, did, db.Name, message, signal).MayRetainReview);
            // This mimics a real old JSON document with no new properties at
            // all, rather than a recent document that explicitly writes null.
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            var legacyEntry = json["Mappings"]![0]!.AsObject();
            Assert.True(legacyEntry.Remove("DidDefinitionFingerprint"));
            Assert.True(legacyEntry.Remove("CanSignalFingerprint"));
            File.WriteAllText(path, json.ToJsonString());
            var legacy = Assert.Single(MappingDocument.Load(path).Mappings);
            Assert.Null(legacy.DidDefinitionFingerprint);
            Assert.Null(legacy.CanSignalFingerprint);
            Assert.False(MappingReviewGuard.Assess(
                legacy, did, db.Name, message, signal).MayRetainReview);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
