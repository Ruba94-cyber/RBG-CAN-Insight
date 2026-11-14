using RbgCanInsight.Dbc;

namespace RbgCanInsight.Analysis;

/// <summary>
/// Conservative numeric helpers for graph readouts. Raw CAN bits are not
/// retained by the compact physical-sample timeline, so exact raw values
/// cannot be claimed for unsupported, piecewise or floating-point encodings.
/// The Trace view still exposes original source frames and their raw decode.
/// </summary>
public static class DbcSignalRawMath
{
    /// <summary>Physical value of an all-ones integer raw pattern, if it
    /// can be represented without losing integer precision. This does not
    /// imply an invalid/NA state; that meaning must come from the source DBC.</summary>
    public static double? AllOnesPhysical(DbcSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (!IsSafelyInvertibleInteger(signal) || signal.BitLength < 8)
            return null;

        var raw = signal.IsSigned ? -1d : Math.Pow(2, signal.BitLength) - 1;
        var physical = raw * signal.Factor + signal.Offset;
        // A physical double cannot prove the all-ones pattern when another
        // integer code collapses to the same value after scaling and offset.
        return TryReconstructRaw(signal, physical, out var recovered) && recovered == raw
            ? physical : null;
    }

    /// <summary>Reconstruct a raw integer only for a one-to-one affine DBC
    /// conversion within the signal's signed/unsigned bit range.</summary>
    public static bool TryReconstructRaw(DbcSignal signal, double physical, out double raw)
    {
        raw = double.NaN;
        if (!double.IsFinite(physical) || !IsSafelyInvertibleInteger(signal))
            return false;

        var candidate = (physical - signal.Offset) / signal.Factor;
        if (!double.IsFinite(candidate))
            return false;

        var rounded = Math.Round(candidate);
        var min = signal.IsSigned ? -Math.Pow(2, signal.BitLength - 1) : 0d;
        var max = signal.IsSigned ? Math.Pow(2, signal.BitLength - 1) - 1
            : Math.Pow(2, signal.BitLength) - 1;
        if (rounded < min || rounded > max)
            return false;

        var reconstructed = rounded * signal.Factor + signal.Offset;
        // The timeline stores values produced by this same affine conversion.
        // A tolerance could admit a different code or an unrecorded value.
        if (!double.IsFinite(reconstructed) || reconstructed != physical)
            return false;

        // Floating-point scaling or a large offset can make adjacent raw codes
        // produce the same physical double even when the raw integer itself
        // fits exactly in 53 bits. The affine operation is monotonic, so a
        // collision with any other code implies a collision with a neighbour.
        if ((rounded > min && (rounded - 1) * signal.Factor + signal.Offset == physical) ||
            (rounded < max && (rounded + 1) * signal.Factor + signal.Offset == physical))
            return false;
        raw = rounded;
        return true;
    }

    private static bool IsSafelyInvertibleInteger(DbcSignal signal) =>
        signal is not null &&
        signal.ValueType == DbcSignalValueType.Integer &&
        signal.BitLength is >= 1 and <= 53 &&
        !signal.UsesRangeDependentScale &&
        !signal.PhysicalConversionUnsupported &&
        double.IsFinite(signal.Factor) &&
        double.IsFinite(signal.Offset) &&
        signal.Factor != 0d;
}
