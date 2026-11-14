using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using RbgCanInsight.App.Common;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;

namespace RbgCanInsight.App.ViewModels;

public sealed class MatrixByteCellViewModel : ObservableObject
{
    private string _text = "--";
    private bool _isChanged;
    private string _changedBitsText = string.Empty;

    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public bool IsChanged
    {
        get => _isChanged;
        private set => SetProperty(ref _isChanged, value);
    }

    public string ChangedBitsText
    {
        get => _changedBitsText;
        private set => SetProperty(ref _changedBitsText, value);
    }

    public void Update(byte? current, byte? previous)
    {
        Text = current is { } value ? value.ToString("X2", CultureInfo.InvariantCulture) : "--";
        if (current is null || previous is null)
        {
            IsChanged = false;
            ChangedBitsText = string.Empty;
            return;
        }

        var flipped = (byte)(current.Value ^ previous.Value);
        IsChanged = flipped != 0;
        ChangedBitsText = flipped == 0 ? string.Empty : FormatBits(flipped);
    }

    private static string FormatBits(byte mask)
    {
        var bits = new List<int>(8);
        for (var bit = 7; bit >= 0; bit--)
        {
            if ((mask & (1 << bit)) != 0)
            {
                bits.Add(bit);
            }
        }

        return "bits " + string.Join(", ", bits);
    }
}

public sealed class ByteMatrixRowViewModel : ObservableObject
{
    private string _messageName = string.Empty;
    private string _ecu = string.Empty;
    private string _extraBytesText = string.Empty;
    private long _count;
    private long _lastTimestampNs;
    private bool _isDetailOpen;

    public ByteMatrixRowViewModel(int channel, uint identifier, bool isExtended)
    {
        RawChannel = channel;
        RawIdentifier = identifier;
        IsExtended = isExtended;
        Channel = channel + 1;
        Identifier = identifier.ToString(isExtended ? "X8" : "X3", CultureInfo.InvariantCulture);
        Bytes = Enumerable.Range(0, 8).Select(_ => new MatrixByteCellViewModel()).ToArray();
    }

    public int RawChannel { get; }
    public uint RawIdentifier { get; }
    public bool IsExtended { get; }
    public int Channel { get; }
    public string Identifier { get; }
    public MatrixByteCellViewModel[] Bytes { get; }

    public bool IsDetailOpen
    {
        get => _isDetailOpen;
        set => SetProperty(ref _isDetailOpen, value);
    }

    public string MessageName
    {
        get => _messageName;
        private set => SetProperty(ref _messageName, value);
    }

    public string Ecu
    {
        get => _ecu;
        private set => SetProperty(ref _ecu, value);
    }

    public string ExtraBytesText
    {
        get => _extraBytesText;
        private set => SetProperty(ref _extraBytesText, value);
    }

    public long Count
    {
        get => _count;
        private set => SetProperty(ref _count, value);
    }

    public long LastTimestampNanoseconds
    {
        get => _lastTimestampNs;
        private set => SetProperty(ref _lastTimestampNs, value);
    }

    public string LastTimeText => (LastTimestampNanoseconds / 1_000_000_000d).ToString("F6", CultureInfo.InvariantCulture);
    public int LastFrameIndex { get; private set; } = -1;

    /// <summary>Applies the replayed state at the current timeline position in one shot.</summary>
    public void ApplyState(
        byte[] payload,
        byte[]? previous,
        long count,
        long lastTimestampNs,
        int lastFrameIndex,
        string messageName,
        string ecu)
    {
        for (var index = 0; index < Bytes.Length; index++)
        {
            byte? current = index < payload.Length ? payload[index] : null;
            byte? before = previous is not null && index < previous.Length ? previous[index] : null;
            Bytes[index].Update(current, before);
        }

        ExtraBytesText = payload.Length > Bytes.Length ? $"+{payload.Length - Bytes.Length}" : string.Empty;
        MessageName = messageName;
        Ecu = ecu;
        Count = count;
        LastTimestampNanoseconds = lastTimestampNs;
        LastFrameIndex = lastFrameIndex;
        OnPropertyChanged(nameof(LastTimeText));
    }

    public bool Matches(string search) =>
        string.IsNullOrWhiteSpace(search) ||
        Identifier.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        MessageName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        Ecu.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        Channel.ToString(CultureInfo.InvariantCulture).Contains(search, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Temporal byte matrix: shows, per bus/ID, the payload state at the selected timeline position
/// (with changed byte/bit highlighting vs the previous occurrence), consistent with the trace
/// and plot views. Forward timeline moves replay incrementally; jumps backwards restart cheaply
/// from the head with plain byte arrays before touching any UI-bound object.
/// </summary>
public sealed class ByteMatrixViewModel : ObservableObject
{
    private sealed class ReplayState
    {
        public byte[] Payload = [];
        public byte[]? Previous;
        public long Count;
        public long LastTimestampNs;
        public int LastFrameIndex;
        public bool IsExtended;
    }

    private IReadOnlyList<CanFrame> _frames = [];
    private IReadOnlyList<DbcDecoder> _decoders = [];
    private int[] _previousSameKeyIndex = [];
    private readonly Dictionary<(int Channel, uint Id), ReplayState> _replay = [];
    private readonly Dictionary<(int Channel, uint Id), ByteMatrixRowViewModel> _rowByKey = [];
    private readonly Dictionary<(int Channel, uint Id, bool Extended), string> _nameByKey = [];
    private readonly Dictionary<(int Channel, uint Id, bool Extended), string> _ecuByKey = [];
    private int _replayEnd = -1;
    private string _filterText = string.Empty;
    private double _timelinePercent = 100;
    private double _playbackSpeed = 1;
    private bool _isPlaying;
    private ByteMatrixRowViewModel? _selectedRow;
    private IReadOnlyList<SignalDetailRow> _selectedRowSignals = [];
    private readonly double[] _playbackSpeedChoices = [0.1, 0.25, 0.5, 1, 2, 5, 10, 20, 50, 100];
    private readonly DispatcherTimer _playTimer;
    private RelayCommand? _togglePlaybackCommand;
    private RelayCommand? _headCommand;
    private RelayCommand? _tailCommand;

    public ByteMatrixViewModel()
    {
        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _playTimer.Tick += (_, _) => AdvancePlayback();
        _playbackSpeed = PlaybackSpeedStore.Current;
        PlaybackSpeedStore.Changed += (_, _) =>
        {
            if (Math.Abs(_playbackSpeed - PlaybackSpeedStore.Current) > 1e-9)
            {
                _playbackSpeed = PlaybackSpeedStore.Current;
                OnPropertyChanged(nameof(PlaybackSpeed));
            }
        };
    }

    public ObservableCollection<ByteMatrixRowViewModel> Rows { get; } = [];

    public ByteMatrixRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            var previous = _selectedRow;
            if (SetProperty(ref _selectedRow, value))
            {
                if (previous is not null)
                {
                    previous.IsDetailOpen = false;
                }

                if (value is not null)
                {
                    value.IsDetailOpen = true;
                }

                UpdateSelectedRowSignals();
            }
        }
    }

    public IReadOnlyList<SignalDetailRow> SelectedRowSignals
    {
        get => _selectedRowSignals;
        private set => SetProperty(ref _selectedRowSignals, value);
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                RefreshVisibleRows();
            }
        }
    }

    public double TimelinePercent
    {
        get => _timelinePercent;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _timelinePercent, clamped))
            {
                RebuildRows();
            }
        }
    }

    public string PlaybackButtonText => _isPlaying ? "Stop" : "Play";
    public IReadOnlyList<double> PlaybackSpeedChoices => _playbackSpeedChoices;

    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        set
        {
            var clamped = Math.Clamp(value, 0.1, 100);
            if (SetProperty(ref _playbackSpeed, clamped))
            {
                PlaybackSpeedStore.Current = clamped;
            }
        }
    }

    public RelayCommand TogglePlaybackCommand => _togglePlaybackCommand ??= new RelayCommand(TogglePlayback);
    public RelayCommand HeadCommand => _headCommand ??= new RelayCommand(() => TimelinePercent = 0);
    public RelayCommand TailCommand => _tailCommand ??= new RelayCommand(() => TimelinePercent = 100);

    public string SummaryText
    {
        get
        {
            if (_frames.Count == 0)
            {
                return "Load a trace to populate the byte matrix.";
            }

            var end = EndIndex;
            var time = end >= 0 ? _frames[end].TimestampNanoseconds / 1_000_000_000d : 0;
            return $"{Rows.Count:N0} bus/ID rows · state at t={time:F3}s (frame {end + 1:N0} / {_frames.Count:N0})";
        }
    }

    private int EndIndex => _frames.Count == 0
        ? -1
        : (int)Math.Round((_frames.Count - 1) * _timelinePercent / 100d);

    public void SetSession(IReadOnlyList<CanFrame> frames, IReadOnlyList<DbcDecoder> decoders)
    {
        _frames = frames;
        _decoders = decoders;
        _previousSameKeyIndex = BuildPreviousSameKeyIndex(frames);
        _nameByKey.Clear();
        _ecuByKey.Clear();
        _rowByKey.Clear();
        ResetReplay();
        SelectedRow = null;
        _timelinePercent = 100;
        OnPropertyChanged(nameof(TimelinePercent));
        RebuildRows();
    }

    private void ResetReplay()
    {
        _replay.Clear();
        _replayEnd = -1;
    }

    private void RebuildRows()
    {
        var target = EndIndex;
        if (target < _replayEnd)
        {
            ResetReplay();
        }

        for (var index = _replayEnd + 1; index <= target; index++)
        {
            var frame = _frames[index];
            var key = (frame.Channel, frame.Identifier);
            if (!_replay.TryGetValue(key, out var state))
            {
                _replay[key] = state = new ReplayState { IsExtended = frame.IsExtended };
            }
            else
            {
                state.Previous = state.Payload;
            }

            state.Payload = frame.GetPayload();
            state.Count++;
            state.LastTimestampNs = frame.TimestampNanoseconds;
            state.LastFrameIndex = index;
        }

        _replayEnd = target;

        foreach (var (key, state) in _replay)
        {
            if (!_rowByKey.TryGetValue(key, out var row))
            {
                _rowByKey[key] = row = new ByteMatrixRowViewModel(key.Channel, key.Id, state.IsExtended);
            }

            row.ApplyState(
                state.Payload,
                state.Previous,
                state.Count,
                state.LastTimestampNs,
                state.LastFrameIndex,
                ResolveMessageName(key.Channel, key.Id, state.IsExtended),
                ResolveEcu(key.Channel, key.Id, state.IsExtended));
        }

        RefreshVisibleRows();
        UpdateSelectedRowSignals();
    }

    private void RefreshVisibleRows()
    {
        // Clearing and re-adding to an ObservableCollection resets the DataGrid's selection
        // (a Reset notification has no selected item), which is what made the decoded signal
        // panel below vanish on every playback tick even though the selected ID was still on
        // screen. Save the reference and restore it once the row is back in the collection.
        var previousSelection = _selectedRow;
        Rows.Clear();
        foreach (var (key, row) in _rowByKey
                     .Where(pair => _replay.ContainsKey(pair.Key) && pair.Value.Matches(FilterText))
                     .OrderBy(pair => pair.Value.Channel)
                     .ThenBy(pair => pair.Value.Identifier, StringComparer.OrdinalIgnoreCase))
        {
            _ = key;
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(SummaryText));

        if (previousSelection is not null && Rows.Contains(previousSelection))
        {
            SelectedRow = previousSelection;
        }
    }

    private void TogglePlayback()
    {
        if (_frames.Count == 0)
        {
            return;
        }

        _isPlaying = !_isPlaying;
        if (_isPlaying)
        {
            if (_timelinePercent >= 100)
            {
                TimelinePercent = 0;
            }

            _playTimer.Start();
        }
        else
        {
            _playTimer.Stop();
        }

        OnPropertyChanged(nameof(PlaybackButtonText));
    }

    private void AdvancePlayback()
    {
        if (!_isPlaying || _frames.Count == 0)
        {
            return;
        }

        var next = Math.Min(100, _timelinePercent + 0.4 * PlaybackSpeed);
        TimelinePercent = next;
        if (next >= 100)
        {
            _isPlaying = false;
            _playTimer.Stop();
            OnPropertyChanged(nameof(PlaybackButtonText));
        }
    }

    private string ResolveMessageName(int channel, uint identifier, bool isExtended)
    {
        var key = (channel, identifier, isExtended);
        if (_nameByKey.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var name = string.Empty;
        foreach (var decoder in _decoders)
        {
            if (decoder.TryGetMessage(channel, identifier, isExtended, out var message))
            {
                name = message.Name;
                break;
            }
        }

        return _nameByKey[key] = name;
    }

    private string ResolveEcu(int channel, uint identifier, bool isExtended)
    {
        var key = (channel, identifier, isExtended);
        if (_ecuByKey.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var ecu = string.Empty;
        foreach (var decoder in _decoders)
        {
            if (decoder.TryGetMessage(channel, identifier, isExtended, out var message))
            {
                ecu = message.Transmitter;
                break;
            }
        }

        return _ecuByKey[key] = ecu;
    }

    private void UpdateSelectedRowSignals()
    {
        if (SelectedRow is not { LastFrameIndex: >= 0 } row || row.LastFrameIndex >= _frames.Count)
        {
            SelectedRowSignals = [];
            return;
        }

        var traceRow = new TraceRowViewModel(
            row.LastFrameIndex,
            _frames,
            _decoders,
            _previousSameKeyIndex);
        SelectedRowSignals = traceRow.DecodeDetailedSignals();
    }

    private static int[] BuildPreviousSameKeyIndex(IReadOnlyList<CanFrame> frames)
    {
        var previous = new int[frames.Count];
        var lastByKey = new Dictionary<(int Channel, uint Id), int>();
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            var key = (frame.Channel, frame.Identifier);
            previous[index] = lastByKey.TryGetValue(key, out var last) ? last : -1;
            lastByKey[key] = index;
        }

        return previous;
    }
}
