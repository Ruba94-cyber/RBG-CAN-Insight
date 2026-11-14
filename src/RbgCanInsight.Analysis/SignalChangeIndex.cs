namespace RbgCanInsight.Analysis;

/// <summary>
/// Sparse, per-selected-signal transition index for navigating actual decoded
/// changes in an offline trace. Only source sample positions are stored; no
/// artificial values or timestamps are created. Build lazily for selected
/// signals rather than allocating another index for every DBC signal.
/// </summary>
public sealed class SignalChangeIndex
{
    private readonly IReadOnlyList<SignalSample> _samples;
    private readonly int[] _transitionPositions;

    public SignalChangeIndex(IReadOnlyList<SignalSample> chronologicallySortedSamples)
    {
        ArgumentNullException.ThrowIfNull(chronologicallySortedSamples);
        _samples = chronologicallySortedSamples;
        var positions = new List<int>();
        for (var i = 1; i < _samples.Count; i++)
        {
            if (_samples[i].Value != _samples[i - 1].Value)
                positions.Add(i);
        }

        _transitionPositions = positions.ToArray();
    }

    public int Count => _transitionPositions.Length;

    /// <summary>First transition strictly after this timestamp, if any.
    /// Equal-timestamp source frames are kept in original ordered evidence,
    /// but timeline navigation does not invent time between them.</summary>
    public SignalSample? Next(long timestampNanoseconds)
    {
        var low = 0;
        var high = _transitionPositions.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (_samples[_transitionPositions[mid]].TimestampNanoseconds <= timestampNanoseconds)
                low = mid + 1;
            else
                high = mid;
        }

        return low == _transitionPositions.Length
            ? null
            : _samples[_transitionPositions[low]];
    }

    /// <summary>Last transition strictly before this timestamp, if any.</summary>
    public SignalSample? Previous(long timestampNanoseconds)
    {
        var low = 0;
        var high = _transitionPositions.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (_samples[_transitionPositions[mid]].TimestampNanoseconds < timestampNanoseconds)
                low = mid + 1;
            else
                high = mid;
        }

        return low == 0 ? null : _samples[_transitionPositions[low - 1]];
    }
}
