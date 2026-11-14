namespace RbgCanInsight.Domain;

/// <summary>
/// CAN-FD data length code (DLC) mapping. Classic CAN allows 0..8 bytes; CAN-FD adds the discrete
/// steps 12, 16, 20, 24, 32, 48, 64.
/// </summary>
public static class CanFdLength
{
    private static readonly int[] FdLengths = [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64];

    /// <summary>True if <paramref name="byteCount"/> is a valid CAN-FD payload length.</summary>
    public static bool IsValidFdLength(int byteCount) => Array.IndexOf(FdLengths, byteCount) >= 0;

    /// <summary>Rounds a byte count up to the next valid CAN-FD payload length (max 64).</summary>
    public static int RoundUpToFdLength(int byteCount)
    {
        if (byteCount <= 0)
        {
            return 0;
        }

        foreach (var length in FdLengths)
        {
            if (length >= byteCount)
            {
                return length;
            }
        }

        return 64;
    }

    /// <summary>The 4-bit DLC field value for a given payload byte count (FD encoding).</summary>
    public static int ToDlc(int byteCount)
    {
        var rounded = RoundUpToFdLength(byteCount);
        return rounded <= 8 ? rounded : Array.IndexOf(FdLengths, rounded);
    }

    /// <summary>The payload byte count for a 4-bit DLC field value (FD decoding).</summary>
    public static int FromDlc(int dlc)
    {
        if (dlc is < 0 or > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(dlc));
        }

        return FdLengths[dlc];
    }
}
