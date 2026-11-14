namespace RbgCanInsight.Analysis;

/// <summary>
/// Source-evidence preserving decimation for offline plots. Each screen-pixel
/// bucket keeps the first and last recorded observation and its minimum and
/// maximum physical values in chronological order. This preserves edge shapes
/// and spikes when dense recordings have far more samples than plot pixels.
/// No synthetic observations are generated.
/// </summary>
public static class SignalWindowDecimator
{
    public static SignalSample[] Decimate(
        IReadOnlyList<SignalSample> sortedSamples,
        long startNanoseconds,
        long endNanoseconds,
        int pixelWidth)
    {
        ArgumentNullException.ThrowIfNull(sortedSamples);
        if (sortedSamples.Count == 0 || endNanoseconds < startNanoseconds)
            return [];

        var width = Math.Max(1, pixelWidth);
        var low = 0;
        var high = sortedSamples.Count;
        // First sample on or after the visible start (O(log N)).
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (sortedSamples[mid].TimestampNanoseconds < startNanoseconds)
                low = mid + 1;
            else
                high = mid;
        }

        if (low == sortedSamples.Count ||
            sortedSamples[low].TimestampNanoseconds > endNanoseconds)
            return []; // no real sample inside the window; don't claim stale data is new

        var capacity = (int)Math.Min(sortedSamples.Count, Math.Min((long)width * 4 + 1, 200_000L));
        var result = new List<SignalSample>(capacity);
        // The actual previous source observation, if available, connects a
        // transition entering the window. It retains its original timestamp.
        if (low > 0)
            result.Add(sortedSamples[low - 1]);

        var currentPixel = -1;
        var first = -1;
        var last = -1;
        var minimum = -1;
        var maximum = -1;
        var timeRange = Math.Max(1d, (double)endNanoseconds - startNanoseconds);

        void Flush()
        {
            if (first < 0)
                return;

            // At most four real source samples per pixel bucket; sorting their
            // source positions also handles same-timestamp CAN frames correctly.
            Span<int> positions = stackalloc int[4] { first, minimum, maximum, last };
            positions.Sort();
            var previous = -1;
            foreach (var position in positions)
            {
                if (position == previous)
                    continue;
                result.Add(sortedSamples[position]);
                previous = position;
            }
        }

        for (var i = low; i < sortedSamples.Count; i++)
        {
            var sample = sortedSamples[i];
            if (sample.TimestampNanoseconds > endNanoseconds)
                break;
            // Calculate the ratio in floating point BEFORE multiplying by the
            // pixel width; integer nanoseconds * width can overflow on long
            // captures even when every recorded timestamp is valid.
            var relative = ((double)sample.TimestampNanoseconds - startNanoseconds) / timeRange;
            var pixel = (int)Math.Clamp(relative * width, 0d, width - 1d);
            if (pixel != currentPixel)
            {
                Flush();
                currentPixel = pixel;
                first = last = minimum = maximum = i;
            }
            else
            {
                last = i;
                if (sample.Value < sortedSamples[minimum].Value)
                    minimum = i;
                if (sample.Value > sortedSamples[maximum].Value)
                    maximum = i;
            }
        }

        Flush();
        return result.ToArray();
    }
}
