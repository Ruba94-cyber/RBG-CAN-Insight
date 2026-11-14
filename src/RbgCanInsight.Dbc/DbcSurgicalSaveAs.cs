using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RbgCanInsight.Dbc;

/// <summary>
/// Restricted DBC Save As: surgically replaces SG_ scalar fields and reviewed
/// VAL_ raw-code tables. All other comments, unknown Vector/OEM statements, attributes,
/// line endings, signal multiplexing and message declarations remain verbatim.
/// Unsupported/ambiguous sources fail closed, rather than using a lossy serializer.
/// </summary>
public static class DbcSurgicalSaveAs
{
    private const long MaximumInputBytes = 64 * 1024 * 1024;
    private static readonly Regex MessageLine = new(
        @"^\s*BO_\s+(?<id>\d+)\s+", RegexOptions.CultureInvariant);
    private static readonly Regex ValueLine = new(
        @"^\s*VAL_\s+(?<id>\d+)\s+(?<signal>[^\s;]+)\s+",
        RegexOptions.CultureInvariant);

    private static readonly Regex SignalLine = new(
        @"^(?<prefix>\s*SG_\s+(?<name>[^\s:]+)(?:\s+(?:M|m\d+M?))?\s*:\s*)" +
        @"\d+\s*\|\s*\d+\s*@\s*[01]\s*[+-]\s+" +
        @"\(\s*[-+\d.eE]+\s*,\s*[-+\d.eE]+\s*\)\s+" +
        @"\[\s*[-+\d.eE]+\s*\|\s*[-+\d.eE]+\s*\]\s+" +
        @"""(?:\\.|[^""])*""\s+(?<receiver>[^;\r\n]+?)(?<suffix>\s*;?\s*)$",
        RegexOptions.CultureInvariant);

    /// <summary>Return a modified DBC text buffer or throw before writing anything.</summary>
    public static string Generate(string originalText, DbcWorkingCopy workingCopy)
    {
        ArgumentNullException.ThrowIfNull(originalText);
        ArgumentNullException.ThrowIfNull(workingCopy);
        if (!workingCopy.IsModified) throw new InvalidOperationException("There are no staged changes.");
        if (originalText.Contains('\r') && !originalText.Contains('\n'))
            throw new NotSupportedException("CR-only DBC line endings need a dedicated preservation pass.");

        var before = new DbcParser().Parse("source-check", new StringReader(originalText));
        // Compare the whole loaded definition graph before attempting any patch.
        // Changes in unrelated messages/signals must not silently enter an edited copy.
        VerifySemantics(workingCopy.OriginalDatabase, before,
            new Dictionary<(DbcMessageIdentity Identity, string Signal), DbcSignalChange>());
        if (before.ParseWarnings.Any(warning => warning.Contains("duplicate message", StringComparison.OrdinalIgnoreCase) ||
                                                warning.Contains("duplicate signal", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Ambiguous duplicate message or signal declarations cannot be edited safely.");

        var changes = workingCopy.Changes.ToDictionary(
            change => (change.MessageIdentity, change.SignalName));
        foreach (var change in changes.Values)
        {
            if (!before.TryGetMessage(change.MessageIdentity.Identifier,
                    change.MessageIdentity.IsExtended, out var sourceMessage))
                throw new InvalidDataException("DBC source changed: message identity is missing.");
            if (sourceMessage.Name != change.MessageName)
                throw new InvalidDataException("DBC source changed: message name no longer matches.");
            var sourceSignal = sourceMessage.Signals.SingleOrDefault(signal =>
                signal.Name == change.SignalName);
            if (sourceSignal is null || !SameSignal(sourceSignal, change.Before))
                throw new InvalidDataException(
                    $"DBC source changed since loading signal {change.SignalName}; reload before saving.");
        }

        var scalarChanges = changes.Where(item =>
            !SameScalarFields(item.Value.Before, item.Value.After))
            .ToDictionary(item => item.Key, item => item.Value);
        var valueChanges = changes.Where(item =>
            !SameValueTable(item.Value.Before.ValueDescriptions, item.Value.After.ValueDescriptions))
            .ToDictionary(item => item.Key, item => item.Value);

        var patched = new HashSet<(DbcMessageIdentity, string)>();
        var lines = originalText.Split('\n').ToList();
        DbcMessageIdentity? scope = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var physicalLine = lines[i];
            var originalLine = physicalLine.TrimEnd('\r');
            var lineEnding = physicalLine[originalLine.Length..];
            var header = MessageLine.Match(originalLine);
            if (header.Success)
            {
                if (!uint.TryParse(header.Groups["id"].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var rawId))
                    throw new InvalidDataException("DBC message identifier exceeds UInt32.");
                var extended = (rawId & 0x8000_0000u) != 0;
                scope = new DbcMessageIdentity(
                    extended ? rawId & 0x1FFF_FFFFu : rawId, extended);
                continue;
            }
            if (scope is not { } identity || scalarChanges.Count == 0) continue;
            var signal = SignalLine.Match(originalLine);
            if (!signal.Success) continue;
            var key = (identity, signal.Groups["name"].Value);
            if (!scalarChanges.TryGetValue(key, out var change)) continue;
            if (!patched.Add(key))
                throw new InvalidDataException("Multiple SG_ source records match one edited signal.");

            var after = change.After;
            var byteOrder = after.ByteOrder == DbcByteOrder.Intel ? 1 : 0;
            var sign = after.IsSigned ? '-' : '+';
            var unit = after.Unit.Replace(@"\", @"\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
            var receiver = signal.Groups["receiver"].Value;
            lines[i] = signal.Groups["prefix"].Value +
                $"{after.StartBit}|{after.BitLength}@{byteOrder}{sign} " +
                $"({Numeric(after.Factor)},{Numeric(after.Offset)}) " +
                $"[{Numeric(after.Minimum)}|{Numeric(after.Maximum)}] " +
                $"\"{unit}\" {receiver}" + signal.Groups["suffix"].Value + lineEnding;
        }
        if (patched.Count != scalarChanges.Count)
            throw new InvalidDataException(
                "One or more staged signals do not have a unique, writable SG_ source line.");

        PatchValueTables(lines, valueChanges);
        var output = string.Join('\n', lines);
        var parsedOutput = new DbcParser().Parse("output-check", new StringReader(output));
        VerifySemantics(before, parsedOutput, changes);
        return output;
    }

    /// <summary>
    /// Save to a new UTF-8 DBC path, never overwriting the original or an
    /// existing target. Unsupported legacy code pages are rejected, not mangled.
    /// </summary>
    public static void WriteNewFile(string sourcePath, string destinationPath, DbcWorkingCopy workingCopy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(workingCopy);

        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Save As must use a different filename; the original DBC is read-only.");
        if (!string.Equals(Path.GetExtension(source), ".dbc", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(destination), ".dbc", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("This source-preserving writer only supports DBC to DBC.");
        if (File.Exists(destination))
            throw new IOException("The destination already exists. Select an unused filename to avoid overwriting.");
        if (new FileInfo(source).Length > MaximumInputBytes)
            throw new NotSupportedException("DBC exceeds the verified 64 MiB Save As limit.");

        var bytes = File.ReadAllBytes(source);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var utf8 = new UTF8Encoding(false, true);
        var input = utf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var output = Generate(input, workingCopy);
        var generated = new UTF8Encoding(bom).GetPreamble()
            .Concat(new UTF8Encoding(false).GetBytes(output)).ToArray();

        var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(generated);
                stream.Flush(flushToDisk: true);
            }
            // No overwrite: another process creating the destination wins safely.
            File.Move(temporary, destination);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { /* Preserve primary error; never touch the source. */ }
        }
    }


    /// <summary>Patch only exact full-identity VAL_ definitions; preserve every
    /// other statement byte-for-byte. New tables are appended as new records.
    /// A source table with ambiguous, duplicate or unterminated declarations
    /// fails closed before writing the new file.</summary>
    private static void PatchValueTables(
        List<string> lines,
        IReadOnlyDictionary<(DbcMessageIdentity Identity, string Signal), DbcSignalChange> changes)
    {
        if (changes.Count == 0) return;
        var found = new HashSet<(DbcMessageIdentity, string)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var match = ValueLine.Match(lines[i].TrimEnd('\r'));
            if (!match.Success) continue;
            if (!uint.TryParse(match.Groups["id"].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var rawId))
                throw new InvalidDataException("VAL_ source identifier is not UInt32.");
            var extended = (rawId & 0x8000_0000u) != 0;
            var identity = new DbcMessageIdentity(
                extended ? rawId & 0x1FFF_FFFFu : rawId, extended);
            var key = (identity, match.Groups["signal"].Value);
            if (!changes.TryGetValue(key, out var change)) continue;
            if (!found.Add(key))
                throw new InvalidDataException("Multiple VAL_ declarations match a single edited signal.");

            // DBC comments or value descriptions can contain escaped quotes.
            // Locate the real semicolon, never one inside a quoted value.
            var end = i;
            while (end < lines.Count &&
                   !HasStatementTerminator(string.Join("\n", lines.GetRange(i, end - i + 1))))
            {
                if (end - i >= 256)
                    throw new InvalidDataException("VAL_ statement exceeds the safe 256-line span.");
                end++;
            }
            if (end == lines.Count)
                throw new InvalidDataException("Unterminated VAL_ definition; cannot safely rewrite.");
            var originalEnd = lines[end].EndsWith('\r') ? "\r" : string.Empty;
            var indent = lines[i][..(lines[i].Length - lines[i].TrimStart(' ', '\t').Length)];
            lines.RemoveRange(i, end - i + 1);
            if ((change.After.ValueDescriptions?.Count ?? 0) > 0)
            {
                lines.Insert(i, FormatValueLine(indent, rawId, change.SignalName,
                    change.After.ValueDescriptions!) + originalEnd);
            }
            else
            {
                i--; // Keep the source's other records untouched; remove only this VAL_.
            }
        }

        foreach (var (key, change) in changes)
        {
            if (found.Contains(key) || (change.After.ValueDescriptions?.Count ?? 0) == 0)
                continue;
            var rawId = key.Identity.Identifier |
                (key.Identity.IsExtended ? 0x8000_0000u : 0u);
            var resultLine = FormatValueLine("", rawId, key.Signal, change.After.ValueDescriptions!);
            var crlf = lines.Any(row => row.EndsWith('\r'));
            var ending = crlf ? "\r" : "";
            if (lines.Count > 0 && lines[^1].Length != 0 &&
                !lines[^1].EndsWith('\r'))
            {
                // Existing input had no newline at EOF. Preserve it before the
                // appended statement, which is a necessary, explicit change.
                lines.Add(resultLine);
            }
            else
            {
                if (lines.Count > 0 && lines[^1].Length == 0)
                    lines.Insert(lines.Count - 1, resultLine + ending);
                else
                    lines.Add(resultLine + ending);
            }
        }
    }

    private static string FormatValueLine(string indent, uint rawId, string name,
        IReadOnlyDictionary<long, string> values)
    {
        var pairs = string.Join(" ", values.OrderBy(pair => pair.Key)
            .Select(pair => pair.Key.ToString(CultureInfo.InvariantCulture) +
                " \"" + pair.Value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal) + "\""));
        return indent + "VAL_ " + rawId.ToString(CultureInfo.InvariantCulture) +
            " " + name + " " + pairs + " ;";
    }

    private static bool HasStatementTerminator(string text)
    {
        var quoted = false;
        var escaped = false;
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (escaped) { escaped = false; continue; }
            if (quoted && character == '\\') { escaped = true; continue; }
            if (character == '"') quoted = !quoted;
            else if (character == ';' && !quoted)
                return text[(i + 1)..].Trim().Length == 0;
        }
        return false;
    }

    private static bool SameScalarFields(DbcSignal old, DbcSignal next) =>
        old.StartBit == next.StartBit && old.BitLength == next.BitLength &&
        old.ByteOrder == next.ByteOrder && old.IsSigned == next.IsSigned &&
        old.Factor == next.Factor && old.Offset == next.Offset &&
        old.Minimum == next.Minimum && old.Maximum == next.Maximum &&
        old.Unit == next.Unit;

    private static bool SameValueTable(IReadOnlyDictionary<long, string>? before,
        IReadOnlyDictionary<long, string>? after)
    {
        if ((before?.Count ?? 0) != (after?.Count ?? 0)) return false;
        return before is null || before.All(pair =>
            after!.TryGetValue(pair.Key, out var label) && label == pair.Value);
    }

    private static string Numeric(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static void VerifySemantics(DbcDatabase before, DbcDatabase after,
        IReadOnlyDictionary<(DbcMessageIdentity Identity, string Signal), DbcSignalChange> changes)
    {
        if (before.MessageDefinitionCount != after.MessageDefinitionCount)
            throw new InvalidDataException("Save As would change the message catalog.");
        foreach (var original in before.AllMessageDefinitions)
        {
            if (!after.TryGetMessage(original.Identifier, original.IsExtended, out var revised) ||
                original.Name != revised.Name || original.Length != revised.Length ||
                original.Transmitter != revised.Transmitter ||
                original.NominalCycleTimeMilliseconds != revised.NominalCycleTimeMilliseconds ||
                original.Comment != revised.Comment ||
                !original.SourceAttributes.SequenceEqual(revised.SourceAttributes) ||
                original.Signals.Count != revised.Signals.Count)
                throw new InvalidDataException("Save As would change unrelated message metadata.");
            var identity = new DbcMessageIdentity(original.Identifier, original.IsExtended);
            for (var index = 0; index < original.Signals.Count; index++)
            {
                var oldSignal = original.Signals[index];
                var expected = changes.TryGetValue((identity, oldSignal.Name), out var change)
                    ? change.After : oldSignal;
                if (!SameSignal(expected, revised.Signals[index]))
                    throw new InvalidDataException(
                        $"Save As would change unexpected data in {original.Name}.{oldSignal.Name}.");
            }
        }
        if (!before.RawAttributeDeclarations.SequenceEqual(after.RawAttributeDeclarations))
            throw new InvalidDataException("Save As would change DBC attribute declarations.");
    }

    private static bool SameSignal(DbcSignal a, DbcSignal b)
    {
        if (a.Name != b.Name || a.StartBit != b.StartBit || a.BitLength != b.BitLength ||
            a.ByteOrder != b.ByteOrder || a.IsSigned != b.IsSigned ||
            a.Factor != b.Factor || a.Offset != b.Offset ||
            a.Minimum != b.Minimum || a.Maximum != b.Maximum ||
            a.Unit != b.Unit || a.Receiver != b.Receiver ||
            a.IsMultiplexor != b.IsMultiplexor || a.MultiplexValue != b.MultiplexValue ||
            a.Comment != b.Comment || a.ValueType != b.ValueType ||
            !a.SourceAttributes.SequenceEqual(b.SourceAttributes))
            return false;
        var aValues = a.ValueDescriptions?.OrderBy(pair => pair.Key).ToArray() ?? [];
        var bValues = b.ValueDescriptions?.OrderBy(pair => pair.Key).ToArray() ?? [];
        if (!aValues.SequenceEqual(bValues)) return false;
        var ac = a.MultiplexConditions ?? [];
        var bc = b.MultiplexConditions ?? [];
        return ac.Count == bc.Count && ac.Zip(bc).All(pair =>
            pair.First.Multiplexor == pair.Second.Multiplexor &&
            pair.First.Ranges.SequenceEqual(pair.Second.Ranges));
    }
}
