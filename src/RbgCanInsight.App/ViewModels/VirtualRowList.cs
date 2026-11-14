using System.Collections;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;

namespace RbgCanInsight.App.ViewModels;

/// <summary>
/// Read-only IList facade over the loaded frames (optionally through a filtered index array)
/// that materialises <see cref="TraceRowViewModel"/> items on demand, so a multi-million-frame
/// trace can be bound to a virtualised DataGrid without creating one view-model per frame.
/// A small MRU cache keeps the rows around the viewport warm.
///
/// This previously replaced its own bound instance on every playback tick, forcing the DataGrid
/// through a full, measured 300-700 ms Reset regardless of trace size (see "Measured Performance
/// Findings" in docs/CANALYZER_PARITY_ROADMAP.md). An attempt to fix this by implementing
/// <c>INotifyCollectionChanged</c> and growing this same instance in place was built, benchmarked
/// with the harness that found the problem, and reverted: WPF's default <c>ICollectionView</c>
/// rejects a batched "range" Add (throws <c>NotSupportedException</c>), and raising one
/// single-item Add event per newly revealed row measured no better — slightly worse, in fact —
/// than the Reset it was meant to replace, both at 20,000 and 500,000 frames. Left as a documented,
/// measured dead end rather than a silent regression; a real fix likely needs to bypass
/// <c>ICollectionView</c> entirely (a custom virtualizing panel reading straight from this list).
/// </summary>
public sealed class VirtualRowList : IList, IList<TraceRowViewModel>, IReadOnlyList<TraceRowViewModel>
{
    private readonly IReadOnlyList<CanFrame> _frames;
    private readonly int[]? _filteredIndices;
    private readonly int? _visibleCount;
    private readonly IReadOnlyList<DbcDecoder> _decoders;
    private readonly int[] _previousSameKeyIndex;
    private readonly Func<int, MarkerCategory?> _isMarked;
    private readonly Func<int, bool> _isExpanded;
    private readonly Action<int, bool> _setExpanded;
    private readonly Func<int, string?>? _didNameResolver;
    private readonly Func<int, uint, bool, bool>? _isDiagnosticId;
    private readonly TraceTimeDisplayMode _timeDisplayMode;
    private readonly long _traceStartTimestampNanoseconds;
    private readonly Func<int, (int Offset, int Count)?>? _selectedSourceByteRange;
    private readonly Func<int, string?>? _rowColour;
    private readonly Func<int, string?>? _sourceFile;
    private readonly Func<int, string?>? _sourceEvidence;
    private readonly Dictionary<int, TraceRowViewModel> _cache = [];
    private readonly Queue<int> _cacheOrder = new();
    private const int CacheCapacity = 4_096;

    public VirtualRowList(
        IReadOnlyList<CanFrame> frames,
        IReadOnlyList<DbcDecoder> decoders,
        int[]? previousSameKeyIndex = null,
        int[]? filteredIndices = null,
        Func<int, MarkerCategory?>? isMarked = null,
        Func<int, bool>? isExpanded = null,
        Action<int, bool>? setExpanded = null,
        int? visibleCount = null,
        Func<int, string?>? didNameResolver = null,
        Func<int, uint, bool, bool>? isDiagnosticId = null,
        TraceTimeDisplayMode timeDisplayMode = TraceTimeDisplayMode.Absolute,
        long traceStartTimestampNanoseconds = 0,
        Func<int, (int Offset, int Count)?>? selectedSourceByteRange = null,
        Func<int, string?>? rowColour = null,
        Func<int, string?>? sourceFile = null,
        Func<int, string?>? sourceEvidence = null)
    {
        _rowColour = rowColour;
        _sourceFile = sourceFile;
        _sourceEvidence = sourceEvidence;
        _frames = frames;
        _decoders = decoders;
        _previousSameKeyIndex = previousSameKeyIndex ?? [];
        _filteredIndices = filteredIndices;
        _visibleCount = visibleCount;
        _isMarked = isMarked ?? (_ => null);
        _isExpanded = isExpanded ?? (_ => false);
        _setExpanded = setExpanded ?? ((_, _) => { });
        _didNameResolver = didNameResolver;
        _isDiagnosticId = isDiagnosticId;
        _timeDisplayMode = timeDisplayMode;
        _traceStartTimestampNanoseconds = traceStartTimestampNanoseconds;
        _selectedSourceByteRange = selectedSourceByteRange;
    }

    public int Count => _filteredIndices is { } indices
        ? Math.Min(_visibleCount ?? indices.Length, indices.Length)
        : Math.Min(_visibleCount ?? _frames.Count, _frames.Count);

    /// <summary>Maps a list position to the underlying frame index.</summary>
    public int FrameIndexAt(int position) => _filteredIndices?[position] ?? position;

    /// <summary>Finds the list position of a frame index (identity without a filter).</summary>
    public int PositionOfFrameIndex(int frameIndex)
    {
        if (_filteredIndices is null)
        {
            return frameIndex >= 0 && frameIndex < Count ? frameIndex : -1;
        }

        // A filtered result is built once for the full trace. The replay
        // cursor exposes only its prefix, so hidden future frames must not
        // resolve to a visible grid position.
        var position = Array.BinarySearch(_filteredIndices, 0, Count, frameIndex);
        return position >= 0 ? position : -1;
    }

    public TraceRowViewModel this[int index]
    {
        get
        {
            var frameIndex = FrameIndexAt(index);
            if (_cache.TryGetValue(frameIndex, out var cached))
            {
                return cached;
            }

            var row = new TraceRowViewModel(
                frameIndex,
                _frames,
                _decoders,
                _previousSameKeyIndex,
                _isMarked,
                _isExpanded,
                _setExpanded,
                _didNameResolver,
                _isDiagnosticId,
                _timeDisplayMode,
                _traceStartTimestampNanoseconds,
                previousRowFrameIndex: index > 0 ? FrameIndexAt(index - 1) : -1,
                rowColour: _rowColour,
                sourceFile: _sourceFile,
                sourceEvidence: _sourceEvidence);
            // The 4,096-row LRU can evict the highlighted raw frame
            // while scrolling a million-frame trace. Restore the exact
            // source-byte overlay on re-materialization, without caching
            // all frame rows or touching the raw CAN records.
            if (_selectedSourceByteRange?.Invoke(frameIndex) is { } source)
                row.HighlightOriginalSourceBytes(source.Offset, source.Count);
            _cache[frameIndex] = row;
            _cacheOrder.Enqueue(frameIndex);
            while (_cacheOrder.Count > CacheCapacity)
            {
                _cache.Remove(_cacheOrder.Dequeue());
            }

            return row;
        }
        set => throw new NotSupportedException();
    }

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public IEnumerator<TraceRowViewModel> GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
        {
            yield return this[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int IndexOf(TraceRowViewModel item) => PositionOfFrameIndex(item.FrameIndex);

    int IList.IndexOf(object? value) => value is TraceRowViewModel row ? IndexOf(row) : -1;

    public bool Contains(TraceRowViewModel item) => IndexOf(item) >= 0;

    bool IList.Contains(object? value) => value is TraceRowViewModel row && Contains(row);

    public void CopyTo(TraceRowViewModel[] array, int arrayIndex)
    {
        for (var index = 0; index < Count; index++)
        {
            array[arrayIndex + index] = this[index];
        }
    }

    void ICollection.CopyTo(Array array, int index)
    {
        for (var i = 0; i < Count; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }

    public void Add(TraceRowViewModel item) => throw new NotSupportedException();
    int IList.Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, TraceRowViewModel item) => throw new NotSupportedException();
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    public bool Remove(TraceRowViewModel item) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
