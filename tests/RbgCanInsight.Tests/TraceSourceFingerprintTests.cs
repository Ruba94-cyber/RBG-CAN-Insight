using RbgCanInsight.Analysis;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class TraceSourceFingerprintTests
{
    [Fact]
    public void UnchangedSourceCanBeVerifiedAndRetainsItsImportTimeHash()
    {
        var path = TemporaryPath();
        try
        {
            File.WriteAllBytes(path, [0x01, 0x02, 0x03, 0x04]);
            var original = TraceSourceFingerprint.ReadStamp(path);
            Assert.NotNull(original);
            var snapshot = TraceSourceFingerprint.TryCapture(path, original);
            Assert.NotNull(snapshot);
            Assert.True(snapshot.VerifyUnchangedFile());
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                new byte[] { 1, 2, 3, 4 })), snapshot.Sha256);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReplacedFileWithSameLengthAndTimestampIsStillRejected()
    {
        var path = TemporaryPath();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            var stamp = TraceSourceFingerprint.ReadStamp(path);
            Assert.NotNull(stamp);
            var snapshot = TraceSourceFingerprint.TryCapture(path, stamp);
            Assert.NotNull(snapshot);

            File.WriteAllBytes(path, [1, 2, 3, 5]);
            File.SetLastWriteTimeUtc(path, stamp.Value.LastWriteUtc);
            Assert.Equal(stamp, TraceSourceFingerprint.ReadStamp(path));
            Assert.False(snapshot.VerifyUnchangedFile());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SourceModifiedDuringImportMustNotBeCertified()
    {
        var path = TemporaryPath();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            var initial = TraceSourceFingerprint.ReadStamp(path);
            File.AppendAllText(path, "unexpected");
            Assert.Null(TraceSourceFingerprint.TryCapture(path, initial));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingOrDeletedFileCannotBeCertified()
    {
        var path = TemporaryPath();
        Assert.Null(TraceSourceFingerprint.ReadStamp(path));
        Assert.Null(TraceSourceFingerprint.TryCapture(path, null));
        try
        {
            File.WriteAllBytes(path, [11, 22]);
            var snapshot = TraceSourceFingerprint.TryCapture(path,
                TraceSourceFingerprint.ReadStamp(path));
            Assert.NotNull(snapshot);
            File.Delete(path);
            Assert.False(snapshot.VerifyUnchangedFile());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static string TemporaryPath() =>
        Path.Combine(Path.GetTempPath(), "rbg-trace-fingerprint-" +
            Guid.NewGuid().ToString("N") + ".asc");
}
