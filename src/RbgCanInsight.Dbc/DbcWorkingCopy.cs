namespace RbgCanInsight.Dbc;

/// <summary>Validated in-memory modification. No source file is ever overwritten.
/// The first increment edits scalar signal encoding while keeping identity and
/// unsupported/vendor-specific metadata unchanged; full DBC persistence follows
/// a verified source-preserving round trip in a separate milestone.</summary>
public sealed record DbcSignalChange(
    DbcMessageIdentity MessageIdentity,
    string MessageName,
    string SignalName,
    DbcSignal Before,
    DbcSignal After);

/// <summary>Non-destructive engineering working copy for reviewed DBC modifications.
/// Its snapshots can be used for a diff/preview before the user authorizes a
/// future save-as flow. Source DbcDatabase and all its definitions remain immutable.</summary>
public sealed class DbcWorkingCopy
{
    private readonly DbcDatabase _original;
    internal DbcDatabase OriginalDatabase => _original;
    private readonly Dictionary<DbcMessageIdentity, DbcMessage> _originalMessages;
    private readonly Dictionary<DbcMessageIdentity, DbcMessage> _messages;
    private readonly Dictionary<(DbcMessageIdentity Identity, string Signal), DbcSignalChange> _pending = [];

    // Each accepted staging operation keeps one immutable message before/after.
    // This avoids copying a large DBC for every undoable edit.
    private const int MaxHistoryEntries = 128;
    private sealed record HistoryEntry(
        DbcMessageIdentity Identity,
        (DbcMessageIdentity Identity, string Signal) Key,
        DbcMessage BeforeMessage,
        DbcMessage AfterMessage,
        DbcSignalChange? BeforeChange,
        DbcSignalChange? AfterChange);
    private readonly List<HistoryEntry> _undo = [];
    private readonly List<HistoryEntry> _redo = [];

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Restore the previous validated staged change, including VAL_
    /// tables, without altering the source DBC or active decoder.</summary>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Restore(entry.Identity, entry.Key, entry.BeforeMessage, entry.BeforeChange);
        _redo.Add(entry);
        return true;
    }

    /// <summary>Redo the most recently undone stage, unless a newer stage
    /// invalidated that redo branch.</summary>
    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Restore(entry.Identity, entry.Key, entry.AfterMessage, entry.AfterChange);
        _undo.Add(entry);
        return true;
    }

    private void Restore(DbcMessageIdentity identity,
        (DbcMessageIdentity Identity, string Signal) key,
        DbcMessage message, DbcSignalChange? pending)
    {
        _messages[identity] = message;
        if (pending is null) _pending.Remove(key);
        else _pending[key] = pending;
    }

    public DbcWorkingCopy(DbcDatabase original)
    {
        _original = original ?? throw new ArgumentNullException(nameof(original));
        _originalMessages = original.AllMessageDefinitions.ToDictionary(
            message => new DbcMessageIdentity(message.Identifier, message.IsExtended));
        _messages = new Dictionary<DbcMessageIdentity, DbcMessage>(_originalMessages);
    }

    public bool IsModified => _pending.Count > 0;

    /// <summary>O(1) identity-safe working-message lookup, avoiding a full DBC
    /// snapshot for every WPF signal selection in large catalogs.</summary>
    public bool TryGetMessage(DbcMessageIdentity identity, out DbcMessage message) =>
        _messages.TryGetValue(identity, out message!);
    public IReadOnlyList<DbcSignalChange> Changes =>
        _pending.Values.OrderBy(change => change.MessageIdentity.Identifier)
            .ThenBy(change => change.MessageIdentity.IsExtended)
            .ThenBy(change => change.SignalName, StringComparer.Ordinal).ToArray();

    public void StageSignal(DbcMessageIdentity identity, string signalName, DbcSignal proposed) =>
        StageSignalCore(identity, signalName, proposed, allowValueTable: false);

    /// <summary>Stage reviewed VAL_ raw-code meanings without changing SG_ encoding.
    /// Names remain case-sensitive, keyed by full 11/29-bit CAN identity.</summary>
    public void StageNamedValues(DbcMessageIdentity identity, string signalName,
        IReadOnlyDictionary<long, string> namedValues)
    {
        ArgumentNullException.ThrowIfNull(namedValues);
        if (!_messages.TryGetValue(identity, out var message))
            throw new ArgumentException("DBC message identity does not exist.", nameof(identity));
        var signal = message.Signals.Single(item => item.Name == signalName);
        ValidateNamedValues(signal, namedValues);
        var source = _originalMessages[identity].Signals.Single(item => item.Name == signalName);
        var copy = new Dictionary<long, string>(namedValues);
        // Preserve original references when semantics match, so a revert really
        // clears the draft instead of treating an equivalent dictionary as edited.
        IReadOnlyDictionary<long, string>? next = ValueTableEqual(source.ValueDescriptions, copy)
            ? source.ValueDescriptions : copy.Count == 0 ? null : copy;
        StageSignalCore(identity, signalName, signal with { ValueDescriptions = next },
            allowValueTable: true);
    }

    private void StageSignalCore(DbcMessageIdentity identity, string signalName,
        DbcSignal proposed, bool allowValueTable)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        if (!_messages.TryGetValue(identity, out var message))
            throw new ArgumentException("DBC message identity does not exist in this database.", nameof(identity));
        var matching = message.Signals
            .Select((signal, index) => (signal, index))
            .Where(row => string.Equals(row.signal.Name, signalName, StringComparison.Ordinal))
            .ToArray();
        if (matching.Length != 1)
            throw new ArgumentException("A uniquely named DBC signal must be selected.", nameof(signalName));

        var (current, index) = matching[0];
        var originalMessage = _originalMessages[identity];
        var originalSignal = originalMessage.Signals.Single(signal =>
            string.Equals(signal.Name, signalName, StringComparison.Ordinal));

        // Renaming and editing mux-controller/VAL_/OEM attributes will be separate,
        // cross-reference-aware operations. Never silently break their references.
        if (!string.Equals(proposed.Name, current.Name, StringComparison.Ordinal) ||
            proposed.Receiver != current.Receiver ||
            proposed.IsMultiplexor != current.IsMultiplexor ||
            proposed.MultiplexValue != current.MultiplexValue ||
            proposed.ValueType != current.ValueType ||
            !ReferenceEquals(proposed.MultiplexConditions, current.MultiplexConditions) ||
            (!allowValueTable && !ReferenceEquals(proposed.ValueDescriptions, current.ValueDescriptions)) ||
            !ReferenceEquals(proposed.Attributes, current.Attributes) ||
            proposed.Comment != current.Comment)
            throw new InvalidOperationException(
                "This editing stage permits only bit layout, encoding, scale, range and unit; " +
                "renames, multiplexing, value tables and OEM metadata need cross-reference editing.");

        ValidateScalarEncoding(message, proposed);
        ValidateNamedValues(proposed, proposed.ValueDescriptions ?? new Dictionary<long, string>());
        if (proposed == current) return; // no-op does not enter history
        var revised = message.Signals.ToArray();
        revised[index] = proposed;
        var key = (identity, signalName);
        _pending.TryGetValue(key, out var pendingBefore);
        var afterMessage = message with { Signals = revised };
        _messages[identity] = afterMessage;
        if (proposed == originalSignal)
            _pending.Remove(key);
        else
            _pending[key] = new DbcSignalChange(identity, message.Name, signalName, originalSignal, proposed);
        _pending.TryGetValue(key, out var pendingAfter);
        _undo.Add(new HistoryEntry(identity, key, message, afterMessage, pendingBefore, pendingAfter));
        if (_undo.Count > MaxHistoryEntries) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public void RevertSignal(DbcMessageIdentity identity, string signalName)
    {
        if (!_messages.TryGetValue(identity, out var current))
            throw new ArgumentException("Message identity is not present.", nameof(identity));
        var originalMessage = _originalMessages[identity];
        var original = originalMessage.Signals.Single(signal =>
            string.Equals(signal.Name, signalName, StringComparison.Ordinal));
        var currentSignal = current.Signals.Single(signal =>
            string.Equals(signal.Name, signalName, StringComparison.Ordinal));
        if (original == currentSignal) return;
        StageSignalCore(identity, signalName, original, allowValueTable: true);
    }

    public void RevertAll()
    {
        _messages.Clear();
        foreach (var (identity, message) in _originalMessages)
            _messages.Add(identity, message);
        _pending.Clear();
        // Discard All is an explicit irreversible draft reset.
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>Creates a new DbcDatabase snapshot with exact standard/extended identity;
    /// no shared message collection is mutated. Not a source-file save operation.</summary>
    public DbcDatabase Preview()
    {
        var identities = _messages.ToDictionary(entry => entry.Key, entry => entry.Value);
        var legacy = new Dictionary<uint, DbcMessage>();
        foreach (var message in identities.Values.OrderBy(message => message.IsExtended))
            legacy.TryAdd(message.Identifier, message);
        return new DbcDatabase(_original.Name, legacy, _original.Warnings,
            identities, _original.AttributeDeclarations);
    }


    private static bool ValueTableEqual(
        IReadOnlyDictionary<long, string>? left, IReadOnlyDictionary<long, string>? right)
    {
        if ((left?.Count ?? 0) != (right?.Count ?? 0)) return false;
        if (left is null || left.Count == 0) return true;
        return left.All(item => right!.TryGetValue(item.Key, out var meaning) &&
            string.Equals(meaning, item.Value, StringComparison.Ordinal));
    }

    private static void ValidateNamedValues(DbcSignal signal,
        IReadOnlyDictionary<long, string> values)
    {
        if (values.Count > 4_096)
            throw new ArgumentException("No more than 4096 named raw codes may be staged per signal.");
        if (signal.ValueType != DbcSignalValueType.Integer && values.Count != 0)
            throw new ArgumentException("VAL_ raw-code meanings require integer signal encoding.");
        foreach (var (code, meaning) in values)
        {
            if (meaning is null || meaning.Length > 4_096 ||
                meaning.Any(character => character is '\0' or '\r' or '\n' ||
                    character is < ' ' and not '\t'))
                throw new ArgumentException("VAL_ labels must be valid, single-line text under 4096 characters.");
            if (signal.BitLength is < 1 or > 64)
                throw new ArgumentException("Invalid signal width for VAL_ validation.");
            var representable = signal.IsSigned
                ? signal.BitLength == 64 ||
                    (code >= -(1L << (signal.BitLength - 1)) &&
                     code < (1L << (signal.BitLength - 1)))
                : code >= 0 &&
                  (signal.BitLength >= 63 || (ulong)code < (1UL << signal.BitLength));
            if (!representable)
                throw new ArgumentException(
                    $"Raw code {code} cannot be represented by this signal's {signal.BitLength}-bit encoding.");
        }
    }

    private static void ValidateScalarEncoding(DbcMessage message, DbcSignal signal)
    {
        if (message.Length is < 1 or > 64)
            throw new ArgumentException("DBC message DLC must be between 1 and 64 bytes.");
        if (signal.StartBit < 0 || signal.BitLength is < 1 or > 64)
            throw new ArgumentException("Signal start bit and length must be within supported bounds.");
        if (!double.IsFinite(signal.Factor) || !double.IsFinite(signal.Offset) ||
            !double.IsFinite(signal.Minimum) || !double.IsFinite(signal.Maximum))
            throw new ArgumentException("Signal scaling and physical limits must be finite.");
        if (signal.Factor == 0)
            throw new ArgumentException("DBC physical scaling factor must not be zero.");
        if (signal.Unit is null || signal.Unit.Any(char.IsControl))
            throw new ArgumentException("Signal units cannot contain DBC control characters.");
        if (signal.Minimum > signal.Maximum)
            throw new ArgumentException("Physical minimum cannot exceed maximum.");
        if ((signal.ValueType == DbcSignalValueType.Float32 && signal.BitLength != 32) ||
            (signal.ValueType == DbcSignalValueType.Float64 && signal.BitLength != 64))
            throw new ArgumentException("Floating-point signal size must match its encoded type.");

        var positions = DbcSignalInspector.PhysicalBits(signal);
        if (positions.Count != signal.BitLength ||
            positions.Any(position => position < 0 || position >= message.Length * 8))
            throw new ArgumentException(
                "Signal occupies bits outside the declared message DLC, including Motorola bit order.");
    }
}
