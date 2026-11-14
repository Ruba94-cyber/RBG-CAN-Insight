using System.Globalization;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis;

/// <summary>
/// Portable, versioned reference to one original physical CAN frame. The
/// source file's full SHA-256 plus the import-order sequence survives trace
/// sorting, replay filtering and database reload. No payload is embedded.
/// </summary>
public readonly record struct FrameEvidenceIdentity(
    string SourceSha256, long SourceSequence,
    long TimestampNanoseconds, int Channel,
    uint Identifier, bool IsExtended)
{
    public const string Version = "RBGFRAME1";

    // Keep one interpolated handler expression: concatenating two completed
    // strings before string.Create would lose the .NET 8 overload's handler
    // conversion and can fail compilation.
    public string Token => string.Create(CultureInfo.InvariantCulture,
        $"{Version}:{SourceSha256}:{SourceSequence}:{TimestampNanoseconds}:{Channel}:{(IsExtended ? 'E' : 'S')}:{Identifier:X8}");

    public static FrameEvidenceIdentity FromFrame(string sourceSha256, in CanFrame frame)
    {
        if (!ValidSha256(sourceSha256))
            throw new ArgumentException("The source file SHA-256 must be 64 hexadecimal characters.",
                nameof(sourceSha256));
        if (frame.Sequence <= 0)
            throw new ArgumentException("Original import-order sequence is required.", nameof(frame));
        return new FrameEvidenceIdentity(sourceSha256.ToUpperInvariant(),
            frame.Sequence, frame.TimestampNanoseconds,
            frame.Channel, frame.Identifier, frame.IsExtended);
    }

    public bool Matches(in CanFrame frame) =>
        frame.Sequence == SourceSequence &&
        frame.TimestampNanoseconds == TimestampNanoseconds &&
        frame.Channel == Channel && frame.Identifier == Identifier &&
        frame.IsExtended == IsExtended;

    public static bool TryParse(string? token, out FrameEvidenceIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split(':');
        if (parts.Length != 7 || parts[0] != Version || !ValidSha256(parts[1]) ||
            !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seq) ||
            seq <= 0 ||
            !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var time) ||
            !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var channel) ||
            channel < 0 ||
            parts[5] is not ("S" or "E") ||
            !uint.TryParse(parts[6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) ||
            id > (parts[5] == "E" ? 0x1FFF_FFFFu : 0x7FFu))
            return false;
        identity = new FrameEvidenceIdentity(parts[1].ToUpperInvariant(),
            seq, time, channel, id, parts[5] == "E");
        return true;
    }

    /// <summary>Resolves by original source sequence, never by filtered or
    /// current visual row index. The caller verifies the expected file hash.
    /// Optimized source-sequence indexes can be built by the UI for repeated lookups.</summary>
    public bool TryResolve(string? currentSourceSha256,
        IReadOnlyList<CanFrame> chronologicalFrames, out int index)
    {
        index = -1;
        if (!ValidSha256(currentSourceSha256) ||
            !string.Equals(SourceSha256, currentSourceSha256, StringComparison.OrdinalIgnoreCase))
            return false;
        for (var i = 0; i < chronologicalFrames.Count; i++)
        {
            var frame = chronologicalFrames[i];
            if (frame.Sequence != SourceSequence) continue;
            if (!Matches(frame)) return false;
            index = i;
            return true;
        }
        return false;
    }

    private static bool ValidSha256(string? sha) =>
        sha is { Length: 64 } && sha.All(Uri.IsHexDigit);
}
