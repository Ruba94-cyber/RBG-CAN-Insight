using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis;

/// <summary>
/// Estimates classic-CAN bus load from a trace and an assumed bitrate. Offline captures do not
/// generally carry the bus bitrate as file metadata (BLF/MF4 sometimes do, but only for the
/// specific vendor/tool that wrote them, and getting that wrong would silently produce a bus-load
/// number that looks precise but is not) — the bitrate is therefore always an explicit,
/// user-visible input rather than something silently assumed.
///
/// The per-frame bit count is the standard nominal count before bit stuffing (SOF, arbitration,
/// control, data, CRC, ACK, EOF, IFS) — the same simplification most offline analysis tools use,
/// since the actual stuffed bit count depends on the exact bit pattern and cannot be recovered
/// from a decoded trace alone. CAN FD frames are estimated at the single (arbitration-phase)
/// bitrate given, which understates load for BRS frames that switch to a faster data-phase rate —
/// documented here rather than silently modeled, since guessing a second data-phase rate would be
/// its own source of illusory precision.
/// </summary>
public static class BusLoadCalculator
{
    private const int StandardFrameOverheadBits = 47;
    private const int ExtendedFrameOverheadBits = 67;

    /// <summary>Nominal (unstuffed) bit count for one frame on the wire.</summary>
    public static int EstimateFrameBits(in CanFrame frame)
    {
        var dataBits = 8 * frame.DataLength;
        return (frame.IsExtended ? ExtendedFrameOverheadBits : StandardFrameOverheadBits) + dataBits;
    }

    /// <summary>Bus load percent for a set of same-channel frames spanning
    /// <paramref name="spanNanoseconds"/> at the given assumed bitrate. Clamped to [0, 100] —
    /// bit-stuffing and back-to-back retransmissions mean the true wire time can occasionally
    /// exceed the unstuffed estimate.</summary>
    public static double ComputeLoadPercent(
        IEnumerable<CanFrame> channelFrames, long spanNanoseconds, double bitrateBitsPerSecond)
    {
        if (spanNanoseconds <= 0 || bitrateBitsPerSecond <= 0)
        {
            return 0;
        }

        long totalBits = 0;
        foreach (var frame in channelFrames)
        {
            totalBits += EstimateFrameBits(frame);
        }

        return ComputeLoadPercent(totalBits, spanNanoseconds, bitrateBitsPerSecond);
    }

    /// <summary>Same estimate from an already accumulated nominal bit total, so a caller can scan
    /// the trace once and never keep a per-channel copy of the frames.</summary>
    public static double ComputeLoadPercent(
        long totalBits, long spanNanoseconds, double bitrateBitsPerSecond)
    {
        if (spanNanoseconds <= 0 || bitrateBitsPerSecond <= 0)
        {
            return 0;
        }

        var busyTimeSeconds = totalBits / bitrateBitsPerSecond;
        var spanSeconds = spanNanoseconds / 1_000_000_000d;
        return Math.Clamp(busyTimeSeconds / spanSeconds * 100d, 0, 100);
    }
}
