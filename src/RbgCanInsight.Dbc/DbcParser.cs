using System.Globalization;
using System.Text.RegularExpressions;

namespace RbgCanInsight.Dbc;

/// <summary>
/// Parser for Vector DBC network databases. Handles messages (BO_), signals (SG_) including
/// multiplexing, value tables (VAL_) and message/signal comments (CM_), which are preserved
/// because they carry the human descriptions used by the DID correlation engine.
/// </summary>
public sealed partial class DbcParser
{
    /// <summary>
    /// Vector CANdb uses this pseudo-message (ID 0xC0000000) as a container for signals that do
    /// not belong to any real message. After 29-bit masking its ID would collide with a real
    /// ID 0 message, so it must never enter the message dictionary.
    /// </summary>
    private const string IndependentSignalsMessage = "VECTOR__INDEPENDENT_SIG_MSG";

    public DbcDatabase Parse(string name, TextReader reader)
    {
        var messages = new Dictionary<uint, MutableMessage>();
        var valueDescriptions =
            new Dictionary<(uint Identifier, string Signal), Dictionary<long, string>>();
        var signalComments = new Dictionary<(uint Identifier, string Signal), string>();
        var messageComments = new Dictionary<uint, string>();
        var signalLongNames = new Dictionary<(uint Identifier, string Signal), string>();
        var messageLongNames = new Dictionary<uint, string>();
        var signalValueTypes = new Dictionary<(uint Identifier, string Signal), DbcSignalValueType>();
        var cycleTimes = new Dictionary<uint, int>();
        var messageAttributes = new Dictionary<uint, List<DbcSourceAttribute>>();
        var signalAttributes = new Dictionary<(uint RawIdentifier, string Signal), List<DbcSourceAttribute>>();
        var attributeDeclarations = new List<string>();
        var extendedMultiplexing = new Dictionary<(uint RawIdentifier, string Signal), List<DbcMultiplexCondition>>();
        var warnings = new List<string>();
        MutableMessage? currentMessage = null;
        var lineNumber = 0;
        _pendingLine = null;
        _inNamespaceBlock = false;
        _lineIsNamespaceEntry = false;
        var contentLines = 0;
        var skippedLines = 0;

        while (ReadLogicalLine(reader, ref lineNumber, warnings) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (_lineIsNamespaceEntry)
            {
                // "NS_ :" new-symbols entries are bare keyword declarations, not statements.
                continue;
            }

            contentLines++;
            if (FindSkippedStatementKind(line) is { } skippedKind)
            {
                // The statement is still offered to the handlers below (unchanged behaviour); this only
                // makes sure bad input is reported through the same parse-warning list that drives the badge.
                if (++skippedLines <= MaxReportedSkippedLines)
                    warnings.Add($"Line {lineNumber}: {skippedKind} ignored: '{Preview(line)}'.");
            }

            if (line.TrimStart().StartsWith("BA_DEF_", StringComparison.Ordinal) ||
                line.TrimStart().StartsWith("BA_DEF_DEF_", StringComparison.Ordinal))
            {
                attributeDeclarations.Add(line.Trim());
                continue;
            }

            var messageMatch = MessagePattern().Match(line);
            if (messageMatch.Success)
            {
                var rawIdentifier = ParseUInt(messageMatch.Groups["id"].Value, lineNumber);
                var isExtended = (rawIdentifier & 0x8000_0000u) != 0;
                var identifier = isExtended ? rawIdentifier & 0x1FFF_FFFFu : rawIdentifier;
                var messageName = DecodeDbcString(messageMatch.Groups["name"].Value);
                if (!isExtended && identifier > 0x7FF)
                    warnings.Add($"Line {lineNumber}: standard CAN identifier 0x{identifier:X} exceeds 11 bits.");
                currentMessage = new MutableMessage(
                    rawIdentifier,
                    identifier,
                    messageName,
                    ParseInt(messageMatch.Groups["length"].Value, lineNumber),
                    messageMatch.Groups["node"].Value,
                    isExtended);
                if (messageName == IndependentSignalsMessage)
                {
                    // Keep parsing its SG_ lines into the discard container, but never register it.
                    warnings.Add($"Line {lineNumber}: skipped Vector pseudo-message '{messageName}' and its orphan signals.");
                }
                else if (messages.TryGetValue(rawIdentifier, out var duplicate))
                {
                    warnings.Add(
                        $"Line {lineNumber}: duplicate message ID 0x{identifier:X} — '{duplicate.Name}' is replaced by '{messageName}'.");
                    messages[rawIdentifier] = currentMessage;
                }
                else
                {
                    messages[rawIdentifier] = currentMessage;
                }

                continue;
            }

            var signalMatch = SignalPattern().Match(line);
            if (signalMatch.Success)
            {
                if (currentMessage is null)
                {
                    throw new DbcParseException("Signal declared before a message.", lineNumber);
                }

                var signalName = DecodeDbcString(signalMatch.Groups["name"].Value);
                if (currentMessage.Signals.Any(item =>
                    string.Equals(item.Name, signalName, StringComparison.Ordinal)))
                {
                    warnings.Add($"Line {lineNumber}: duplicate signal '{signalName}' in message 0x{currentMessage.RawIdentifier:X}; later definition was skipped.");
                    continue;
                }
                var mux = signalMatch.Groups["mux"].Value;
                // "m<N>M" marks a signal that is both multiplexed under value N and itself the
                // multiplexor for a nested group (extended multiplexing).
                var isMultiplexor = mux == "M" || mux.EndsWith('M');
                int? multiplexValue = mux.Length > 0 && mux[0] == 'm'
                    ? ParseInt(isMultiplexor ? mux[1..^1] : mux[1..], lineNumber)
                    : null;
                currentMessage.Signals.Add(new MutableSignal(
                    signalName,
                    ParseInt(signalMatch.Groups["start"].Value, lineNumber),
                    ParseInt(signalMatch.Groups["length"].Value, lineNumber),
                    signalMatch.Groups["order"].Value == "1"
                        ? DbcByteOrder.Intel
                        : DbcByteOrder.Motorola,
                    signalMatch.Groups["sign"].Value == "-",
                    ParseDouble(signalMatch.Groups["factor"].Value, lineNumber),
                    ParseDouble(signalMatch.Groups["offset"].Value, lineNumber),
                    ParseDouble(signalMatch.Groups["min"].Value, lineNumber),
                    ParseDouble(signalMatch.Groups["max"].Value, lineNumber),
                    DecodeDbcString(signalMatch.Groups["unit"].Value),
                    signalMatch.Groups["receiver"].Value.Trim(),
                    isMultiplexor,
                    multiplexValue));
                continue;
            }

            var signalCommentMatch = SignalCommentPattern().Match(line);
            if (signalCommentMatch.Success)
            {
                var rawId = ParseUInt(signalCommentMatch.Groups["id"].Value, lineNumber);
                var id = (rawId & 0x8000_0000u) != 0 ? rawId & 0x1FFF_FFFFu : rawId;
                signalComments[(rawId, signalCommentMatch.Groups["signal"].Value)] =
                    DecodeDbcString(signalCommentMatch.Groups["text"].Value);
                continue;
            }

            var messageCommentMatch = MessageCommentPattern().Match(line);
            if (messageCommentMatch.Success)
            {
                var rawId = ParseUInt(messageCommentMatch.Groups["id"].Value, lineNumber);
                var id = (rawId & 0x8000_0000u) != 0 ? rawId & 0x1FFF_FFFFu : rawId;
                messageComments[rawId] = DecodeDbcString(messageCommentMatch.Groups["text"].Value);
                continue;
            }

            var messageLongNameMatch = MessageLongNamePattern().Match(line);
            if (messageLongNameMatch.Success)
            {
                var rawId = ParseUInt(messageLongNameMatch.Groups["id"].Value, lineNumber);
                var id = (rawId & 0x8000_0000u) != 0 ? rawId & 0x1FFF_FFFFu : rawId;
                messageLongNames[rawId] = DecodeDbcString(messageLongNameMatch.Groups["text"].Value);
                continue;
            }

            var signalLongNameMatch = SignalLongNamePattern().Match(line);
            if (signalLongNameMatch.Success)
            {
                var rawId = ParseUInt(signalLongNameMatch.Groups["id"].Value, lineNumber);
                var id = (rawId & 0x8000_0000u) != 0 ? rawId & 0x1FFF_FFFFu : rawId;
                signalLongNames[(rawId, signalLongNameMatch.Groups["signal"].Value)] =
                    DecodeDbcString(signalLongNameMatch.Groups["text"].Value);
                continue;
            }

            var valueMatch = ValuePattern().Match(line);
            if (valueMatch.Success)
            {
                var rawValueIdentifier = ParseUInt(valueMatch.Groups["id"].Value, lineNumber);
                var valueIdentifier = NormalizeIdentifier(rawValueIdentifier);
                var values = new Dictionary<long, string>();
                foreach (Match pair in ValuePairPattern().Matches(valueMatch.Groups["values"].Value))
                {
                    // A key beyond the signed 64-bit range cannot be a decodable raw value here;
                    // skip that one entry instead of failing the whole database.
                    if (!long.TryParse(pair.Groups["value"].Value, NumberStyles.AllowLeadingSign,
                            CultureInfo.InvariantCulture, out var key))
                    {
                        warnings.Add($"Line {lineNumber}: VAL_ key {pair.Groups["value"].Value} is outside the supported range and was skipped.");
                        continue;
                    }

                    values[key] = DecodeDbcString(pair.Groups["text"].Value);
                }

                valueDescriptions[(rawValueIdentifier, valueMatch.Groups["signal"].Value)] = values;
                continue;
            }

            var attribute = SourceAttributePattern().Match(line);
            if (attribute.Success)
            {
                var messageAttribute = attribute.Groups["boId"].Success;
                var rawId = ParseUInt(attribute.Groups[messageAttribute ? "boId" : "sgId"].Value, lineNumber);
                var attributeName = attribute.Groups["name"].Value;
                var rawValue = attribute.Groups[messageAttribute ? "boRaw" : "sgRaw"].Value.Trim();
                var source = new DbcSourceAttribute(attributeName, rawValue);
                if (messageAttribute)
                {
                    if (!messageAttributes.TryGetValue(rawId, out var list))
                        messageAttributes[rawId] = list = [];
                    list.Add(source);
                    if (attributeName == "GenMsgCycleTime")
                    {
                        if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0)
                            cycleTimes[rawId] = value;
                        else
                            warnings.Add($"Line {lineNumber}: invalid GenMsgCycleTime for 0x{NormalizeIdentifier(rawId):X}; no nominal cycle assumed.");
                    }
                }
                else
                {
                    var key = (rawId, attribute.Groups["signal"].Value);
                    if (!signalAttributes.TryGetValue(key, out var list))
                        signalAttributes[key] = list = [];
                    list.Add(source);
                }
                continue;
            }

            var extendedMux = ExtendedMultiplexPattern().Match(line);
            if (extendedMux.Success)
            {
                var rawId = ParseUInt(extendedMux.Groups["id"].Value, lineNumber);
                var key = (rawId, extendedMux.Groups["signal"].Value);
                var controller = extendedMux.Groups["mux"].Value;
                var ranges = new List<DbcMuxRange>();
                var rangeText = extendedMux.Groups["ranges"].Value;
                if (!ValidMultiplexRangesPattern().IsMatch(rangeText))
                {
                    warnings.Add($"Line {lineNumber}: malformed SG_MUL_VAL_ ranges for '{key.Item2}' were ignored.");
                    continue;
                }
                foreach (Match range in MultiplexRangePattern().Matches(rangeText))
                {
                    if (!long.TryParse(range.Groups["min"].Value, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var lower) ||
                        !long.TryParse(range.Groups["max"].Value, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var upper))
                    {
                        warnings.Add($"Line {lineNumber}: SG_MUL_VAL_ range exceeds supported integer limits for '{key.Item2}'.");
                        continue;
                    }
                    if (lower > upper)
                    {
                        warnings.Add($"Line {lineNumber}: inverted SG_MUL_VAL_ range {lower}-{upper} for {key.Item2}.");
                        continue;
                    }
                    ranges.Add(new DbcMuxRange(lower, upper));
                }
                if (ranges.Count == 0)
                {
                    warnings.Add($"Line {lineNumber}: SG_MUL_VAL_ has no valid controller range for {key.Item2}.");
                    continue;
                }
                if (!extendedMultiplexing.TryGetValue(key, out var conditions))
                {
                    conditions = [];
                    extendedMultiplexing[key] = conditions;
                }
                conditions.Add(new DbcMultiplexCondition(controller, ranges));
                continue;
            }

            var signalValueTypeMatch = SignalValueTypePattern().Match(line);
            if (signalValueTypeMatch.Success)
            {
                var rawId = ParseUInt(signalValueTypeMatch.Groups["id"].Value, lineNumber);
                var id = NormalizeIdentifier(rawId);
                signalValueTypes[(rawId, signalValueTypeMatch.Groups["signal"].Value)] =
                    signalValueTypeMatch.Groups["type"].Value switch
                    {
                        "1" => DbcSignalValueType.Float32,
                        "2" => DbcSignalValueType.Float64,
                        _ => DbcSignalValueType.Integer
                    };
            }
        }

        if (skippedLines > MaxReportedSkippedLines)
            warnings.Add($"{skippedLines - MaxReportedSkippedLines} more unrecognised or malformed line(s) were ignored; the file may be truncated or damaged.");
        if (messages.Count == 0 && contentLines > 0)
            warnings.Add("No messages were parsed (no valid BO_ definitions found) - the file does not look like a valid DBC.");

        var completeMessages = messages.Values.Select(message =>
            message.ToImmutable(
                valueDescriptions, signalComments, signalLongNames, signalValueTypes,
                extendedMultiplexing, messageAttributes, signalAttributes,
                CombineText(
                    messageLongNames.GetValueOrDefault(message.RawIdentifier, string.Empty),
                    messageComments.GetValueOrDefault(message.RawIdentifier, string.Empty)),
                cycleTimes.GetValueOrDefault(message.RawIdentifier) is > 0
                    ? cycleTimes[message.RawIdentifier] : (int?)null))
            .ToArray();
        // DBC layout issues must be visible even when no trace is loaded.
        foreach (var message in completeMessages)
        {
            var muxNames = message.Signals.Where(signal => signal.IsMultiplexor)
                .Select(signal => signal.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var signal in message.Signals)
            {
                var positions = DbcSignalInspector.PhysicalBits(signal);
                if (signal.BitLength is <= 0 or > 64)
                    warnings.Add($"Message {message.Name}, signal {signal.Name}: unsupported bit length {signal.BitLength}.");
                else if (positions.Any(position => position < 0 || position >= message.Length * 8))
                    warnings.Add($"Message {message.Name}, signal {signal.Name}: declared bit range exceeds DLC {message.Length}.");
                foreach (var condition in signal.MultiplexConditions ?? [])
                {
                    if (!muxNames.Contains(condition.Multiplexor))
                        warnings.Add($"Message {message.Name}, signal {signal.Name}: SG_MUL_VAL_ references unknown multiplexor '{condition.Multiplexor}'.");
                }
            }
        }

        // Preserve the legacy numeric projection while keeping *all* physical
        // CAN identities in the authoritative decoder/model index.
        var legacy = completeMessages
            .GroupBy(message => message.Identifier)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(message => message.IsExtended).First());
        var byFrameIdentity = completeMessages.ToDictionary(
            message => new DbcMessageIdentity(message.Identifier, message.IsExtended));
        return new DbcDatabase(name, legacy, warnings, byFrameIdentity, attributeDeclarations);
    }

    public DbcDatabase ParseFile(string path)
    {
        using var reader = new StreamReader(path);
        return Parse(Path.GetFileNameWithoutExtension(path), reader);
    }

    private string? _pendingLine;

    private const int MaxReportedSkippedLines = 5;

    private static readonly HashSet<string> KnownStatementKeywords = new(StringComparer.Ordinal)
    {
        "BO_", "SG_", "CM_", "VAL_", "BA_", "BA_DEF_", "BA_DEF_DEF_", "BA_DEF_REL_", "BA_DEF_DEF_REL_",
        "BA_REL_", "BU_", "BS_", "NS_", "EV_", "VAL_TABLE_", "SIG_VALTYPE_", "SG_MUL_VAL_", "SIG_GROUP_",
        "VERSION", "BO_TX_BU_", "BU_SG_REL_", "BU_EV_REL_", "BU_BO_REL_", "CAT_DEF_", "CAT_", "FILTER",
        "SGTYPE_", "SGTYPE_VAL_", "BA_DEF_SGTYPE_", "ENVVAR_DATA_", "SIG_TYPE_REF_", "SIG_TYPE_",
        "BA_SGTYPE_", "SIGTYPE_VALTYPE_", "NS_DESC_", "EV_DATA_", "ENVVAR_"
    };

    /// <summary>Returns a description when the statement is not valid DBC (unknown keyword, or a BO_/SG_
    /// definition too incomplete for the parser to use); null for statements the parser understands.</summary>
    private string? FindSkippedStatementKind(string line)
    {
        var trimmed = line.TrimStart();
        var end = 0;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]) && trimmed[end] is not (':' or '"'))
            end++;
        var keyword = trimmed[..end];
        if (!KnownStatementKeywords.Contains(keyword))
            return "unrecognised statement";
        if (keyword == "BO_" && !MessagePattern().IsMatch(line))
            return "incomplete or malformed message definition (BO_)";
        if (keyword == "SG_" && !SignalPattern().IsMatch(line))
            return "incomplete or malformed signal definition (SG_)";
        return null;
    }

    private static string Preview(string line)
    {
        var single = line.Trim().ReplaceLineEndings(" ");
        return single.Length <= 48 ? single : single[..48] + "...";
    }

    private static readonly string[] TopLevelKeywords =
    [
        "BO_ ", "SG_ ", "CM_ ", "VAL_ ", "BA_", "BU_", "BS_", "NS_", "EV_ ",
        "VAL_TABLE_ ", "SIG_VALTYPE_ ", "SG_MUL_VAL_ ", "SIG_GROUP_ ", "VERSION",
        "BO_TX_BU_ ", "CAT_ ", "CAT_DEF_ ", "FILTER ", "SGTYPE_ ", "ENVVAR_DATA_ "
    ];

    private bool _inNamespaceBlock;
    private bool _lineIsNamespaceEntry;

    private static bool IsBareKeyword(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 0 &&
               trimmed.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private string? ReadLogicalLine(TextReader reader, ref int lineNumber, List<string> warnings)
    {
        _lineIsNamespaceEntry = false;
        var line = TakeLine(reader, ref lineNumber);
        if (line is null)
        {
            return null;
        }

        // The "NS_ :" new-symbols block lists keywords (CM_, BA_, VAL_ ...) as bare,
        // indented words without ';'. They are declarations, not statements to join.
        if (_inNamespaceBlock && IsBareKeyword(line))
        {
            // Entries are bare keywords, usually indented but some tools write them at column 0.
            _lineIsNamespaceEntry = true;
            return line;
        }

        if (line.TrimStart().StartsWith("NS_", StringComparison.Ordinal))
        {
            _inNamespaceBlock = true;
            return line;
        }

        if (_inNamespaceBlock && line.Trim().Length > 0)
            _inNamespaceBlock = false;

        if (!line.TrimStart().StartsWith("CM_", StringComparison.Ordinal) &&
            !line.TrimStart().StartsWith("VAL_", StringComparison.Ordinal) &&
            !line.TrimStart().StartsWith("BA_", StringComparison.Ordinal))
        {
            return line;
        }

        // Vector DBC comments may span several physical lines. Treat them as one logical
        // statement so descriptions are not silently dropped when they contain line breaks.
        // If a continuation line starts a new top-level statement, the current one is
        // malformed (missing ';'): stop joining so it cannot swallow the rest of the file.
        var startLine = lineNumber;
        var stoppedAtNextStatement = false;
        while (!IsStatementComplete(line) && TakeLine(reader, ref lineNumber) is { } continuation)
        {
            if (IsInsideQuotes(line)
                ? MessagePattern().IsMatch(continuation) || SignalPattern().IsMatch(continuation)
                : StartsTopLevelStatement(continuation))
            {
                stoppedAtNextStatement = true;
                _pendingLine = continuation;
                lineNumber--;
                warnings.Add($"Line {startLine}: statement not terminated with ';' — truncated to keep parsing.");
                break;
            }

            line += "\n" + continuation;
        }

        if (!stoppedAtNextStatement && !IsStatementComplete(line))
            warnings.Add($"Line {startLine}: statement not terminated with ';' before the end of the file - the file may be truncated.");

        return line;
    }

    private static bool IsInsideQuotes(string text)
    {
        var insideQuotes = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\\' && insideQuotes)
                index++;
            else if (text[index] == '"')
                insideQuotes = !insideQuotes;
        }

        return insideQuotes;
    }

    private string? TakeLine(TextReader reader, ref int lineNumber)
    {
        string? line;
        if (_pendingLine is not null)
        {
            line = _pendingLine;
            _pendingLine = null;
        }
        else
        {
            line = reader.ReadLine();
        }

        if (line is not null)
        {
            // A byte-order mark survives when Parse(name, TextReader) is fed a decoded string.
            if (lineNumber == 0 && line.Length > 0 && line[0] == '﻿')
                line = line[1..];
            lineNumber++;
        }

        return line;
    }

    private static bool StartsTopLevelStatement(string line)
    {
        var trimmed = line.TrimStart();
        return TopLevelKeywords.Any(keyword => trimmed.StartsWith(keyword, StringComparison.Ordinal));
    }

    /// <summary>A statement ends with ';' outside a quoted string. A ';' that merely ends a
    /// physical line inside a multi-line comment text does not terminate it.</summary>
    private static bool IsStatementComplete(string text)
    {
        var insideQuotes = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\\' && insideQuotes)
                index++; // skip the escaped character (\" or \\)
            else if (text[index] == '"')
                insideQuotes = !insideQuotes;
        }

        return !insideQuotes && text.TrimEnd().EndsWith(';');
    }

    /// <summary>DBC text escapes only the quote and the backslash. "\n" in a comment or unit is
    /// a literal backslash followed by n (as in a Windows path), not a line break.</summary>
    private static string DecodeDbcString(string value)
    {
        if (!value.Contains('\\'))
            return value;
        var builder = new System.Text.StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' && index + 1 < value.Length && value[index + 1] is '"' or '\\')
                index++;
            builder.Append(value[index]);
        }

        return builder.ToString();
    }

    private static string CombineText(string primary, string secondary)
    {
        if (string.IsNullOrWhiteSpace(primary))
        {
            return secondary;
        }

        if (string.IsNullOrWhiteSpace(secondary) ||
            secondary.Contains(primary, StringComparison.OrdinalIgnoreCase))
        {
            return primary;
        }

        return primary + Environment.NewLine + secondary;
    }

    private static uint ParseUInt(string value, int lineNumber) =>
        uint.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new DbcParseException($"Invalid unsigned integer '{value}'.", lineNumber);

    private static uint NormalizeIdentifier(uint rawIdentifier) =>
        (rawIdentifier & 0x8000_0000u) != 0 ? rawIdentifier & 0x1FFF_FFFFu : rawIdentifier;

    private static int ParseInt(string value, int lineNumber) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new DbcParseException($"Invalid integer '{value}'.", lineNumber);

    private static double ParseDouble(string value, int lineNumber) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new DbcParseException($"Invalid number '{value}'.", lineNumber);

    [GeneratedRegex(
        @"^\s*BO_\s+(?<id>\d+)\s+(?<name>[^:\s]+)\s*:\s*(?<length>\d+)\s+(?<node>\S+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex MessagePattern();

    [GeneratedRegex(
        "^\\s*SG_\\s+(?<name>[^\\s:]+)(?:\\s+(?<mux>M|m\\d+M?))?\\s*:\\s*(?<start>\\d+)\\s*\\|\\s*(?<length>\\d+)\\s*@\\s*(?<order>[01])\\s*(?<sign>[+-])\\s+\\(\\s*(?<factor>[-+\\d.eE]+)\\s*,\\s*(?<offset>[-+\\d.eE]+)\\s*\\)\\s+\\[\\s*(?<min>[-+\\d.eE]+)\\s*\\|\\s*(?<max>[-+\\d.eE]+)\\s*\\]\\s+\"(?<unit>(?:\\\\.|[^\"])*)\"\\s+(?<receiver>[^;\\r\\n]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SignalPattern();

    [GeneratedRegex(
        "^\\s*VAL_\\s+(?<id>\\d+)\\s+(?<signal>[^\\s;]+)\\s+(?<values>.+);\\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ValuePattern();

    [GeneratedRegex("(?<value>-?\\d+)\\s+\"(?<text>(?:\\\\.|[^\"])*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex ValuePairPattern();

    [GeneratedRegex(
        "^\\s*CM_\\s+SG_\\s+(?<id>\\d+)\\s+(?<signal>[^\\s;]+)\\s+\"(?<text>(?:\\\\.|[^\"])*)\"\\s*;\\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex SignalCommentPattern();

    [GeneratedRegex(
        "^\\s*CM_\\s+BO_\\s+(?<id>\\d+)\\s+\"(?<text>(?:\\\\.|[^\"])*)\"\\s*;\\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex MessageCommentPattern();

    [GeneratedRegex(
        "^\\s*BA_\\s+\"SystemMessageLongSymbol\"\\s+BO_\\s+(?<id>\\d+)\\s+\"(?<text>(?:\\\\.|[^\"])*)\"\\s*;\\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex MessageLongNamePattern();

    [GeneratedRegex(
        "^\\s*BA_\\s+\"SystemSignalLongSymbol\"\\s+SG_\\s+(?<id>\\d+)\\s+(?<signal>[^\\s;]+)\\s+\"(?<text>(?:\\\\.|[^\"])*)\"\\s*;\\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex SignalLongNamePattern();

    [GeneratedRegex(
        "^\\s*SIG_VALTYPE_\\s+(?<id>\\d+)\\s+(?<signal>[^\\s:]+)\\s*:\\s*(?<type>\\d+)\\s*;\\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SignalValueTypePattern();

    [GeneratedRegex(@"^\s*SG_MUL_VAL_\s+(?<id>\d+)\s+(?<signal>\S+)\s+(?<mux>\S+)\s+(?<ranges>[^;]+)\s*;\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExtendedMultiplexPattern();

    [GeneratedRegex(@"(?<min>\d+)\s*-\s*(?<max>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex MultiplexRangePattern();

    [GeneratedRegex(@"^\s*\d+\s*-\s*\d+\s*(?:,\s*\d+\s*-\s*\d+\s*)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ValidMultiplexRangesPattern();

    [GeneratedRegex(@"^\s*BA_\s+""(?<name>[^""]+)""\s+(?:BO_\s+(?<boId>\d+)\s+(?<boRaw>.+)|SG_\s+(?<sgId>\d+)\s+(?<signal>[^\s;]+)\s+(?<sgRaw>.+))\s*;\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex SourceAttributePattern();



    private sealed class MutableMessage(
        uint rawIdentifier,
        uint identifier,
        string name,
        int length,
        string transmitter,
        bool isExtended)
    {
        public uint RawIdentifier { get; } = rawIdentifier;
        public uint Identifier { get; } = identifier;
        public string Name { get; } = name;
        public List<MutableSignal> Signals { get; } = [];

        public DbcMessage ToImmutable(
            IReadOnlyDictionary<(uint Identifier, string Signal), Dictionary<long, string>> values,
            IReadOnlyDictionary<(uint Identifier, string Signal), string> comments,
            IReadOnlyDictionary<(uint Identifier, string Signal), string> longNames,
            IReadOnlyDictionary<(uint Identifier, string Signal), DbcSignalValueType> valueTypes,
            IReadOnlyDictionary<(uint RawIdentifier, string Signal), List<DbcMultiplexCondition>> extendedMultiplexing,
            IReadOnlyDictionary<uint, List<DbcSourceAttribute>> messageAttributes,
            IReadOnlyDictionary<(uint RawIdentifier, string Signal), List<DbcSourceAttribute>> signalAttributes,
            string messageComment,
            int? nominalCycleMs) =>
            new(
                Identifier,
                Name,
                length,
                transmitter,
                isExtended,
                Signals.Select(signal => signal.ToImmutable(
                    values.GetValueOrDefault((RawIdentifier, signal.Name)),
                    CombineText(
                        longNames.GetValueOrDefault((RawIdentifier, signal.Name), string.Empty),
                        comments.GetValueOrDefault((RawIdentifier, signal.Name), string.Empty)),
                    valueTypes.GetValueOrDefault((RawIdentifier, signal.Name), DbcSignalValueType.Integer),
                    extendedMultiplexing.GetValueOrDefault((RawIdentifier, signal.Name)),
                    signalAttributes.GetValueOrDefault((RawIdentifier, signal.Name)))).ToArray(),
                messageComment,
                nominalCycleMs,
                messageAttributes.GetValueOrDefault(RawIdentifier));
    }

    private sealed record MutableSignal(
        string Name,
        int StartBit,
        int BitLength,
        DbcByteOrder ByteOrder,
        bool IsSigned,
        double Factor,
        double Offset,
        double Minimum,
        double Maximum,
        string Unit,
        string Receiver,
        bool IsMultiplexor,
        int? MultiplexValue)
    {
        public DbcSignal ToImmutable(
            IReadOnlyDictionary<long, string>? values,
            string comment,
            DbcSignalValueType valueType,
            IReadOnlyList<DbcMultiplexCondition>? extendedMux,
            IReadOnlyList<DbcSourceAttribute>? attributes) =>
            new(
                Name, StartBit, BitLength, ByteOrder, IsSigned, Factor, Offset,
                Minimum, Maximum, Unit, Receiver, IsMultiplexor, MultiplexValue, values, comment, valueType,
                extendedMux, attributes);
    }
}
