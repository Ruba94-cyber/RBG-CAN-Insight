using System.Globalization;
using RbgCanInsight.App.Common;
using RbgCanInsight.Correlation;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;

namespace RbgCanInsight.App.ViewModels;

/// <summary>How the Trace grid's Time column reads a row's timestamp.</summary>
public enum TraceTimeDisplayMode
{
    /// <summary>The timestamp as recorded in the trace file.</summary>
    Absolute,

    /// <summary>Seconds since the first frame of the whole (unfiltered) trace.</summary>
    RelativeToTraceStart,

    /// <summary>Seconds since the row immediately above this one in the grid (any identifier) —
    /// honours the active filter, so it always matches the row actually visible above it rather
    /// than the previous frame in the whole trace.</summary>
    DeltaFromPreviousFrame,

    /// <summary>Seconds since the previous frame of the same bus/identifier — reads the period
    /// and its jitter directly off the grid without opening Statistics.</summary>
    DeltaFromPreviousSameId
}

/// <summary>
/// One trace grid row, materialised on demand by <see cref="VirtualRowList"/> while scrolling.
/// Message name and decoded signal summary come from the loaded DBC databases.
/// </summary>
public sealed class TraceRowViewModel : ObservableObject
{
    private readonly IReadOnlyList<CanFrame> _frames;
    private readonly IReadOnlyList<DbcDecoder> _decoders;
    private readonly int[] _previousSameKeyIndex;
    private readonly Func<int, MarkerCategory?> _isMarked;
    private readonly Func<int, string?>? _rowColour;
    private bool _rowColourResolved;
    private System.Windows.Media.Brush? _rowColourBrush;
    private readonly Action<int, bool> _setExpanded;
    private readonly Func<int, string?>? _didNameResolver;
    private readonly Func<int, uint, bool, bool>? _isDiagnosticId;
    private readonly TraceTimeDisplayMode _timeDisplayMode;
    private readonly long _traceStartTimestampNanoseconds;
    private readonly int _previousRowFrameIndex;
    private string? _signalsSummary;
    private string? _udsText;
    private TracePayloadDiff? _payloadDiff;
    private IReadOnlyList<TracePayloadByteViewModel>? _sourceTaggedPayloadBytes;
    private bool _isExpanded;

    /// <summary>Multiplexed signals may skip occurrences; cap the back-walk so it stays O(1)-ish.</summary>
    private const int MaxPreviousLookback = 64;

    public TraceRowViewModel(
        int frameIndex,
        IReadOnlyList<CanFrame> frames,
        IReadOnlyList<DbcDecoder> decoders,
        int[] previousSameKeyIndex,
        Func<int, MarkerCategory?>? isMarked = null,
        Func<int, bool>? isExpanded = null,
        Action<int, bool>? setExpanded = null,
        Func<int, string?>? didNameResolver = null,
        Func<int, uint, bool, bool>? isDiagnosticId = null,
        TraceTimeDisplayMode timeDisplayMode = TraceTimeDisplayMode.Absolute,
        long traceStartTimestampNanoseconds = 0,
        int previousRowFrameIndex = -1,
        Func<int, string?>? rowColour = null,
        Func<int, string?>? sourceFile = null,
        Func<int, string?>? sourceEvidence = null)
    {
        _rowColour = rowColour;
        _frames = frames;
        _decoders = decoders;
        _previousSameKeyIndex = previousSameKeyIndex;
        _isMarked = isMarked ?? (_ => null);
        _setExpanded = setExpanded ?? ((_, _) => { });
        _didNameResolver = didNameResolver;
        _isDiagnosticId = isDiagnosticId;
        _timeDisplayMode = timeDisplayMode;
        _traceStartTimestampNanoseconds = traceStartTimestampNanoseconds;
        _previousRowFrameIndex = previousRowFrameIndex;
        FrameIndex = frameIndex;
        Frame = frames[frameIndex];
        SourceFile = sourceFile?.Invoke(frameIndex) ?? string.Empty;
        SourceEvidenceInfo = sourceEvidence?.Invoke(frameIndex) ?? SourceFile;
        _isExpanded = isExpanded?.Invoke(frameIndex) == true;
        var message = FindMessage();
        MessageName = message?.Name ?? string.Empty;
        Ecu = message?.Transmitter ?? string.Empty;
        MessageComment = message?.Comment ?? string.Empty;
    }

    private int PreviousSameKeyIndex =>
        FrameIndex < _previousSameKeyIndex.Length ? _previousSameKeyIndex[FrameIndex] : -1;

    /// <summary>Tint from the first matching saved-query colour rule, or null. Resolved once per row on demand.</summary>
    public System.Windows.Media.Brush? RowColourBrush
    {
        get
        {
            if (!_rowColourResolved)
            {
                _rowColourResolved = true;
                _rowColourBrush = RowColourBrushes.For(_rowColour?.Invoke(FrameIndex));
            }

            return _rowColourBrush;
        }
    }

    public bool HasRowColour => RowColourBrush is not null;

    public int FrameIndex { get; }
    /// <summary>Original source name, not an inferred CAN channel or DBC source.</summary>
    public string SourceFile { get; }
    /// <summary>Full physical file identity and source-frame position, not a
    /// guessed CAN database source or an unverified byte offset.</summary>
    public string SourceEvidenceInfo { get; }
    public CanFrame Frame { get; }
    public long TimestampNanoseconds => Frame.TimestampNanoseconds;

    public string TimeText => _timeDisplayMode switch
    {
        TraceTimeDisplayMode.RelativeToTraceStart =>
            FormatSeconds(Frame.TimestampNanoseconds - _traceStartTimestampNanoseconds),
        TraceTimeDisplayMode.DeltaFromPreviousFrame => _previousRowFrameIndex >= 0
            ? FormatDelta(Frame.TimestampNanoseconds - _frames[_previousRowFrameIndex].TimestampNanoseconds)
            : "first",
        TraceTimeDisplayMode.DeltaFromPreviousSameId => PreviousSameKeyIndex >= 0
            ? FormatDelta(Frame.TimestampNanoseconds - _frames[PreviousSameKeyIndex].TimestampNanoseconds)
            : "first",
        _ => FormatSeconds(Frame.TimestampNanoseconds)
    };

    private static string FormatSeconds(long nanoseconds) =>
        (nanoseconds / 1_000_000_000d).ToString("F6", CultureInfo.InvariantCulture);

    private static string FormatDelta(long deltaNanoseconds) =>
        (deltaNanoseconds / 1_000_000_000d).ToString("+0.000000;-0.000000;0.000000", CultureInfo.InvariantCulture);
    public int Channel => Frame.Channel + 1;
    public string IdentifierHex =>
        Frame.Identifier.ToString(Frame.IsExtended ? "X8" : "X3", CultureInfo.InvariantCulture) +
        (Frame.IsExtended ? "x" : string.Empty);
    /// <summary>Decode the numeric identifier into J1939 fields for inspection
    /// only. 29-bit IDs are not assumed to originate from a J1939 network.</summary>
    public string IdentifierDetailTooltip => !Frame.IsExtended
        ? $"Standard CAN 11-bit ID 0x{Frame.Identifier:X3}"
        : $"Extended CAN 29-bit ID 0x{Frame.Identifier:X8}" + Environment.NewLine +
          J1939.Explain(Frame.Identifier).Summary +
          Environment.NewLine + "Only applicable when this network uses SAE J1939.";
    public string MessageName { get; }
    public string Ecu { get; }
    public string MessageComment { get; }
    // The Trace DLC column describes the recorded requested length, not
    // the number of data bytes in an RTR (which must always be zero).
    public int Length => Frame.DeclaredDataLength;
    public string LengthTooltip => Frame.Flags.HasFlag(CanFrameFlags.Remote)
        ? $"Remote (RTR): DLC {Frame.DeclaredDataLength}; zero payload bytes. " +
          "DLC 0 may also mean the source omitted its requested length."
        : $"Recorded payload: {Frame.DataLength} byte(s).";
    public string PayloadHex => string.Join(' ', Frame.GetPayload().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    public string PreviousPayloadHex => PayloadDiff.PreviousPayload;
    public string ChangedBytesText => PayloadDiff.ChangedBytes;
    public string PayloadDeltaText => PayloadDiff.PayloadDelta;
    public IReadOnlyList<TracePayloadByteViewModel> PayloadBytes =>
        _sourceTaggedPayloadBytes ?? PayloadDiff.Bytes;

    /// <summary>Remove an old diagnostic overlay when the analyst jumps
    /// to another source. The underlying CAN payload remains immutable.</summary>
    public void ClearOriginalSourceBytes()
    {
        if (_sourceTaggedPayloadBytes is null)
            return;
        _sourceTaggedPayloadBytes = null;
        OnPropertyChanged(nameof(PayloadBytes));
    }

    /// <summary>Mark only the immutable original recorded bytes of the
    /// selected DID subparameter. This overlay never changes the capture,
    /// markers, comparison data or DBC interpretation.</summary>
    public bool HighlightOriginalSourceBytes(int byteOffset, int byteCount)
    {
        if (byteOffset < 0 || byteCount <= 0 ||
            (long)byteOffset + byteCount > Frame.DataLength)
            return false;

        _sourceTaggedPayloadBytes = PayloadDiff.Bytes
            .Select(value => value with
            {
                IsSourceByte = value.Index >= byteOffset &&
                               (long)value.Index < (long)byteOffset + byteCount
            }).ToArray();
        OnPropertyChanged(nameof(PayloadBytes));
        IsExpanded = true;
        return true;
    }
    public bool IsChanged => PayloadDiff.IsChanged;
    public MarkerCategory? Category => _isMarked(FrameIndex);
    public bool IsMarked => Category is not null;
    public string MarkerText => Category switch
    {
        MarkerCategory.Manual => "MAN",
        MarkerCategory.Timing => "TIM",
        MarkerCategory.Diagnostic => "DIA",
        MarkerCategory.Range => "RNG",
        _ => string.Empty
    };
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                _setExpanded(FrameIndex, value);
            }
        }
    }
    /// <summary>"Tx" when the log marks the frame as transmitted; blank for Rx or when the log has no direction.</summary>
    public string DirectionText => Frame.IsTransmit ? "Tx" : string.Empty;

    public string FlagsText =>
        Frame.IsError ? "ERR" :
        Frame.Flags.HasFlag(CanFrameFlags.FlexibleDataRate) ? "FD" :
        Frame.Flags.HasFlag(CanFrameFlags.Remote) ? "RTR" : string.Empty;

    /// <summary>Compact "Signal=Value unit" summary, decoded lazily the first time the cell shows.</summary>
    public string SignalsSummary => _signalsSummary ??= BuildSignalsSummary();

    /// <summary>Best-effort ISO-TP/UDS reading of this frame ("UDS Req ReadDataByIdentifier — DID
    /// 0xF190 (VIN)"); empty when the frame does not look like diagnostic traffic. DID names come
    /// from the loaded CDD/ODX catalogs when available.</summary>
    public string UdsText => _udsText ??= DescribeUds();

    private string DescribeUds()
    {
        Span<byte> payloadBuffer = stackalloc byte[64];
        Frame.CopyPayloadTo(payloadBuffer[..Frame.DataLength]);
        return UdsFrameInterpreter.Describe(
            payloadBuffer[..Frame.DataLength],
            Frame.Identifier,
            Frame.IsExtended,
            _didNameResolver,
            assumeDiagnostic: _isDiagnosticId?.Invoke(Frame.Channel, Frame.Identifier, Frame.IsExtended) ?? false) ?? string.Empty;
    }

    public string InterpretationText
    {
        get
        {
            // Diagnostic frames rarely have a DBC message; the UDS reading is the interpretation.
            if (string.IsNullOrWhiteSpace(MessageName) && UdsText.Length > 0)
            {
                return UdsText;
            }

            if (_decoders.Count == 0)
            {
                return "Load DBC to interpret";
            }

            if (string.IsNullOrWhiteSpace(MessageName))
            {
                return "No DBC message for this ID";
            }

            return string.IsNullOrWhiteSpace(SignalsSummary)
                ? "Message matched, no extractable signals"
                : SignalsSummary;
        }
    }

    public IReadOnlyList<SignalDetailRow> DetailedSignals => DecodeDetailedSignals();

    public IReadOnlyList<SignalValue> DecodeSignals()
    {
        List<SignalValue>? allValues = null;
        foreach (var decoder in _decoders)
        {
            var values = decoder.Decode(Frame);
            if (values.Count > 0)
            {
                allValues ??= [];
                allValues.AddRange(values);
            }
        }

        if (allValues is null)
        {
            return [];
        }

        return allValues
            .GroupBy(value => (value.Network, value.Message, value.Signal))
            .Select(group => group.First())
            .ToArray();
    }

    public IReadOnlyList<SignalDetailRow> DecodeDetailedSignals()
    {
        var current = DecodeSignals();
        if (current.Count == 0)
        {
            return [];
        }

        var previousValues = CollectPreviousSignalValues(current);
        return current.Select(value =>
        {
            var identity = (value.Network, value.Message, value.Signal);
            SignalValue? previous = previousValues.TryGetValue(identity, out var found) ? found : null;
            var signal = FindSignal(value.Network, value.Message, value.Signal);
            var delta = previous is { } before
                ? value.PhysicalValue - before.PhysicalValue
                : (double?)null;
            var previousText = previous is { } previousValue
                ? FormatPhysical(previousValue.PhysicalValue, value.Unit)
                : "first value for this signal";
            return new SignalDetailRow(
                Ecu,
                value.Message,
                value.Signal,
                value.PhysicalValue.ToString("G6", CultureInfo.InvariantCulture),
                value.RawValue.ToString("G6", CultureInfo.InvariantCulture),
                value.Unit,
                signal?.GetValueDescription(value.RawValue) ?? string.Empty,
                previousText,
                delta is null ? string.Empty : delta.Value.ToString("+0.#####;-0.#####;0", CultureInfo.InvariantCulture),
                delta is not null && Math.Abs(delta.Value) > double.Epsilon,
                value.FrameIdentifier,
                value.Network, value.Channel, Frame.IsExtended,
                FindDefinitionIdentifier(value.Network, value.Message),
                Frame.TimestampNanoseconds);
        }).ToArray();
    }

    /// <summary>
    /// Decodes previous same-bus/ID frames (via the precomputed index chain, newest first) until
    /// a previous value is known for every current signal or the lookback cap is reached.
    /// </summary>
    private Dictionary<(string Database, string Message, string Signal), SignalValue> CollectPreviousSignalValues(IReadOnlyList<SignalValue> current)
    {
        var previousValues = new Dictionary<(string Database, string Message, string Signal), SignalValue>(current.Count);
        var index = PreviousSameKeyIndex;
        var hops = 0;
        while (index >= 0 && previousValues.Count < current.Count && hops < MaxPreviousLookback)
        {
            var frame = _frames[index];
            foreach (var decoder in _decoders)
            {
                foreach (var value in decoder.Decode(frame))
                {
                    var key = (value.Network, value.Message, value.Signal);
                    if (!previousValues.ContainsKey(key))
                    {
                        previousValues[key] = value;
                    }
                }
            }

            index = index < _previousSameKeyIndex.Length ? _previousSameKeyIndex[index] : -1;
            hops++;
        }

        return previousValues;
    }

    private static string FormatPhysical(double value, string unit)
    {
        var suffix = string.IsNullOrWhiteSpace(unit) ? string.Empty : $" {unit}";
        return $"{value.ToString("G6", CultureInfo.InvariantCulture)}{suffix}";
    }

    private DbcMessage? FindMessage()
    {
        foreach (var decoder in _decoders)
        {
            if (decoder.TryGetMessage(Frame, out var message))
            {
                return message;
            }
        }

        return null;
    }

    /// <summary>The DBC message ID may differ from the recorded wire ID when
    /// PGN matching is enabled for a J1939 database. Resolve the definition
    /// through the same active decoder profile as the actual trace frame.</summary>
    private uint? FindDefinitionIdentifier(string databaseName, string messageName)
    {
        uint? found = null;
        foreach (var decoder in _decoders)
        {
            if (!string.Equals(decoder.Database.Name, databaseName, StringComparison.OrdinalIgnoreCase) ||
                !decoder.TryGetMessage(Frame, out var message) ||
                !string.Equals(message.Name, messageName, StringComparison.Ordinal))
                continue;
            if (found is not null && found.Value != message.Identifier)
                return null; // Do not invent a unique source definition.
            found = message.Identifier;
        }
        return found;
    }

    private DbcSignal? FindSignal(string databaseName, string messageName, string signalName)
    {
        foreach (var decoder in _decoders)
        {
            if (!string.Equals(decoder.Database.Name, databaseName, StringComparison.OrdinalIgnoreCase) ||
                !decoder.TryGetMessage(Frame, out var message) ||
                message.Name != messageName)
                continue;
            var signal = message.Signals.FirstOrDefault(item => item.Name == signalName);
            if (signal is not null) return signal;
        }
        return null;
    }

    private TracePayloadDiff PayloadDiff => _payloadDiff ??= BuildPayloadDiff();

    private TracePayloadDiff BuildPayloadDiff()
    {
        var current = Frame.GetPayload();
        var previousIndex = PreviousSameKeyIndex;
        byte[] previous = previousIndex >= 0 ? _frames[previousIndex].GetPayload() : [];

        var previousText = previous.Length == 0
            ? "No previous same-ID payload"
            : FormatPayload(previous);
        var changed = FormatChangedBytes(previous, current);
        var delta = FormatPayloadDelta(previous, current);
        var bytes = BuildPayloadBytes(previous, current);
        return new TracePayloadDiff(previousText, changed, delta, previous.Length > 0 && changed != "none", bytes);
    }

    private static string FormatPayload(ReadOnlySpan<byte> payload) =>
        payload.Length == 0
            ? string.Empty
            : string.Join(' ', payload.ToArray().Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));

    private static string FormatChangedBytes(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current)
    {
        if (previous.Length == 0)
        {
            return "first occurrence";
        }

        var changed = new List<string>();
        var length = Math.Max(previous.Length, current.Length);
        for (var index = 0; index < length; index++)
        {
            if (index >= previous.Length || index >= current.Length || previous[index] != current[index])
            {
                changed.Add($"B{index}");
            }
        }

        return changed.Count == 0 ? "none" : string.Join(", ", changed);
    }

    private static string FormatPayloadDelta(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current)
    {
        if (previous.Length == 0)
        {
            return "First occurrence for this bus/ID.";
        }

        var deltas = new List<string>();
        var length = Math.Max(previous.Length, current.Length);
        for (var index = 0; index < length; index++)
        {
            if (index < previous.Length && index < current.Length && previous[index] == current[index])
            {
                continue;
            }

            var before = index < previous.Length ? previous[index].ToString("X2", CultureInfo.InvariantCulture) : "--";
            var after = index < current.Length ? current[index].ToString("X2", CultureInfo.InvariantCulture) : "--";
            deltas.Add($"B{index}: {before}->{after}");
        }

        return deltas.Count == 0 ? "No byte changes from previous same-ID payload." : string.Join(" | ", deltas);
    }

    private static IReadOnlyList<TracePayloadByteViewModel> BuildPayloadBytes(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current)
    {
        var length = Math.Max(previous.Length, current.Length);
        var rows = new List<TracePayloadByteViewModel>(length);
        for (var index = 0; index < length; index++)
        {
            var before = index < previous.Length ? previous[index].ToString("X2", CultureInfo.InvariantCulture) : "--";
            var after = index < current.Length ? current[index].ToString("X2", CultureInfo.InvariantCulture) : "--";
            var state = previous.Length == 0 ? "First" : before == after ? "Same" : "Changed";
            rows.Add(new TracePayloadByteViewModel(index, before, after, state));
        }

        return rows;
    }

    private string BuildSignalsSummary()
    {
        var values = DecodeSignals();
        if (values.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("  ", values.Select(value =>
        {
            var unit = string.IsNullOrWhiteSpace(value.Unit) ? string.Empty : $" {value.Unit}";
            return $"{value.Signal}={value.PhysicalValue:G5}{unit}";
        }));
    }
}

public sealed record TracePayloadDiff(
    string PreviousPayload,
    string ChangedBytes,
    string PayloadDelta,
    bool IsChanged,
    IReadOnlyList<TracePayloadByteViewModel> Bytes);

public sealed record TracePayloadByteViewModel(int Index, string Previous, string Current, string State)
{
    public bool IsSourceByte { get; init; }
    public string ByteLabel => $"B{Index}";
    public string EvidenceByteLabel => IsSourceByte
        ? $"Verified DID source byte B{Index}"
        : $"Recorded CAN byte B{Index}";
    public bool IsChanged => State == "Changed";
}

/// <summary>Frozen, shared brushes for row colour rules (#AARRGGBB).</summary>
internal static class RowColourBrushes
{
    private static readonly Dictionary<string, System.Windows.Media.Brush?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static System.Windows.Media.Brush? For(string? colour)
    {
        if (string.IsNullOrEmpty(colour)) return null;
        if (Cache.TryGetValue(colour, out var cached)) return cached;
        System.Windows.Media.Brush? brush = null;
        try
        {
            var converted = System.Windows.Media.ColorConverter.ConvertFromString(colour);
            if (converted is System.Windows.Media.Color value)
            {
                var solid = new System.Windows.Media.SolidColorBrush(value);
                solid.Freeze();
                brush = solid;
            }
        }
        catch (FormatException)
        {
        }

        Cache[colour] = brush;
        return brush;
    }
}
