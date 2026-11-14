using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class MappingDocumentValidationTests
{
    private static DidSignalMapping Valid() =>
        new(0xF190, "Synthetic DID", "ECM", "synthetic.dbc", "Engine", "State",
            0.91, "synthetic ranking", MappingStatus.ToVerify, "review later",
            0x123, false);

    private static void InTemporaryFile(Action<string> check)
    {
        var path = Path.Combine(Path.GetTempPath(),
            "rbg-mapping-validation-" + Guid.NewGuid().ToString("N") + ".json");
        try { check(path); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void NormalDocumentRoundTripsWithoutChangingReviewStateOrIdentity()
    {
        InTemporaryFile(path =>
        {
            var original = new MappingDocument("Valid", DateTimeOffset.UnixEpoch, [Valid()]);
            original.Save(path);
            var loaded = MappingDocument.Load(path);
            Assert.Equal(original.Title, loaded.Title);
            Assert.Equal(original.SavedAt, loaded.SavedAt);
            Assert.Equal(Valid(), Assert.Single(loaded.Mappings));
        });
    }

    [Fact]
    public void NullOrMissingMappingDataIsRejectedRatherThanCrashingLater()
    {
        InTemporaryFile(path =>
        {
            File.WriteAllText(path, """
                {"Title":"Broken","SavedAt":"2026-11-05T00:00:00+00:00","Mappings":null}
                """);
            Assert.Throws<InvalidDataException>(() => MappingDocument.Load(path));
            new MappingDocument("Broken", DateTimeOffset.UnixEpoch, [null!]).Save(path);
            Assert.Throws<InvalidDataException>(() => MappingDocument.Load(path));
        });
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void InvalidDidIsRejected(int did)
    {
        InTemporaryFile(path =>
        {
            new MappingDocument("Invalid DID", DateTimeOffset.UnixEpoch,
                [Valid() with { Did = did }]).Save(path);
            Assert.Throws<InvalidDataException>(() => MappingDocument.Load(path));
        });
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void OutOfRangeCandidateRankingIsRejected(double score)
    {
        InTemporaryFile(path =>
        {
            new MappingDocument("Invalid score", DateTimeOffset.UnixEpoch,
                [Valid() with { Confidence = score }]).Save(path);
            Assert.Throws<InvalidDataException>(() => MappingDocument.Load(path));
        });
    }

    [Fact]
    public void UnknownReviewStatusAndNullIdentityAreRejected()
    {
        InTemporaryFile(path =>
        {
            new MappingDocument("Unknown status", DateTimeOffset.UnixEpoch,
                [Valid() with { Status = (MappingStatus)999 }]).Save(path);
            Assert.Throws<InvalidDataException>(() => MappingDocument.Load(path));
            new MappingDocument("Null source identity", DateTimeOffset.UnixEpoch,
                [Valid() with { Signal = null! }]).Save(path);
            Assert.Throws<InvalidDataException>(() => MappingDocument.Load(path));
        });
    }

    [Theory]
    [InlineData(0x800u, false)]
    [InlineData(0x20000000u, true)]
    [InlineData(0x20000000u, null)]
    public void InvalidFrameFormatAndArbitrationIdAreRejected(uint id, bool? extended)
    {
        InTemporaryFile(path =>
        {
            new MappingDocument("Invalid CAN", DateTimeOffset.UnixEpoch,
                [Valid() with { MessageIdentifier = id, IsExtended = extended }]).Save(path);
            Assert.Throws<InvalidDataException>(() => MappingDocument.Load(path));
        });
    }

    [Fact]
    public void UnknownLegacyCanFormatRemainsLoadableWhenArbitrationIdIsLegal()
    {
        InTemporaryFile(path =>
        {
            var legacy = Valid() with { IsExtended = null, MessageIdentifier = 0x123u,
                DidDefinitionFingerprint = null, CanSignalFingerprint = null };
            new MappingDocument("Legacy", DateTimeOffset.UnixEpoch, [legacy]).Save(path);
            Assert.Equal(legacy, Assert.Single(MappingDocument.Load(path).Mappings));
        });
    }
}
