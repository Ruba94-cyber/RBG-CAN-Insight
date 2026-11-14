namespace RbgCanInsight.Dbc;

public enum DbcEngineeringSeverity
{
    Error,
    Warning,
    Review
}

/// <summary>A definition-only finding; no claim about observed CAN traffic.</summary>
public sealed record DbcEngineeringFinding(
    DbcEngineeringSeverity Severity,
    string Signal,
    string Code,
    string Detail);

/// <summary>
/// Independent, explainable engineering checks for one DBC message.
/// Multiplexing is treated conservatively: disjoint known branches can
/// reuse bits; unknown or nested variants are flagged for review, not as
/// definitively conflicting. No mutation or ECU interaction.
/// </summary>
public static class DbcEngineeringValidator
{
    private const int MaxFindings = 256;

    public static IReadOnlyList<DbcEngineeringFinding> Validate(DbcMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var findings = new List<DbcEngineeringFinding>();
        void Report(DbcEngineeringSeverity severity, string signal, string code, string detail)
        {
            if (findings.Count < MaxFindings)
                findings.Add(new DbcEngineeringFinding(severity, signal, code, detail));
        }

        if (message.IsExtended && message.Identifier > 0x1FFF_FFFFu ||
            !message.IsExtended && message.Identifier > 0x7FFu)
            Report(DbcEngineeringSeverity.Error, "(message)", "CAN_ID_RANGE",
                "Identifier is outside its declared 11-bit or 29-bit arbitration range.");
        if (message.Length is < 0 or > 64)
            Report(DbcEngineeringSeverity.Error, "(message)", "DLC_RANGE",
                "CAN payload length must be within 0..64 bytes.");
        else if (message.Length > 8)
            Report(DbcEngineeringSeverity.Review, "(message)", "CAN_FD_REQUIRED",
                "Payload exceeds eight bytes; verify that the physical capture uses CAN FD.");

        var duplicateNames = message.Signals.GroupBy(signal => signal.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1);
        foreach (var group in duplicateNames)
            Report(DbcEngineeringSeverity.Error, group.Key, "DUPLICATE_SIGNAL",
                "Signal names are duplicated within the same message.");

        var bitOwners = new Dictionary<int, List<int>>();
        for (var index = 0; index < message.Signals.Count; index++)
        {
            var signal = message.Signals[index];
            if (signal.StartBit < 0 || signal.BitLength is < 1 or > 64)
            {
                Report(DbcEngineeringSeverity.Error, signal.Name, "BIT_LENGTH",
                    "Start bit must be nonnegative and signal width 1..64.");
                continue;
            }
            if (signal.ValueType is DbcSignalValueType.Float32 && signal.BitLength != 32 ||
                signal.ValueType is DbcSignalValueType.Float64 && signal.BitLength != 64)
                Report(DbcEngineeringSeverity.Error, signal.Name, "FLOAT_WIDTH",
                    "Floating point encoding requires a matching 32/64-bit payload.");
            if (!double.IsFinite(signal.Factor) || !double.IsFinite(signal.Offset) ||
                !double.IsFinite(signal.Minimum) || !double.IsFinite(signal.Maximum))
                Report(DbcEngineeringSeverity.Error, signal.Name, "NONFINITE_SCALE",
                    "Factor, offset and physical limits must be finite.");
            if (signal.PhysicalConversionUnsupported)
                Report(DbcEngineeringSeverity.Review, signal.Name, "SOURCE_CONVERSION_RAW_ONLY",
                    "ARXML scaling is unsupported or internally inconsistent; physical measurements are suppressed.");
            else if (signal.UsesRangeDependentScale)
                Report(DbcEngineeringSeverity.Review, signal.Name, "SOURCE_SEGMENTED_CONVERSION",
                    "ARXML COMPU-SCALE uses raw-value intervals; gaps and conflicting overlaps remain raw-only.");
            if (signal.Factor == 0 && !signal.PhysicalConversionUnsupported &&
                !signal.UsesRangeDependentScale)
                Report(DbcEngineeringSeverity.Error, signal.Name, "ZERO_FACTOR",
                    "Scaling factor zero collapses raw measurements.");
            if (signal.Minimum > signal.Maximum)
                Report(DbcEngineeringSeverity.Error, signal.Name, "PHYSICAL_RANGE",
                    "Declared physical minimum exceeds maximum.");

            var physicalBits = DbcSignalInspector.PhysicalBits(signal);
            if (physicalBits.Count() != signal.BitLength || physicalBits.Any(bit => bit >= message.Length * 8 || bit < 0))
            {
                Report(DbcEngineeringSeverity.Error, signal.Name, "OUTSIDE_DLC",
                    $"Signal occupies bits outside the {message.Length}-byte message.");
            }
            else
            {
                foreach (var bit in physicalBits)
                {
                    if (!bitOwners.TryGetValue(bit, out var owners))
                        bitOwners[bit] = owners = [];
                    owners.Add(index);
                }
            }
            if (signal.MultiplexValue is { } branch &&
                signal.MultiplexConditions is not { Count: > 0 })
            {
                var roots = message.Signals.Where(candidate =>
                    candidate.IsMultiplexor &&
                    candidate.MultiplexValue is null &&
                    candidate.MultiplexConditions is not { Count: > 0 })
                    .ToArray();
                if (roots.Length != 1)
                    Report(DbcEngineeringSeverity.Review, signal.Name,
                        "MUX_ROOT_UNRESOLVED",
                        "Classic mN branch requires exactly one unconditioned " +
                        "root multiplexor in this message.");
                else if (roots[0].ValueType != DbcSignalValueType.Integer)
                    Report(DbcEngineeringSeverity.Review, signal.Name,
                        "MUX_ROOT_FLOAT",
                        "Classic mN branch cannot be selected by an IEEE-754 root.");
                else if (branch < 0 || !ValidEnumValue(roots[0], branch))
                    Report(DbcEngineeringSeverity.Review, signal.Name,
                        "MUX_BRANCH_OUT_OF_BOUNDS",
                        $"Classic branch m{branch} is not representable by " +
                        $"root selector '{roots[0].Name}' ({roots[0].BitLength} bits).");
            }
            if (signal.MultiplexConditions is { Count: > 0 } conditions)
            {
                foreach (var condition in conditions)
                {
                    var controller = message.Signals.Where(candidate =>
                        candidate.Name == condition.Multiplexor).ToArray();
                    if (controller.Length != 1)
                        Report(DbcEngineeringSeverity.Review, signal.Name,
                            "MUX_CONTROLLER_MISSING",
                            $"Extended multiplex condition references '{condition.Multiplexor}', " +
                            "but there is not exactly one matching signal in this message.");
                    else if (!controller[0].IsMultiplexor)
                        Report(DbcEngineeringSeverity.Review, signal.Name,
                            "MUX_CONTROLLER_NOT_SELECTOR",
                            $"Extended multiplex condition references '{condition.Multiplexor}', " +
                            "which is not declared as a multiplexor.");
                    else if (controller[0].ValueType != DbcSignalValueType.Integer)
                        Report(DbcEngineeringSeverity.Review, signal.Name,
                            "MUX_CONTROLLER_FLOAT",
                            $"Selector '{condition.Multiplexor}' uses IEEE-754 raw coding " +
                            "and cannot safely select integer multiplex ranges.");

                    if (condition.Ranges.Count == 0 || condition.Ranges.Any(range =>
                            range.Maximum < range.Minimum ||
                            (controller.Length == 1 &&
                             !controller[0].IsSigned && range.Minimum < 0)))
                        Report(DbcEngineeringSeverity.Review, signal.Name,
                            "MUX_RANGE_INVALID",
                            "Extended multiplex ranges must be nonempty, nonnegative " +
                            "and have a maximum not smaller than the minimum.");
                    else if (controller.Length == 1 && controller[0].IsMultiplexor &&
                        controller[0].ValueType == DbcSignalValueType.Integer &&
                        condition.Ranges.Any(range => !ValidEnumValue(controller[0], range.Minimum) ||
                            !ValidEnumValue(controller[0], range.Maximum)))
                        Report(DbcEngineeringSeverity.Review, signal.Name,
                            "MUX_RANGE_OUT_OF_BOUNDS",
                            $"Extended multiplex range exceeds the coded width of " +
                            $"selector '{condition.Multiplexor}'.");
                }
            }
            foreach (var raw in signal.ValueDescriptions?.Keys ?? [])
            {
                if (!ValidEnumValue(signal, raw))
                    Report(DbcEngineeringSeverity.Warning, signal.Name, "VAL_OUT_OF_RANGE",
                        $"Named raw value {raw} cannot be represented by the declared {signal.BitLength}-bit " +
                        (signal.IsSigned ? "signed" : "unsigned") + " signal.");
            }
        }

        var pairBits = new Dictionary<(int A, int B), int>();
        foreach (var (bit, owners) in bitOwners)
            for (var i = 0; i < owners.Count; i++)
                for (var j = i + 1; j < owners.Count; j++)
                {
                    var pair = (owners[i], owners[j]);
                    if (!pairBits.ContainsKey(pair))
                        pairBits.Add(pair, bit);
                }

        foreach (var ((aIndex, bIndex), bit) in pairBits.OrderBy(pair => pair.Key.A)
                     .ThenBy(pair => pair.Key.B))
        {
            if (findings.Count >= MaxFindings) break;
            var a = message.Signals[aIndex];
            var b = message.Signals[bIndex];
            if (AreKnownExclusive(message, a, b)) continue;
            var hasMultiplexing = IsConditional(a) || IsConditional(b) ||
                a.IsMultiplexor || b.IsMultiplexor;
            Report(hasMultiplexing ? DbcEngineeringSeverity.Review : DbcEngineeringSeverity.Warning,
                a.Name, hasMultiplexing ? "MUX_OVERLAP_REVIEW" : "BIT_OVERLAP",
                $"Payload bit {bit} is used by both {a.Name} and {b.Name}; " +
                (hasMultiplexing
                    ? "nested/conditional mux activation needs review."
                    : "this is a real byte/bit-definition overlap unless independently documented."));
        }

        return findings;
    }

    private static bool IsConditional(DbcSignal signal) =>
        signal.MultiplexValue is not null || signal.MultiplexConditions is { Count: > 0 };

    private static bool AreKnownExclusive(DbcMessage message, DbcSignal a, DbcSignal b)
    {
        // Only one unambiguous simple selector and no extended conditions.
        if (message.Signals.Count(signal => signal.IsMultiplexor) == 1 &&
            a.MultiplexConditions is not { Count: > 0 } &&
            b.MultiplexConditions is not { Count: > 0 } &&
            a.MultiplexValue is { } av && b.MultiplexValue is { } bv &&
            av != bv && !a.IsMultiplexor && !b.IsMultiplexor)
        {
            var controller = message.Signals.Single(signal => signal.IsMultiplexor);
            if (controller.ValueType == DbcSignalValueType.Integer &&
                ValidEnumValue(controller, av) && ValidEnumValue(controller, bv) &&
                av >= 0 && bv >= 0)
                return true;
        }

        // Extended multiplex ranges are provably exclusive only when their
        // exact named controller exists as one valid selector in the message.
        // Missing, duplicate, malformed or out-of-width controllers must not
        // suppress a real overlapping-bit review.
        if (a.MultiplexConditions is { Count: 1 } aConditions &&
            b.MultiplexConditions is { Count: 1 } bConditions &&
            aConditions[0].Multiplexor == bConditions[0].Multiplexor &&
            aConditions[0].Ranges.Count > 0 && bConditions[0].Ranges.Count > 0)
        {
            var selectors = message.Signals.Where(signal =>
                signal.Name == aConditions[0].Multiplexor && signal.IsMultiplexor)
                .ToArray();
            if (selectors.Length != 1 || message.Signals.Count(signal =>
                    signal.Name == selectors[0].Name) != 1)
                return false;
            var selector = selectors[0];
            if (selector.ValueType != DbcSignalValueType.Integer)
                return false;
            var all = aConditions[0].Ranges.Concat(bConditions[0].Ranges).ToArray();
            if (all.Any(range => range.Maximum < range.Minimum ||
                !ValidEnumValue(selector, range.Minimum) ||
                !ValidEnumValue(selector, range.Maximum)))
                return false;
            return aConditions[0].Ranges.All(ar =>
                bConditions[0].Ranges.All(br =>
                    ar.Maximum < br.Minimum || br.Maximum < ar.Minimum));
        }
        return false;
    }

    private static bool ValidEnumValue(DbcSignal signal, long raw)
    {
        if (signal.BitLength is < 1 or > 64) return false;
        if (signal.IsSigned)
        {
            if (signal.BitLength == 64) return true;
            var half = 1L << (signal.BitLength - 1);
            return raw >= -half && raw < half;
        }
        if (raw < 0) return false;
        if (signal.BitLength >= 63) return true;
        return (ulong)raw < (1UL << signal.BitLength);
    }
}
