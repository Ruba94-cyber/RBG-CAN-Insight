using RbgCanInsight.Analysis;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class FrameEvidenceIdentityTests
{
    private static readonly string SourceSha256 = new string('A', 64);

    [Fact]
    public void TokenResolvesOriginalFrameAfterChronologicalReordering()
    {
        var firstInFile = new CanFrame(1, 0x123, new byte[] { 0xAA },
            900, sequence: 1);
        var secondInFile = new CanFrame(1, 0x123, new byte[] { 0xBB },
            100, sequence: 2);
        var sorted = new[] { secondInFile, firstInFile };

        var source = FrameEvidenceIdentity.FromFrame(SourceSha256, firstInFile);
        Assert.True(FrameEvidenceIdentity.TryParse(source.Token, out var parsed));
        Assert.Equal(source, parsed);
        Assert.True(parsed.TryResolve(SourceSha256, sorted, out var resolved));
        Assert.Equal(1, resolved);
        Assert.False(parsed.TryResolve(new string('B', 64), sorted, out _));
    }

    [Fact]
    public void FrameFormatChannelAndOriginalSequenceArePartOfIdentity()
    {
        var frame = new CanFrame(2, 0x123, [1, 2, 3], 123456,
            flags: CanFrameFlags.None, sequence: 7);
        var evidence = FrameEvidenceIdentity.FromFrame(SourceSha256, frame);
        Assert.False(evidence.Matches(frame with { Sequence = 8 }));
        Assert.False(evidence.Matches(new CanFrame(3, 0x123, [1, 2, 3],
            123456, sequence: 7)));
        Assert.False(evidence.Matches(new CanFrame(2, 0x123, [1, 2, 3],
            123456, flags: CanFrameFlags.Extended, sequence: 7)));
        Assert.False(evidence.Matches(new CanFrame(2, 0x124, [1, 2, 3],
            123456, sequence: 7)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RBGFRAME1:invalid:1:2:0:S:00000123")]
    [InlineData("RBGFRAME2:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA:1:2:0:S:00000123")]
    [InlineData("RBGFRAME1:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA:0:2:0:S:00000123")]
    [InlineData("RBGFRAME1:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA:1:2:0:S:1FFFFFFF")]
    [InlineData("RBGFRAME1:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA:1:2:-1:E:00000123")]
    [InlineData("RBGFRAME1:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA:1:2:0:X:00000123")]
    public void InvalidOrTamperedEvidenceTokensAreRejected(string? token)
    {
        Assert.False(FrameEvidenceIdentity.TryParse(token, out _));
    }

    [Fact]
    public void AnUnsequencedSyntheticFrameCannotBeClaimedAsStableSourceEvidence()
    {
        var frame = new CanFrame(0, 0x100, [1], 0);
        Assert.Throws<ArgumentException>(() =>
            FrameEvidenceIdentity.FromFrame(SourceSha256, frame));
        Assert.Throws<ArgumentException>(() =>
            FrameEvidenceIdentity.FromFrame("not-sha256", frame with { Sequence = 1 }));
    }

    [Fact]
    public void OriginalFileContentHashChangesWhenSourceBytesChange()
    {
        var before = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1, 2, 3]));
        var after = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1, 2, 4]));
        Assert.NotEqual(before, after);
        var frame = new CanFrame(0, 0x123, [1], 1000, sequence: 1);
        var token = FrameEvidenceIdentity.FromFrame(before, frame);
        Assert.False(token.TryResolve(after, new[] { frame }, out _));
    }
}
