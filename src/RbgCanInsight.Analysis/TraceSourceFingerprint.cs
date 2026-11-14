using System.Security.Cryptography;

namespace RbgCanInsight.Analysis;

/// <summary>
/// Snapshot of the original offline trace's byte identity. A digest computed
/// only at export time is not evidence of the bytes decoded earlier: the
/// source file may have been replaced between import and report generation.
/// </summary>
public sealed record TraceSourceFingerprint(
    string Path,
    long Length,
    DateTime LastWriteUtc,
    string Sha256)
{
    public readonly record struct FileStamp(long Length, DateTime LastWriteUtc);

    public static FileStamp? ReadStamp(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            return new FileStamp(file.Length, file.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Optional best-effort fingerprint. The stamp recorded before opening
    /// the trace must match both sides of hashing; otherwise file identity
    /// cannot be asserted. Failure never changes the imported frame content.
    /// </summary>
    public static TraceSourceFingerprint? TryCapture(string path, FileStamp? beforeImport)
    {
        if (beforeImport is null || ReadStamp(path) != beforeImport)
            return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 128 * 1024,
                options: FileOptions.SequentialScan);
            var digest = Convert.ToHexString(SHA256.HashData(stream));
            if (ReadStamp(path) != beforeImport) return null;
            return new TraceSourceFingerprint(System.IO.Path.GetFullPath(path),
                beforeImport.Value.Length, beforeImport.Value.LastWriteUtc, digest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Full rehash, rather than trusting filename or timestamps alone. Only
    /// use the original SHA for frame-evidence IDs if this check succeeds.
    /// </summary>
    public bool VerifyUnchangedFile()
    {
        var expected = new FileStamp(Length, LastWriteUtc);
        if (ReadStamp(Path) != expected) return false;
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 128 * 1024,
                options: FileOptions.SequentialScan);
            var digest = Convert.ToHexString(SHA256.HashData(stream));
            return ReadStamp(Path) == expected &&
                string.Equals(digest, Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
