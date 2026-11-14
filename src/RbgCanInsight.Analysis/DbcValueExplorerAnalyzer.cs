using System.Globalization;
using System.Numerics;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis;

/// <summary>
/// One exact raw CAN-coded integer and its actual observations. Raw bits are
/// never converted to double for equality, grouping or VAL_ lookup.
/// </summary>
public sealed record DbcObservedRawCode(
    ulong EncodedBits,
    string ExactRaw,
    string HexRaw,
    string? DeclaredMeaning,
    long ObservedCount,
    int FirstSourceFrameIndex,
    int LastSourceFrameIndex,
    long FirstTimestampNanoseconds,
    long LastTimestampNanoseconds)
{
    public string Meaning => DeclaredMeaning ?? "Unknown / not declared";
    public string FirstFrameText => (FirstSourceFrameIndex + 1).ToString(CultureInfo.InvariantCulture);
    public string LastFrameText => (LastSourceFrameIndex + 1).ToString(CultureInfo.InvariantCulture);
}

public sealed record DbcValueExplorerReport(
    string TimelineKey,
    string RawDomain,
    string DeclaredPhysicalRange,
    long ObservedSamples,
    long ObservedUnknownRawCodes,
    long ObservedOutsideDeclaredPhysicalRange,
    long ObservedWithoutExactDoubleRawRepresentation,
    IReadOnlyList<DbcObservedRawCode> RawCodes,
    long UntrackedSamplesDueToDistinctLimit,
    long UnavailableOriginalFrames,
    int DeclaredNamedCodes,
    bool IsFloatingPoint,
    string? Warning)
{
    public string Summary =>
        $"{ObservedSamples:N0} source-backed observations · {RawCodes.Count:N0} tracked raw codes" +
        (UntrackedSamplesDueToDistinctLimit > 0
            ? $" · {UntrackedSamplesDueToDistinctLimit:N0} samples with untracked distinct raw codes"
            : "") +
        $" · {ObservedUnknownRawCodes:N0} unnamed code observations" +
        $" · {ObservedOutsideDeclaredPhysicalRange:N0} outside declared DBC physical limits";
}

/// <summary>
/// On-demand, source-scoped histogram of exact recorded raw values for a
/// decoded DBC signal. Reuses timeline frame indices (including multiplex
/// branch gating), but re-reads raw bit positions from the original CAN frame
/// instead of grouping rounded double signal samples. This is a read-only,
/// bounded analysis; distinct-code limits never claim global completeness.
/// </summary>
public static class DbcValueExplorerAnalyzer
{
    public const int MaxTrackedDistinctRawCodes = 4096;

    private sealed class Accumulator(ulong raw, string exact, string hex,
        string? label, SignalSample first)
    {
        public ulong Raw { get; } = raw;
        public string Exact { get; } = exact;
        public string Hex { get; } = hex;
        public string? Label { get; } = label;
        public long Count { get; set; } = 1;
        public SignalSample First { get; } = first;
        public SignalSample Last { get; set; } = first;
    }

    public static DbcValueExplorerReport Analyze(
        SignalTimeline timeline,
        IReadOnlyList<CanFrame> sourceFrames,
        int maxDistinctCodes = 512,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(sourceFrames);
        if (maxDistinctCodes is < 0 or > MaxTrackedDistinctRawCodes)
            throw new ArgumentOutOfRangeException(nameof(maxDistinctCodes));
        cancellationToken.ThrowIfCancellationRequested();
        var signal = timeline.Signal;
        var domain = BuildRawDomain(signal);
        var physical = $"{signal.Minimum.ToString("G17", CultureInfo.InvariantCulture)} .. " +
            $"{signal.Maximum.ToString("G17", CultureInfo.InvariantCulture)}" +
            (string.IsNullOrWhiteSpace(signal.Unit) ? "" : " " + signal.Unit) +
            " (DBC-declared, not an observed limit)";

        if (signal.BitLength is < 1 or > 64 ||
            signal.StartBit < 0 ||
            signal.ValueType == DbcSignalValueType.Float32 && signal.BitLength != 32 ||
            signal.ValueType == DbcSignalValueType.Float64 && signal.BitLength != 64)
            return new DbcValueExplorerReport(timeline.Key, domain, physical,
                timeline.Samples.Count, 0, 0, 0, [], 0, timeline.Samples.Count,
                signal.ValueDescriptions?.Count ?? 0,
                signal.ValueType != DbcSignalValueType.Integer,
                "The source signal has an unsupported raw layout; no bits were guessed.");

        var bits = DbcSignalInspector.PhysicalBits(signal);
        if (bits.Count != signal.BitLength ||
            bits.Any(position => position is < 0 or >= 512))
            return new DbcValueExplorerReport(timeline.Key, domain, physical,
                timeline.Samples.Count, 0, 0, 0, [], 0, timeline.Samples.Count,
                signal.ValueDescriptions?.Count ?? 0,
                signal.ValueType != DbcSignalValueType.Integer,
                "Bit positions exceed the maximum CAN-FD 64-byte payload.");

        // All signal bit positions are fixed for the selected DBC definition.
        // Check the highest required byte once, not with a LINQ allocation on
        // every source frame of a multi-million-row trace.
        var highestRequiredByte = bits.Max() / 8;
        var accumulators = new Dictionary<ulong, Accumulator>();
        long missing = 0, untracked = 0, unknown = 0, outside = 0, rounded = 0;
        var samples = timeline.Samples;
        for (var index = 0; index < samples.Count; index++)
        {
            if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var sample = samples[index];
            if (sample.FrameIndex < 0 || sample.FrameIndex >= sourceFrames.Count)
            {
                missing++;
                continue;
            }
            var frame = sourceFrames[sample.FrameIndex];
            if (frame.Channel != timeline.Channel ||
                frame.IsExtended != timeline.Message.IsExtended ||
                frame.IsError || frame.Flags.HasFlag(CanFrameFlags.Remote) ||
                highestRequiredByte >= frame.DataLength)
            {
                missing++;
                continue;
            }
            // J1939 PGN matching may legitimately use another source address,
            // but it must not accept an unrelated recorded 29-bit PGN.
            if (frame.Identifier != timeline.Message.Identifier &&
                !(frame.IsExtended && timeline.Message.IsExtended &&
                  J1939.ExtractPgn(frame.Identifier) ==
                  J1939.ExtractPgn(timeline.Message.Identifier)))
            {
                missing++;
                continue;
            }

            ulong encoded = 0;
            if (signal.ByteOrder == DbcByteOrder.Intel)
            {
                for (var bitIndex = 0; bitIndex < bits.Count; bitIndex++)
                {
                    var position = bits[bitIndex];
                    encoded |= (ulong)((frame.GetByte(position / 8) >>
                        (position % 8)) & 1) << bitIndex;
                }
            }
            else
            {
                foreach (var position in bits)
                    encoded = (encoded << 1) |
                        (uint)((frame.GetByte(position / 8) >> (position % 8)) & 1);
            }

            if (signal.IsOutsideDeclaredRange(sample.Value))
                outside++;

            string code;
            string? name;
            if (signal.ValueType != DbcSignalValueType.Integer)
            {
                code = "IEEE-754 encoded bits";
                name = null; // VAL_ codes are integer keys, not floating states.
            }
            else if (signal.IsSigned)
            {
                var signed = signal.BitLength == 64 ? unchecked((long)encoded)
                    : (long)((encoded ^ (1UL << (signal.BitLength - 1))) -
                        (1UL << (signal.BitLength - 1)));
                code = signed.ToString(CultureInfo.InvariantCulture);
                name = signal.GetValueDescription(signed);
                if (signed < -9_007_199_254_740_991L ||
                    signed > 9_007_199_254_740_991L) rounded++;
            }
            else
            {
                code = encoded.ToString(CultureInfo.InvariantCulture);
                name = encoded <= long.MaxValue
                    ? signal.GetValueDescription((long)encoded) : null;
                if (encoded > 9_007_199_254_740_991UL) rounded++;
            }

            if (signal.ValueType == DbcSignalValueType.Integer &&
                signal.ValueDescriptions is { Count: > 0 } && name is null)
                unknown++;

            if (accumulators.TryGetValue(encoded, out var bucket))
            {
                bucket.Count++;
                bucket.Last = sample;
            }
            else if (accumulators.Count < maxDistinctCodes)
            {
                var hexDigits = (signal.BitLength + 3) / 4;
                accumulators.Add(encoded, new Accumulator(encoded, code,
                    "0x" + encoded.ToString("X" + hexDigits.ToString(
                        CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                    name, sample));
            }
            else
                untracked++;
        }
        cancellationToken.ThrowIfCancellationRequested();

        var values = accumulators.Values
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Raw)
            .Select(item => new DbcObservedRawCode(item.Raw, item.Exact,
                item.Hex, item.Label, item.Count,
                item.First.FrameIndex, item.Last.FrameIndex,
                item.First.TimestampNanoseconds, item.Last.TimestampNanoseconds))
            .ToArray();
        var notices = new List<string>();
        if (signal.ValueType != DbcSignalValueType.Integer)
            notices.Add("Floating-point values are grouped by exact IEEE-754 bit pattern, not integer VAL_ codes.");
        if (signal.PhysicalConversionUnsupported || signal.UsesRangeDependentScale)
            notices.Add("Only successfully decoded physical samples appear; raw-only or ambiguous scale samples are not represented.");
        if (untracked > 0)
            notices.Add("Distinct raw code limit reached. Tracked raw-code rows are not a complete histogram.");
        if (missing > 0)
            notices.Add("Some samples could not be reconciled with an original frame; those observations are not assigned a raw code.");
        return new DbcValueExplorerReport(timeline.Key, domain, physical,
            samples.Count, unknown, outside, rounded, values, untracked,
            missing, signal.ValueDescriptions?.Count ?? 0,
            signal.ValueType != DbcSignalValueType.Integer,
            notices.Count == 0 ? null : string.Join(" ", notices));
    }

    private static string BuildRawDomain(DbcSignal signal)
    {
        if (signal.ValueType != DbcSignalValueType.Integer)
            return $"IEEE-754 {signal.ValueType}; bit-pattern codes are not signed integers.";
        if (signal.BitLength is < 1 or > 64)
            return "Invalid DBC bit length: raw domain unsupported.";
        var exponent = signal.IsSigned ? signal.BitLength - 1 : signal.BitLength;
        var upper = (BigInteger.One << exponent) - BigInteger.One;
        var lower = signal.IsSigned ? -(BigInteger.One << exponent) : BigInteger.Zero;
        return $"{lower} .. {upper} ({signal.BitLength}-bit " +
            (signal.IsSigned ? "signed" : "unsigned") + " raw codes)";
    }
}
