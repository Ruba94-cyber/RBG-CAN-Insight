using RbgCanInsight.Domain;

namespace RbgCanInsight.Dbc;

/// <summary>Per-database matching options: bus assignment and J1939 PGN matching.</summary>
public sealed record DbcDecoderOptions(bool MatchByPgn = false, int? Channel = null)
{
    public static readonly DbcDecoderOptions Default = new();
}

/// <summary>Decodes raw CAN frames into physical signal values using a DBC database.</summary>
public sealed class DbcDecoder
{
    private readonly DbcDatabase _database;
    private readonly DbcDecoderOptions _options;
    private readonly Dictionary<uint, DbcMessage>? _byPgn;
    private readonly HashSet<uint> _ambiguousPgns = [];
    // Compiled once: the common non-multiplexed frame must not allocate selector
    // dictionaries / HashSets for each of millions of received CAN frames.
    // Compile mux names/roots once per imported DBC. A multi-million-frame
    // trace must not group selector metadata for every recorded frame.
    private readonly Dictionary<DbcMessage, MuxPlan> _muxPlans =
        new(ReferenceEqualityComparer.Instance);
    // A malformed imported message with duplicate signal names is ambiguous:
    // its two different values would otherwise collapse into one named plot
    // timeline. Decode the unaffected signals, not a first/last winner.
    private readonly Dictionary<DbcMessage, HashSet<string>> _ambiguousSignals =
        new(ReferenceEqualityComparer.Instance);

    private sealed record CompiledMuxGroup(
        string Controller, DbcMuxRange[] Ranges);

    private sealed class MuxPlan
    {
        public MuxPlan(DbcMessage message)
        {
            var groups = message.Signals.Where(signal => signal.IsMultiplexor)
                .GroupBy(signal => signal.Name, StringComparer.Ordinal).ToArray();
            Ambiguous = groups.Where(group => group.Count() != 1)
                .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
            Selectors = groups.Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(),
                    StringComparer.Ordinal);
            Roots = Selectors.Values.Where(signal =>
                signal.MultiplexValue is null &&
                signal.MultiplexConditions is not { Count: > 0 }).ToArray();

            var compiled = new Dictionary<DbcSignal, CompiledMuxGroup[]>(
                ReferenceEqualityComparer.Instance);
            foreach (var signal in message.Signals.Where(signal =>
                         signal.MultiplexConditions is { Count: > 0 }))
            {
                compiled[signal] = signal.MultiplexConditions!
                    .GroupBy(condition => condition.Multiplexor,
                        StringComparer.Ordinal)
                    .Select(group => new CompiledMuxGroup(group.Key,
                        group.SelectMany(item => item.Ranges).ToArray()))
                    .ToArray();
            }
            Conditions = compiled;
        }

        public IReadOnlyDictionary<string, DbcSignal> Selectors { get; }
        public IReadOnlySet<string> Ambiguous { get; }
        public IReadOnlyList<DbcSignal> Roots { get; }
        public IReadOnlyDictionary<DbcSignal, CompiledMuxGroup[]> Conditions { get; }
    }

    public DbcDecoder(DbcDatabase database)
        : this(database, DbcDecoderOptions.Default)
    {
    }

    public DbcDecoder(DbcDatabase database, DbcDecoderOptions options)
    {
        _database = database;
        _options = options;
        foreach (var msg in database.AllMessageDefinitions)
        {
            var duplicates = msg.Signals
                .GroupBy(signal => signal.Name, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.Ordinal);
            if (duplicates.Count > 0)
                _ambiguousSignals.TryAdd(msg, duplicates);
            if (msg.Signals.Any(signal => signal.IsMultiplexor ||
                signal.MultiplexValue is not null ||
                signal.MultiplexConditions is { Count: > 0 }))
                _muxPlans.TryAdd(msg, new MuxPlan(msg));
        }
        if (options.MatchByPgn)
        {
            _byPgn = [];
            foreach (var message in database.AllMessageDefinitions.Where(item => item.IsExtended))
            {
                var pgn = J1939.ExtractPgn(message.Identifier);
                if (_ambiguousPgns.Contains(pgn)) continue;
                if (!_byPgn.TryAdd(pgn, message))
                {
                    // Several DBC messages claim the same PGN. Never pick
                    // whichever was loaded first for an unrelated source ECU.
                    _ambiguousPgns.Add(pgn);
                    _byPgn.Remove(pgn);
                }
            }
        }
    }

    public DbcDatabase Database => _database;
    public DbcDecoderOptions Options => _options;

    /// <summary>
    /// Resolves the DBC message for a frame, honouring the channel assignment and, when enabled,
    /// J1939 PGN matching (so the same PG sent by different source addresses still matches).
    /// </summary>
    public bool TryGetMessage(in CanFrame frame, out DbcMessage message) =>
        TryGetMessage(frame.Channel, frame.Identifier, frame.IsExtended, out message);

    public bool TryGetMessage(int channel, uint identifier, bool isExtended, out DbcMessage message)
    {
        message = null!;
        if (_options.Channel is { } assigned && channel != assigned)
        {
            return false;
        }

        if (_database.TryGetMessage(identifier, isExtended, out var exact))
        {
            message = exact;
            return true;
        }

        if (_byPgn is not null && isExtended &&
            _byPgn.TryGetValue(J1939.ExtractPgn(identifier), out var byPgn))
        {
            message = byPgn;
            return true;
        }

        return false;
    }

    public IReadOnlyList<SignalValue> Decode(CanFrame frame)
    {
        // Error objects and remote requests are not data frames: decoding their
        // buffer as if it were a transmitted ECU payload creates false evidence.
        if (frame.IsError || frame.Flags.HasFlag(CanFrameFlags.Remote))
            return [];
        if (!TryGetMessage(frame, out var message))
        {
            return [];
        }

        var values = new List<SignalValue>(message.Signals.Count);
        if (!_muxPlans.TryGetValue(message, out var plan))
        {
            foreach (var signal in message.Signals)
            {
                if (_ambiguousSignals.TryGetValue(message, out var ambiguous) &&
                    ambiguous.Contains(signal.Name))
                    continue;
                if (CanExtract(frame, signal) &&
                    TryCreateValue(frame, message, signal, out var value))
                    values.Add(value);
            }
            return values;
        }
        // Nested/multiple multiplexors use a precompiled per-message plan.
        // A duplicate controller is excluded conservatively rather than
        // choosing the first occurrence or aborting the offline analysis.
        var multiplexors = plan.Selectors;
        var rawSelectors = new Dictionary<string, SelectorCode>(
            multiplexors.Count, StringComparer.Ordinal);
        foreach (var (name, selector) in multiplexors)
        {
            // An IEEE-754 selector's bit pattern is not an integer branch ID.
            if (selector.ValueType == DbcSignalValueType.Integer &&
                CanExtract(frame, selector))
                rawSelectors.Add(name, SelectorCode.FromRaw(
                    ExtractRaw(frame, selector), selector));
        }
        var rootSwitches = plan.Roots;

        bool Active(DbcSignal signal, HashSet<string> visiting)
        {
            if (!visiting.Add(signal.Name)) return false; // broken cyclic mux declarations
            try
            {
                if (plan.Conditions.TryGetValue(signal, out var conditions))
                {
                    // Same-controller ranges are OR alternatives; separate
                    // controller dependencies remain AND, precompiled once.
                    foreach (var group in conditions)
                    {
                        if (!multiplexors.TryGetValue(group.Controller, out var selector) ||
                            !rawSelectors.TryGetValue(group.Controller, out var rawValue) ||
                            !Active(selector, visiting))
                            return false;
                        var matches = false;
                        foreach (var range in group.Ranges)
                        {
                            if (!rawValue.Matches(range)) continue;
                            matches = true;
                            break;
                        }
                        if (!matches) return false;
                    }
                }
                else if (signal.MultiplexValue is { } expected)
                {
                    if (expected < 0 || rootSwitches.Count != 1) return false;
                    var root = rootSwitches[0];
                    if (!rawSelectors.TryGetValue(root.Name, out var raw) ||
                        !Active(root, visiting) || !raw.MatchesBranch(expected))
                        return false;
                }
                return true;
            }
            finally
            {
                visiting.Remove(signal.Name);
            }
        }

        // One cycle-detection set per frame instead of allocating a new set
        // for every signal in every imported CAN frame.
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var signal in message.Signals)
        {
            if (_ambiguousSignals.TryGetValue(message, out var ambiguous) &&
                    ambiguous.Contains(signal.Name) ||
                signal.IsMultiplexor && plan.Ambiguous.Contains(signal.Name) ||
                !CanExtract(frame, signal))
                continue;
            visiting.Clear();
            if (!Active(signal, visiting))
                continue;

            if (TryCreateValue(frame, message, signal, out var value))
                values.Add(value);
        }

        return values;
    }

    /// <summary>Exact mux comparison. Signed DBC controllers use signed
    /// two's-complement codes; unsigned ones may span the full UInt64 range.
    /// Never compare a signed negative selector as an unsigned raw bit pattern.</summary>
    private readonly record struct SelectorCode(ulong Unsigned, long Signed, bool IsSigned)
    {
        public static SelectorCode FromRaw(ulong raw, DbcSignal signal) =>
            new(raw, signal.IsSigned ? SignExtend(raw, signal.BitLength) : 0,
                signal.IsSigned);

        public bool Matches(DbcMuxRange range)
        {
            if (range.Maximum < range.Minimum) return false;
            return IsSigned
                ? Signed >= range.Minimum && Signed <= range.Maximum
                : range.Minimum >= 0 &&
                  Unsigned >= (ulong)range.Minimum &&
                  Unsigned <= (ulong)range.Maximum;
        }

        public bool MatchesBranch(int branch) => branch >= 0 &&
            (IsSigned ? Signed == branch : Unsigned == (ulong)branch);
    }

    private bool TryCreateValue(CanFrame frame, DbcMessage message, DbcSignal signal,
        out SignalValue value)
    {
        value = default;
        var rawUnsigned = ExtractRaw(frame, signal);
        var raw = DecodeRawValue(rawUnsigned, signal);
        if (!signal.TryGetPhysicalValue(raw, out var physical))
            return false;
        value = new SignalValue(
            _database.Name, message.Name, signal.Name,
            raw, physical, signal.Unit,
            frame.TimestampNanoseconds, frame.Channel, frame.Identifier);
        return true;
    }

    private static ulong ExtractRaw(CanFrame frame, DbcSignal signal) =>
        signal.ByteOrder == DbcByteOrder.Intel
            ? ExtractIntel(frame, signal.StartBit, signal.BitLength)
            : ExtractMotorola(frame, signal.StartBit, signal.BitLength);

    private static double DecodeRawValue(ulong rawUnsigned, DbcSignal signal) =>
        signal.ValueType switch
        {
            DbcSignalValueType.Float32 when signal.BitLength == 32 =>
                BitConverter.Int32BitsToSingle(unchecked((int)(uint)rawUnsigned)),
            DbcSignalValueType.Float64 when signal.BitLength == 64 =>
                BitConverter.Int64BitsToDouble(unchecked((long)rawUnsigned)),
            _ => signal.IsSigned ? SignExtend(rawUnsigned, signal.BitLength) : rawUnsigned
        };

    private static bool CanExtract(CanFrame frame, DbcSignal signal)
    {
        if (signal.BitLength is <= 0 or > 64 || signal.StartBit < 0)
            return false;

        if (signal.ByteOrder == DbcByteOrder.Intel)
        {
            // Subtraction avoids overflow when malformed DBC StartBit is
            // close to Int32.MaxValue. An overflowed sum must never let a
            // bogus signal reach GetByte and abort the entire trace.
            return signal.StartBit <= frame.DataLength * 8 - signal.BitLength;
        }

        var bitPosition = signal.StartBit;
        for (var index = 0; index < signal.BitLength; index++)
        {
            if (bitPosition < 0 || bitPosition >= frame.DataLength * 8)
            {
                return false;
            }

            bitPosition = bitPosition % 8 == 0 ? bitPosition + 15 : bitPosition - 1;
        }

        return true;
    }

    private static ulong ExtractIntel(in CanFrame frame, int startBit, int length)
    {
        ValidateRange(frame, startBit, length);
        ulong value = 0;
        for (var index = 0; index < length; index++)
        {
            var sourceBit = startBit + index;
            var bit = (frame.GetByte(sourceBit / 8) >> (sourceBit % 8)) & 1;
            value |= (ulong)bit << index;
        }

        return value;
    }

    private static ulong ExtractMotorola(in CanFrame frame, int startBit, int length)
    {
        if (length is <= 0 or > 64)
        {
            throw new InvalidOperationException("DBC signal length must be between 1 and 64.");
        }

        ulong value = 0;
        var bitPosition = startBit;
        for (var index = 0; index < length; index++)
        {
            if (bitPosition < 0 || bitPosition >= frame.DataLength * 8)
            {
                throw new InvalidOperationException("DBC signal exceeds the CAN payload.");
            }

            var bit = (frame.GetByte(bitPosition / 8) >> (bitPosition % 8)) & 1;
            value = (value << 1) | (uint)bit;
            bitPosition = bitPosition % 8 == 0 ? bitPosition + 15 : bitPosition - 1;
        }

        return value;
    }

    private static void ValidateRange(in CanFrame frame, int startBit, int length)
    {
        if (startBit < 0 || length is <= 0 or > 64 ||
            startBit > frame.DataLength * 8 - length)
        {
            throw new InvalidOperationException("DBC signal exceeds the CAN payload.");
        }
    }

    private static long SignExtend(ulong value, int length)
    {
        if (length == 64)
        {
            return unchecked((long)value);
        }

        var signBit = 1UL << (length - 1);
        return (long)((value ^ signBit) - signBit);
    }
}
