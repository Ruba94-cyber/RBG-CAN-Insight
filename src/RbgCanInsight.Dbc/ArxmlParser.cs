using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace RbgCanInsight.Dbc;

/// <summary>
/// Best-effort AUTOSAR ARXML importer that maps CAN frames and I-SIGNAL mappings into the
/// internal DBC model. It intentionally favours useful offline visibility over failing on
/// vendor-specific AUTOSAR variants.
/// </summary>
public sealed class ArxmlParser
{
    private sealed record SignalDefinition(
        string Name,
        int Length,
        string Unit,
        double Factor,
        double Offset,
        double Minimum,
        double Maximum,
        IReadOnlyDictionary<long, string>? ValueDescriptions,
        string Comment,
        IReadOnlyList<DbcScaleSegment>? ScaleSegments,
        bool ConversionUnsupported,
        string? ConversionWarning,
        bool IsSigned = false,
        DbcSignalValueType ValueType = DbcSignalValueType.Integer,
        string? EncodingWarning = null);

    private sealed record CompuMethodDefinition(
        double Factor,
        double Offset,
        IReadOnlyDictionary<long, string>? ValueDescriptions,
        IReadOnlyList<DbcScaleSegment>? ScaleSegments = null,
        bool ConversionUnsupported = false,
        string? ConversionWarning = null);

    public DbcDatabase ParseFile(string path)
    {
        // ARXML is untrusted offline XML. Never resolve external entities or DTDs.
        using var xmlReader = XmlReader.Create(path, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 256L * 1024 * 1024
        });
        var document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
        var warnings = new List<string>();

        var units = document.Descendants()
            .Where(element => Is(element, "UNIT"))
            .Select(element => new
            {
                Name = ShortName(element),
                Display = element.Descendants().FirstOrDefault(child => Is(child, "DISPLAY-NAME"))?.Value.Trim()
                          ?? ShortName(element)
            })
            .Where(unit => !string.IsNullOrWhiteSpace(unit.Name))
            .GroupBy(unit => unit.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Display, StringComparer.OrdinalIgnoreCase);

        var compuMethods = document.Descendants()
            .Where(element => Is(element, "COMPU-METHOD"))
            .Select(element => new { Name = ShortName(element), Definition = ParseCompuMethod(element) })
            .Where(method => !string.IsNullOrWhiteSpace(method.Name))
            .GroupBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Definition, StringComparer.OrdinalIgnoreCase);

        // SW-BASE-TYPE encodings decide signedness and IEEE float decoding (2C / IEEE754 / ...).
        var baseTypeEncodings = document.Descendants()
            .Where(element => Is(element, "SW-BASE-TYPE"))
            .Select(element => new
            {
                Name = ShortName(element),
                Encoding = element.Descendants().FirstOrDefault(child => Is(child, "BASE-TYPE-ENCODING"))?.Value.Trim()
            })
            .Where(type => !string.IsNullOrWhiteSpace(type.Name) && !string.IsNullOrWhiteSpace(type.Encoding))
            .GroupBy(type => type.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Encoding!, StringComparer.OrdinalIgnoreCase);
        var systemSignals = document.Descendants()
            .Where(element => Is(element, "SYSTEM-SIGNAL"))
            .Select(element => new { Name = ShortName(element), Element = element })
            .Where(signal => !string.IsNullOrWhiteSpace(signal.Name))
            .GroupBy(signal => signal.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Element, StringComparer.OrdinalIgnoreCase);

        var signalGroups = BuildSignalGroups(document);
        var signals = document.Descendants()
            .Where(element => Is(element, "I-SIGNAL"))
            .Select(element => ParseSignal(element, units, compuMethods, signalGroups, baseTypeEncodings, systemSignals))
            .Where(signal => !string.IsNullOrWhiteSpace(signal.Name))
            .GroupBy(signal => signal.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var signal in signals.Values)
        {
            if (signal.ConversionWarning is { Length: > 0 } warning)
                warnings.Add($"ARXML signal '{signal.Name}': {warning}");
            if (signal.EncodingWarning is { Length: > 0 } encodingWarning)
                warnings.Add($"ARXML signal '{signal.Name}': {encodingWarning}");
        }

        var pduSignals = new Dictionary<string, List<(string SignalName, int StartBit, DbcByteOrder ByteOrder)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pdu in document.Descendants().Where(element => Is(element, "I-SIGNAL-I-PDU") || Is(element, "N-PDU")))
        {
            var pduName = ShortName(pdu);
            if (string.IsNullOrWhiteSpace(pduName))
            {
                continue;
            }

            var mappings = new List<(string SignalName, int StartBit, DbcByteOrder ByteOrder)>();
            foreach (var mapping in pdu.Descendants().Where(element => Is(element, "I-SIGNAL-TO-I-PDU-MAPPING")))
            {
                var signalRef = mapping.Descendants().FirstOrDefault(element => Is(element, "I-SIGNAL-REF"))?.Value.Trim();
                var signalName = LastRefSegment(signalRef);
                if (string.IsNullOrWhiteSpace(signalName))
                {
                    continue;
                }

                // AUTOSAR: MOST-SIGNIFICANT-BYTE-FIRST is big-endian and START-POSITION is then the
                // most significant bit in sawtooth numbering, i.e. exactly the DBC Motorola start bit.
                var packing = mapping.Descendants().FirstOrDefault(element => Is(element, "PACKING-BYTE-ORDER"))?.Value.Trim();
                var byteOrder = string.Equals(packing, "MOST-SIGNIFICANT-BYTE-FIRST", StringComparison.OrdinalIgnoreCase)
                    ? DbcByteOrder.Motorola
                    : DbcByteOrder.Intel;
                mappings.Add((signalName, IntValue(mapping, "START-POSITION") ?? 0, byteOrder));
            }

            if (mappings.Count > 0)
            {
                pduSignals[pduName] = mappings;
            }
        }

        var frames = document.Descendants()
            .Where(element => Is(element, "CAN-FRAME"))
            .Select(element => new
            {
                Name = ShortName(element),
                Length = IntValue(element, "FRAME-LENGTH") ?? IntValue(element, "LENGTH") ?? 8,
                PduName = LastRefSegment(element.Descendants().FirstOrDefault(child => Is(child, "PDU-REF"))?.Value),
                Comment = Description(element)
            })
            .Where(frame => !string.IsNullOrWhiteSpace(frame.Name))
            .GroupBy(frame => frame.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var messages = new Dictionary<uint, DbcMessage>();
        var identities = new Dictionary<DbcMessageIdentity, DbcMessage>();
        foreach (var triggering in document.Descendants().Where(element => Is(element, "CAN-FRAME-TRIGGERING")))
        {
            var frameName = LastRefSegment(triggering.Descendants().FirstOrDefault(element => Is(element, "FRAME-REF"))?.Value)
                            ?? ShortName(triggering);
            if (string.IsNullOrWhiteSpace(frameName) || !frames.TryGetValue(frameName, out var frame))
            {
                continue;
            }

            var identifier = UIntValue(triggering, "IDENTIFIER");
            if (identifier is null)
            {
                warnings.Add($"Frame '{frameName}' has no CAN identifier and was skipped.");
                continue;
            }

            var pduName = frame.PduName;
            var mappedSignals = pduName is not null && pduSignals.TryGetValue(pduName, out var found)
                ? found
                : [];
            var dbcSignals = mappedSignals
                .Select(mapping =>
                {
                    if (!signals.TryGetValue(mapping.SignalName, out var signal))
                    {
                        warnings.Add($"Signal '{mapping.SignalName}' referenced by frame '{frameName}' is missing its I-SIGNAL definition.");
                    }

                    return new DbcSignal(
                        mapping.SignalName,
                        mapping.StartBit,
                        Math.Clamp(signal?.Length ?? 1, 1, 64),
                        mapping.ByteOrder,
                        signal?.IsSigned ?? false,
                        signal?.Factor ?? 1,
                        signal?.Offset ?? 0,
                        signal?.Minimum ?? 0,
                        signal?.Maximum ?? Math.Pow(2, Math.Min(53, signal?.Length ?? 1)) - 1,
                        signal?.Unit ?? string.Empty,
                        string.Empty,
                        ValueDescriptions: signal?.ValueDescriptions,
                        Comment: signal?.Comment ?? string.Empty,
                        ValueType: signal?.ValueType ?? DbcSignalValueType.Integer,
                        ScaleSegments: signal?.ScaleSegments,
                        PhysicalConversionUnsupported: signal?.ConversionUnsupported ?? false);
                })
                .ToArray();

            var id = identifier.Value;
            // The frame format is declared, not implied by the numeric value: a 29-bit frame may
            // carry a small identifier. Only a file without CAN-ADDRESSING-MODE falls back to the value.
            var addressingMode = triggering.Descendants()
                .FirstOrDefault(element => Is(element, "CAN-ADDRESSING-MODE"))?.Value.Trim();
            var isExtended = string.IsNullOrEmpty(addressingMode)
                ? id > 0x7FF
                : addressingMode.Equals("EXTENDED", StringComparison.OrdinalIgnoreCase);
            var message = new DbcMessage(
                id,
                frame.Name,
                Math.Clamp(frame.Length, 0, 64),
                string.Empty,
                isExtended,
                dbcSignals,
                frame.Comment);
            var identity = new DbcMessageIdentity(id, isExtended);
            if (identities.ContainsKey(identity))
                warnings.Add($"Frame '{frameName}' repeats CAN identifier 0x{id:X} ({(isExtended ? "extended" : "standard")}); the later definition is used.");
            identities[identity] = message;
            // Legacy numeric index: prefer the standard frame when both formats share a number.
            if (!messages.TryGetValue(id, out var existing) || existing.IsExtended || !isExtended)
                messages[id] = message;
        }

        if (messages.Count == 0)
        {
            warnings.Add("No CAN-FRAME-TRIGGERING entries with identifiers were imported from the ARXML.");
        }

        return new DbcDatabase(Path.GetFileNameWithoutExtension(path), messages, warnings, identities);
    }

    private static SignalDefinition ParseSignal(
        XElement element,
        IReadOnlyDictionary<string, string> units,
        IReadOnlyDictionary<string, CompuMethodDefinition> compuMethods,
        IReadOnlyDictionary<string, List<string>> signalGroups,
        IReadOnlyDictionary<string, string> baseTypeEncodings,
        IReadOnlyDictionary<string, XElement> systemSignals)
    {
        var name = ShortName(element);
        var length = IntValue(element, "LENGTH") ?? IntValue(element, "BIT-LENGTH") ?? 1;

        // Base type: on the I-SIGNAL itself, otherwise on the SYSTEM-SIGNAL it refers to.
        var baseTypeName = LastRefSegment(element.Descendants().FirstOrDefault(child => Is(child, "BASE-TYPE-REF"))?.Value);
        if (baseTypeName is null &&
            LastRefSegment(element.Descendants().FirstOrDefault(child => Is(child, "SYSTEM-SIGNAL-REF"))?.Value) is { } systemSignalName &&
            systemSignals.TryGetValue(systemSignalName, out var systemSignal))
        {
            baseTypeName = LastRefSegment(systemSignal.Descendants().FirstOrDefault(child => Is(child, "BASE-TYPE-REF"))?.Value);
        }

        var isSigned = false;
        var valueType = DbcSignalValueType.Integer;
        string? encodingWarning = null;
        var encodingUnsupported = false;
        if (baseTypeName is not null && baseTypeEncodings.TryGetValue(baseTypeName, out var encoding))
        {
            switch (encoding.ToUpperInvariant())
            {
                case "2C":
                    isSigned = true;
                    break;
                case "IEEE754":
                    if (length == 32) valueType = DbcSignalValueType.Float32;
                    else if (length == 64) valueType = DbcSignalValueType.Float64;
                    else
                    {
                        encodingUnsupported = true;
                        encodingWarning = $"IEEE754 base type with {length} bits is not decodable; values stay raw.";
                    }
                    break;
                case "1C":
                case "SM":
                    // One's complement / sign-magnitude are not two's complement: never guess a value.
                    encodingUnsupported = true;
                    encodingWarning = $"base type encoding '{encoding}' is not supported; values stay raw.";
                    break;
            }
        }

        var unitName = LastRefSegment(element.Descendants().FirstOrDefault(child => Is(child, "UNIT-REF"))?.Value);
        var compuMethodName = LastRefSegment(element.Descendants().FirstOrDefault(child => Is(child, "COMPU-METHOD-REF"))?.Value);
        var compu = compuMethodName is not null && compuMethods.TryGetValue(compuMethodName, out var found)
            ? found
            : new CompuMethodDefinition(1, 0, null);
        var rawMaximum = Math.Pow(2, Math.Min(53, length)) - 1;
        var physical0 = compu.Offset;
        var physicalMax = rawMaximum * compu.Factor + compu.Offset;
        if (compu.ScaleSegments is { Count: > 0 } segments)
        {
            // Display bounds are conservative estimates of declared intervals;
            // an unbounded interval uses the representable raw domain.
            var projected = segments.SelectMany(segment =>
            {
                var lo = Math.Clamp(segment.RawLower ?? 0, 0, rawMaximum);
                var hi = Math.Clamp(segment.RawUpper ?? rawMaximum, 0, rawMaximum);
                return new[]
                {
                    lo * segment.Factor + segment.Offset,
                    hi * segment.Factor + segment.Offset
                };
            }).Where(double.IsFinite).ToArray();
            if (projected.Length > 0)
            {
                physical0 = projected.Min();
                physicalMax = projected.Max();
            }
        }
        var comment = Description(element);
        if (signalGroups.TryGetValue(name, out var groups) && groups.Count > 0)
        {
            comment = string.IsNullOrWhiteSpace(comment)
                ? "Groups: " + string.Join(", ", groups)
                : comment + " Groups: " + string.Join(", ", groups);
        }

        return new SignalDefinition(
            name,
            length,
            unitName is not null && units.TryGetValue(unitName, out var unit) ? unit : string.Empty,
            compu.Factor,
            compu.Offset,
            Math.Min(physical0, physicalMax),
            Math.Max(physical0, physicalMax),
            compu.ValueDescriptions,
            comment,
            compu.ScaleSegments,
            compu.ConversionUnsupported || encodingUnsupported,
            compu.ConversionWarning,
            isSigned,
            valueType,
            encodingWarning);
    }

    private static Dictionary<string, List<string>> BuildSignalGroups(XDocument document)
    {
        var groupsBySignal = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in document.Descendants().Where(element => Is(element, "I-SIGNAL-GROUP")))
        {
            var groupName = ShortName(group);
            if (string.IsNullOrWhiteSpace(groupName))
            {
                continue;
            }

            foreach (var reference in group.Descendants().Where(element => Is(element, "I-SIGNAL-REF")))
            {
                var signalName = LastRefSegment(reference.Value);
                if (string.IsNullOrWhiteSpace(signalName))
                {
                    continue;
                }

                if (!groupsBySignal.TryGetValue(signalName, out var groups))
                {
                    groups = [];
                    groupsBySignal[signalName] = groups;
                }

                if (!groups.Contains(groupName, StringComparer.OrdinalIgnoreCase))
                {
                    groups.Add(groupName);
                }
            }
        }

        return groupsBySignal;
    }

    private static CompuMethodDefinition ParseCompuMethod(XElement method)
    {
        var scales = new List<DbcScaleSegment>();
        var values = new Dictionary<long, string>();
        var unsupported = false;
        var warnings = new List<string>();
        foreach (var scale in method.Descendants().Where(element => Is(element, "COMPU-SCALE")))
        {
            var lowerNode = scale.Elements().FirstOrDefault(element => Is(element, "LOWER-LIMIT"));
            var upperNode = scale.Elements().FirstOrDefault(element => Is(element, "UPPER-LIMIT"));
            var lower = DoubleValue(lowerNode?.Value);
            var upper = DoubleValue(upperNode?.Value);

            // Text tables supply named raw codes, not linear physical units.
            // Do not mistake a COMPU-NUMERATOR/V scalar for a text meaning.
            var text = scale.Descendants().FirstOrDefault(element => Is(element, "VT"))?.Value.Trim();
            var code = LongValue(lowerNode?.Value);
            if (code is not null && !string.IsNullOrWhiteSpace(text) &&
                upper is not null && lower == upper)
                values.TryAdd(code.Value, text);

            var rational = scale.Descendants()
                .FirstOrDefault(element => Is(element, "COMPU-RATIONAL-COEFFS"));
            if (rational is null) continue;

            var numerator = rational.Descendants()
                .FirstOrDefault(element => Is(element, "COMPU-NUMERATOR"))?
                .Elements().Where(element => Is(element, "V"))
                .Select(element => DoubleValue(element.Value)).ToArray() ?? [];
            var denominator = rational.Descendants()
                .FirstOrDefault(element => Is(element, "COMPU-DENOMINATOR"))?
                .Elements().Where(element => Is(element, "V"))
                .Select(element => DoubleValue(element.Value)).ToArray() ?? [];

            if (numerator.Length != 2 || numerator.Any(value => value is null) ||
                denominator.Length > 1 || denominator.Any(value => value is null) ||
                (denominator.Length > 0 && denominator[0] == 0) ||
                (lowerNode is not null && lower is null) ||
                (upperNode is not null && upper is null) ||
                (lower is not null && upper is not null && lower > upper))
            {
                unsupported = true;
                warnings.Add("non-affine, malformed, or invalid-bounds COMPU-SCALE");
                continue;
            }

            var divisor = denominator.Length == 0 ? 1d : denominator[0]!.Value;
            var factor = numerator[1]!.Value / divisor;
            var offset = numerator[0]!.Value / divisor;
            if (!double.IsFinite(factor) || !double.IsFinite(offset))
            {
                unsupported = true;
                warnings.Add("non-finite COMPU-SCALE coefficients");
                continue;
            }
            var lowerInclusive = !string.Equals(
                lowerNode?.Attribute("INTERVAL-TYPE")?.Value, "OPEN",
                StringComparison.OrdinalIgnoreCase);
            var upperInclusive = !string.Equals(
                upperNode?.Attribute("INTERVAL-TYPE")?.Value, "OPEN",
                StringComparison.OrdinalIgnoreCase);
            scales.Add(new DbcScaleSegment(lower, upper, factor, offset,
                lowerInclusive, upperInclusive));
        }

        if (scales.Count == 0)
        {
            return new CompuMethodDefinition(1, 0, values.Count == 0 ? null : values,
                ConversionUnsupported: unsupported,
                ConversionWarning: unsupported
                    ? "Physical conversion unsupported; recorded raw value is retained without a fabricated physical value."
                    : null);
        }

        // Even a single bounded affine scale is not valid outside its bounds.
        // A whole-range scalar can use the legacy fast path.
        var first = scales[0];
        var isFullRangeScalar = scales.Count == 1 &&
            first.RawLower is null && first.RawUpper is null;
        var piecewise = isFullRangeScalar ? null : scales.ToArray();
        if (piecewise is not null)
            warnings.Add("range-dependent conversion; uncovered or conflicting raw codes remain raw-only");
        if (unsupported)
            warnings.Add("at least one scale is unparseable; physical values are suppressed");

        return new CompuMethodDefinition(first.Factor, first.Offset,
            values.Count == 0 ? null : values, piecewise, unsupported,
            warnings.Count > 0 ? string.Join("; ", warnings.Distinct(StringComparer.Ordinal)) : null);
    }

    private static bool Is(XElement element, string localName) =>
        element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase);

    private static string ShortName(XElement element) =>
        element.Elements().FirstOrDefault(child => Is(child, "SHORT-NAME"))?.Value.Trim() ?? string.Empty;

    private static string Description(XElement element) =>
        string.Join(" ", element.Descendants()
            .Where(child => Is(child, "L-2") || Is(child, "DESC"))
            .Select(child => child.Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static string? LastRefSegment(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var trimmed = reference.Trim();
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static int? IntValue(XElement root, string localName)
    {
        var text = root.Descendants().FirstOrDefault(element => Is(element, localName))?.Value.Trim();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static uint? UIntValue(XElement root, string localName)
    {
        var text = root.Descendants().FirstOrDefault(element => Is(element, localName))?.Value.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : null;
        }

        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static double? DoubleValue(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
        double.IsFinite(value) ? value : null;

    private static long? LongValue(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}
