using System.Globalization;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;

namespace RbgCanInsight.App.ViewModels;

public sealed record DbcAttributeInspectorRow(string Scope, string Name, string RawValue);

/// <summary>
/// A source-backed CANdb-like description of the selected DBC signal.
/// The byte/bit layout is authoritative from DBC metadata, while the last
/// recorded observation is explicitly scoped to its actual CAN interface.
/// </summary>
public sealed class DbcSignalInspectorViewModel : ObservableObject
{
    private DbcDatabase? _database;
    private DbcMessage? _message;
    private DbcSignal? _signal;
    private DbcSignalLayout? _layout;
    private string? _activeDatabaseName;
    private DbcMessageIdentity? _activeMessageIdentity;
    private int _preferredFrameIndex = -1;
    private IReadOnlyList<SignalTimeline> _matchingTimelines = [];
    private IReadOnlyList<DbcDecoder> _decoders = [];
    private IReadOnlyList<CanFrame> _frames = [];
    private int _selectedChannelIndex;
    private string _exampleText = "Load a CAN trace to see an observed value.";
    private int _sourceFrameIndex = -1;
    public int SourceFrameIndex
    {
        get => _sourceFrameIndex;
        private set => SetProperty(ref _sourceFrameIndex, value);
    }
    public int? SelectedPhysicalChannel => _selectedChannelIndex > 0
        ? _matchingTimelines[_selectedChannelIndex - 1].Channel
        : _preferredFrameIndex >= 0 && _preferredFrameIndex < _frames.Count
            ? _frames[_preferredFrameIndex].Channel
        : _matchingTimelines.Count == 1 ? _matchingTimelines[0].Channel : null;

    public bool HasSelection => _signal is not null;
    public string Header => _signal is null ? "Select a signal to inspect its bits and values"
        : $"{_message!.Name} / {_signal.Name}";
    public string SourceText => _signal is null ? "No signal selected" :
        $"DBC: {_database!.Name} · 0x{_message!.Identifier:X} " +
        (_message.IsExtended ? "(29-bit extended)" : "(11-bit standard)") +
        $" · DLC {_message.Length} · TX {_message.Transmitter} · RX {_signal.Receiver}" +
        (_message.NominalCycleTimeMilliseconds is { } ms
            ? $" · nominal cycle {ms} ms (DBC)" : " · nominal cycle not declared") +
        (_message.IsExtended
            ? " · " + J1939.Explain(_message.Identifier).Summary +
              " (only if network is J1939)" : string.Empty);

    public string ByteOrderText { get; private set; } = string.Empty;
    public string EncodingText { get; private set; } = string.Empty;
    public string BoundsText { get; private set; } = string.Empty;
    public string CodedRangeText { get; private set; } = string.Empty;
    public string FormulaText { get; private set; } = string.Empty;
    public string RangeText { get; private set; } = string.Empty;
    public string MultiplexText { get; private set; } = string.Empty;
    public string CommentText { get; private set; } = string.Empty;
    public string WarningsText { get; private set; } = string.Empty;
    public string EnumerationSummary { get; private set; } = "No symbolic value descriptions (VAL_) are defined.";
    public IReadOnlyList<DbcBitRow> BitRows { get; private set; } = [];
    public IReadOnlyList<DbcNamedValue> NamedValues { get; private set; } = [];
    public IReadOnlyList<DbcAttributeInspectorRow> SourceAttributes { get; private set; } = [];
    public string AttributeSummary => SourceAttributes.Count == 0
        ? "No BO_/SG_ source attributes recorded in this DBC definition."
        : $"{SourceAttributes.Count:N0} attributes preserved verbatim from the DBC; vendor-specific semantics are not assumed.";
    public IReadOnlyList<string> ChannelChoices { get; private set; } = ["All recorded channels"];

    public int SelectedChannelIndex
    {
        get => _selectedChannelIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, ChannelChoices.Count - 1));
            if (SetProperty(ref _selectedChannelIndex, clamped))
            {
                RefreshRecordedExample();
                OnPropertyChanged(nameof(SelectedPhysicalChannel));
            }
        }
    }

    /// <summary>Choose exactly one observed CAN interface. This is used by
    /// UDS↔CAN navigation so identical CAN IDs on other buses never become
    /// a silently selected source. A missing or duplicated observed channel
    /// is an explicit nonmatch.</summary>
    public bool TrySelectObservedChannel(int channel)
    {
        var found = -1;
        for (var index = 0; index < _matchingTimelines.Count; index++)
        {
            if (_matchingTimelines[index].Channel != channel)
                continue;
            if (found >= 0)
                return false;
            found = index;
        }
        if (found < 0)
            return false;

        SelectedChannelIndex = found + 1;
        return SelectedPhysicalChannel == channel;
    }

    public string ExampleText
    {
        get => _exampleText;
        private set => SetProperty(ref _exampleText, value);
    }

    public void Inspect(
        DbcDatabase? database,
        DbcMessage? message,
        DbcSignal? signal,
        IReadOnlyList<SignalTimeline> timelines,
        IReadOnlyList<DbcDecoder> decoders,
        IReadOnlyList<CanFrame> frames)
    {
        var identity = message is null
            ? (DbcMessageIdentity?)null : new DbcMessageIdentity(message.Identifier, message.IsExtended);
        if (!string.Equals(_activeDatabaseName, database?.Name, StringComparison.OrdinalIgnoreCase) ||
            _activeMessageIdentity != identity)
            _preferredFrameIndex = -1;
        _activeDatabaseName = database?.Name;
        _activeMessageIdentity = identity;
        _database = database;
        _message = message;
        _signal = signal;
        _frames = frames;
        _decoders = decoders;
        _matchingTimelines = [];
        _layout = message is not null && signal is not null
            ? DbcSignalInspector.Inspect(message, signal)
            : null;
        var layout = _layout;

        ByteOrderText = layout?.ByteOrderText ?? string.Empty;
        EncodingText = layout?.EncodingText ?? string.Empty;
        BoundsText = layout?.SelectedBoundsText ?? string.Empty;
        CodedRangeText = layout?.CodedRangeText ?? string.Empty;
        FormulaText = layout?.FormulaText ?? string.Empty;
        RangeText = layout?.RangeText ?? string.Empty;
        MultiplexText = layout?.MultiplexText ?? string.Empty;
        CommentText = signal?.Comment ?? string.Empty;
        WarningsText = layout?.Warnings.Count > 0
            ? string.Join(Environment.NewLine, layout.Warnings)
            : "Layout validated against the declared DBC DLC.";
        BitRows = layout?.Rows ?? [];
        NamedValues = layout?.NamedValues ?? [];
        SourceAttributes = [
            .. (message?.SourceAttributes ?? []).Select(item =>
                new DbcAttributeInspectorRow("Message", item.Name, item.RawValue)),
            .. (signal?.SourceAttributes ?? []).Select(item =>
                new DbcAttributeInspectorRow("Signal", item.Name, item.RawValue))
        ];
        EnumerationSummary = NamedValues.Count == 0
            ? "No VAL_ descriptions are declared. Do not infer the meaning of arbitrary numeric states."
            : $"{NamedValues.Count:N0} symbolic raw values declared by the DBC; all other values remain numeric.";

        if (database is not null && message is not null && signal is not null)
        {
            _matchingTimelines = timelines.Where(item =>
                string.Equals(item.DatabaseName, database.Name, StringComparison.OrdinalIgnoreCase) &&
                item.Message.Identifier == message.Identifier &&
                item.Message.IsExtended == message.IsExtended &&
                item.Signal.Name == signal.Name)
                .OrderBy(item => item.Channel)
                .ToArray();
        }

        ChannelChoices = ["All recorded channels",
            .. _matchingTimelines.Select(item => $"CAN {item.Channel + 1} · {item.Samples.Count:N0} {(item.Samples.Count == 1 ? "sample" : "samples")}")];
        _selectedChannelIndex = 0;
        OnPropertyChanged(nameof(SelectedChannelIndex));

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(ByteOrderText));
        OnPropertyChanged(nameof(EncodingText));
        OnPropertyChanged(nameof(BoundsText));
        OnPropertyChanged(nameof(CodedRangeText));
        OnPropertyChanged(nameof(FormulaText));
        OnPropertyChanged(nameof(RangeText));
        OnPropertyChanged(nameof(MultiplexText));
        OnPropertyChanged(nameof(CommentText));
        OnPropertyChanged(nameof(WarningsText));
        OnPropertyChanged(nameof(BitRows));
        OnPropertyChanged(nameof(NamedValues));
        OnPropertyChanged(nameof(SourceAttributes));
        OnPropertyChanged(nameof(AttributeSummary));
        OnPropertyChanged(nameof(EnumerationSummary));
        OnPropertyChanged(nameof(ChannelChoices));
        OnPropertyChanged(nameof(SelectedPhysicalChannel));
        RefreshRecordedExample();
    }

    /// <summary>Pin the exact frame chosen in Trace, preserving raw evidence when
    /// switching signals within the same DBC message (including inactive mux branches).</summary>
    public void PinSourceFrame(int frameIndex)
    {
        if (frameIndex < 0 || frameIndex >= _frames.Count) return;
        _preferredFrameIndex = frameIndex;
        _selectedChannelIndex = 0;
        OnPropertyChanged(nameof(SelectedChannelIndex));
        OnPropertyChanged(nameof(SelectedPhysicalChannel));
        RefreshRecordedExample();
    }

    private void RefreshRecordedExample()
    {
        SourceFrameIndex = -1;
        BitRows = _layout?.Rows ?? [];
        OnPropertyChanged(nameof(BitRows));
        if (_signal is null || _layout is null || _database is null || _message is null)
        {
            ExampleText = "Select a signal to inspect its recorded evidence.";
            return;
        }

        int frameIndex = -1;
        if (_preferredFrameIndex >= 0 && _preferredFrameIndex < _frames.Count &&
            (_selectedChannelIndex == 0 ||
             (_selectedChannelIndex <= _matchingTimelines.Count &&
              _frames[_preferredFrameIndex].Channel == _matchingTimelines[_selectedChannelIndex - 1].Channel)))
        {
            // A user-selected correlation source frame takes precedence over
            // the arbitrary latest frame, provided the physical bus matches.
            frameIndex = _preferredFrameIndex;
        }
        else if (_matchingTimelines.Count > 1 && _selectedChannelIndex == 0)
        {
            ExampleText = $"Observed on {_matchingTimelines.Count} distinct CAN channels. Select a bus above to avoid mixing networks.";
            return;
        }
        else if (_matchingTimelines.Count > 0)
        {
            var timeline = _selectedChannelIndex > 0
                ? _matchingTimelines[_selectedChannelIndex - 1]
                : _matchingTimelines[0];
            if (timeline.Samples.Count > 0)
                frameIndex = timeline.Samples[^1].FrameIndex;
        }

        if (frameIndex < 0 || frameIndex >= _frames.Count)
        {
            ExampleText = "No matching captured signal observations. Check DBC bus assignment, CAN ID, message DLC and multiplexing.";
            return;
        }

        var frame = _frames[frameIndex];
        var decoders = _decoders.Where(decoder =>
            string.Equals(decoder.Database.Name, _database.Name, StringComparison.OrdinalIgnoreCase) &&
            decoder.TryGetMessage(frame, out var resolved) &&
            resolved.Identifier == _message.Identifier &&
            resolved.IsExtended == _message.IsExtended).ToArray();
        if (decoders.Length != 1)
        {
            ExampleText = decoders.Length == 0
                ? "The originating DBC message no longer matches this CAN frame on the selected bus."
                : "More than one DBC decoding profile matches this source frame; resolve it before using values.";
            return;
        }

        // The bit pattern comes from THIS frame (not the latest frame of another
        // channel). Inactive mux signals still show their physical bit positions.
        BitRows = DbcSignalInspector.OverlayRecordedPayload(_layout, frame.GetPayload());
        OnPropertyChanged(nameof(BitRows));
        SourceFrameIndex = frameIndex;
        var source = $"CAN {frame.Channel + 1} · frame #{frameIndex + 1:N0} · " +
                     $"t={frame.TimestampNanoseconds / 1_000_000_000d:F6} s";
        var decoded = decoders[0].Decode(frame)
            .Where(item => item.Signal == _signal.Name).ToArray();
        if (decoded.Length != 1)
        {
            ExampleText = source + " · raw bytes visible in bit map; signal inactive (multiplex branch) or DLC too short for safe decoding.";
            return;
        }

        var value = decoded[0];
        var raw = value.RawValue.ToString("G12", CultureInfo.InvariantCulture);
        var physical = value.PhysicalValue.ToString("G12", CultureInfo.InvariantCulture);
        // Decode() exposes a double timeline sample. Inspect the *same*
        // originating source frame's bits to recover the exact raw code,
        // including values outside the 53-bit integer precision of double.
        var sourceRaw = DbcSignalInspector.InspectRecordedRaw(_signal, frame.GetPayload());
        var rawText = sourceRaw is null
            ? $"raw {raw} (exact source bits unavailable)"
            : sourceRaw.IsFloatingPoint
                ? $"raw {raw} · exact IEEE-754 bit pattern {sourceRaw.RawHex}"
                : $"exact raw {sourceRaw.RawText} ({sourceRaw.RawHex})";
        var physicalPrecision = sourceRaw is
            { IsFloatingPoint: false, IsExactInDouble: false }
                ? " (approximate: double-based scaling loses wide-integer precision)"
                : string.Empty;
        var meaning = sourceRaw?.DeclaredMeaning;
        ExampleText = source + " · " + rawText + $" · physical {physical}" +
            (string.IsNullOrWhiteSpace(_signal.Unit) ? string.Empty : " " + _signal.Unit) +
            physicalPrecision +
            (meaning is null ? " · no declared symbolic meaning" : $" · meaning: {meaning}");
    }
}
