using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis.BusHealth;

/// <summary>Explicit bitrates of one channel. Never auto-detected: the caller must supply them.
/// <see cref="DataBitrate"/> is only needed (and required) when CAN FD frames with BRS occur.</summary>
public sealed record ChannelBitrate(double NominalBitrate, double? DataBitrate = null);

/// <summary>Bit-stuffing assumption used by <see cref="CanFrameBitModel"/>.</summary>
public enum StuffingModel
{
    /// <summary>No dynamic stuff bits (lower bound). FD fixed stuff bits in the CRC field are still counted.</summary>
    None,
    /// <summary>ASSUMPTION: worst case, one stuff bit per 4 bits after the first in each stuffed region
    /// (floor((g-1)/4)). Real traffic is typically well below this; the load is therefore an upper estimate.</summary>
    WorstCase
}

public sealed record BusHealthOptions(
    long BucketWidthNanoseconds,
    IReadOnlyDictionary<int, ChannelBitrate> Bitrates,
    StuffingModel Stuffing = StuffingModel.WorstCase,
    int TopIdCount = 8,
    long? OriginNanoseconds = null);

/// <summary>Bits on the wire split by the bitrate they are transmitted at.</summary>
public readonly record struct FrameBits(int NominalPhaseBits, int DataPhaseBits)
{
    public int Total => NominalPhaseBits + DataPhaseBits;
}

/// <summary>
/// Documented bit-count model (ISO 11898-1 field layout; all stuffing numbers are ASSUMPTIONS, see
/// <see cref="StuffingModel"/>).
/// Classic data frame, n data bytes (0..8, remote frames use n=0):
///   standard: 47 + 8n bits (SOF1 ID11 RTR1 IDE1 r0 1 DLC4 | data | CRC15 CRCdel1 ACK1 ACKdel1 EOF7 IFS3)
///   extended: 67 + 8n bits. Stuffed region = SOF..CRC = 34+8n (std) / 54+8n (ext) bits;
///   worst-case extra = floor((g-1)/4).
/// CAN FD (n rounded up to a valid FD length), nominal-rate bits:
///   standard 17 (SOF ID11 RRS IDE FDF res BRS) + 12 (ACK ACKdel EOF7 IFS3) = 29; extended 36 + 12 = 48.
///   Arbitration-region worst-case stuffing = floor((g-1)/4) with g = 17 / 36.
/// FD data-phase bits (BRS frames; without BRS they are at the nominal rate):
///   ESI1 + DLC4 + 8n + stuff-count 4 + CRC (17 for n&lt;=16, else 21) + CRCdel 1;
///   plus fixed CRC stuff bits = ceil((4+crc)/4) (6 or 7; always counted),
///   plus worst-case dynamic stuffing over ESI..data: floor((5+8n-1)/4).
/// Error frames: assumed 17 nominal bits (6 flag + 8 delimiter + 3 IFS; superposed flags can be longer).
/// </summary>
public static class CanFrameBitModel
{
    public const int ErrorFrameAssumedBits = 17;
    private static readonly int[] FdLengths = [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64];

    public static int RoundUpToFdLength(int bytes)
    {
        foreach (var l in FdLengths)
        {
            if (l >= bytes) { return l; }
        }
        return 64;
    }

    public static FrameBits Estimate(in CanFrame frame, StuffingModel stuffing)
    {
        if (frame.IsError)
        {
            return new FrameBits(ErrorFrameAssumedBits, 0);
        }

        var fd = frame.Flags.HasFlag(CanFrameFlags.FlexibleDataRate);
        var brs = fd && frame.Flags.HasFlag(CanFrameFlags.BitRateSwitch);
        var ext = frame.IsExtended;
        var worst = stuffing == StuffingModel.WorstCase;

        if (!fd)
        {
            var n = frame.Flags.HasFlag(CanFrameFlags.Remote) ? 0 : Math.Min((int)frame.DataLength, 8);
            var bits = (ext ? 67 : 47) + 8 * n;
            if (worst)
            {
                var g = (ext ? 54 : 34) + 8 * n;
                bits += (g - 1) / 4;
            }
            return new FrameBits(bits, 0);
        }

        var len = RoundUpToFdLength(frame.DataLength);
        var arbG = ext ? 36 : 17;
        var nominal = arbG + 12 + (worst ? (arbG - 1) / 4 : 0);
        var crc = len <= 16 ? 17 : 21;
        var data = 5 + 8 * len + 4 + crc + 1 + (4 + crc + 3) / 4;
        if (worst)
        {
            data += (5 + 8 * len - 1) / 4;
        }
        return brs ? new FrameBits(nominal, data) : new FrameBits(nominal + data, 0);
    }
}

public sealed record BusHealthBucket(
    long StartNanoseconds,
    int FrameCount,
    double BusLoadPercent,
    int ErrorFrames,
    int RemoteFrames,
    int DistinctIds);

public sealed record IdActivity(uint Identifier, bool IsExtended, int TotalFrames, IReadOnlyList<int> FramesPerBucket);

public sealed record ChannelBusHealth(
    int Channel,
    IReadOnlyList<BusHealthBucket> Buckets,
    IReadOnlyList<IdActivity> TopIds);

public sealed record BusHealthResult(long OriginNanoseconds, long BucketWidthNanoseconds, IReadOnlyList<ChannelBusHealth> Channels);

public static class BusHealthTimeline
{
    /// <summary>
    /// Buckets are [origin + k*width, origin + (k+1)*width), origin defaulting to the earliest frame.
    /// A frame is attributed entirely to the bucket of its (start) timestamp. FrameCount includes error and
    /// remote frames; DistinctIds and TopIds ignore error frames. BusLoadPercent is clamped to [0,100]
    /// (start-timestamp attribution can overfill a bucket). Every channel in the data must have an entry
    /// in <see cref="BusHealthOptions.Bitrates"/>; FD+BRS frames require a DataBitrate. Otherwise
    /// <see cref="ArgumentException"/> is thrown — bitrates are never guessed.
    /// Each channel's timeline spans from the origin to its last frame's bucket (empty buckets included).
    /// </summary>
    public static BusHealthResult Compute(IEnumerable<CanFrame> frames, BusHealthOptions options)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(options);
        if (options.BucketWidthNanoseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Bucket width must be positive.");
        }

        var list = frames as IReadOnlyList<CanFrame> ?? frames.ToList();
        if (list.Count == 0)
        {
            return new BusHealthResult(options.OriginNanoseconds ?? 0, options.BucketWidthNanoseconds, []);
        }

        var origin = options.OriginNanoseconds ?? list.Min(f => f.TimestampNanoseconds);
        var width = options.BucketWidthNanoseconds;
        var perChannel = new SortedDictionary<int, List<CanFrame>>();
        foreach (var f in list)
        {
            if (f.TimestampNanoseconds < origin) { continue; }
            if (!perChannel.TryGetValue(f.Channel, out var l)) { perChannel[f.Channel] = l = []; }
            l.Add(f);
        }

        var channels = new List<ChannelBusHealth>();
        foreach (var (channel, cf) in perChannel)
        {
            if (!options.Bitrates.TryGetValue(channel, out var rate) || rate.NominalBitrate <= 0)
            {
                throw new ArgumentException($"No positive bitrate supplied for channel {channel}; bitrates are never auto-detected.", nameof(options));
            }

            var count = (int)((cf.Max(f => f.TimestampNanoseconds) - origin) / width) + 1;
            var frameCount = new int[count];
            var err = new int[count];
            var rem = new int[count];
            var seconds = new double[count];
            var ids = new HashSet<(uint, bool)>?[count];
            var perId = new Dictionary<(uint, bool), int[]>();
            var widthSec = width / 1e9;

            foreach (var f in cf)
            {
                var b = (int)((f.TimestampNanoseconds - origin) / width);
                frameCount[b]++;
                var bits = CanFrameBitModel.Estimate(f, options.Stuffing);
                seconds[b] += bits.NominalPhaseBits / rate.NominalBitrate;
                if (bits.DataPhaseBits > 0)
                {
                    if (rate.DataBitrate is not { } dr || dr <= 0)
                    {
                        throw new ArgumentException($"Channel {channel} has CAN FD BRS frames but no data-phase bitrate was supplied.", nameof(options));
                    }
                    seconds[b] += bits.DataPhaseBits / dr;
                }

                if (f.IsError) { err[b]++; continue; }
                if (f.Flags.HasFlag(CanFrameFlags.Remote)) { rem[b]++; }
                var key = (f.Identifier, f.IsExtended);
                (ids[b] ??= []).Add(key);
                if (!perId.TryGetValue(key, out var arr)) { perId[key] = arr = new int[count]; }
                arr[b]++;
            }

            var buckets = new List<BusHealthBucket>(count);
            for (var i = 0; i < count; i++)
            {
                buckets.Add(new BusHealthBucket(origin + i * width, frameCount[i],
                    Math.Clamp(seconds[i] / widthSec * 100d, 0, 100), err[i], rem[i], ids[i]?.Count ?? 0));
            }

            var top = perId
                .Select(kv => new IdActivity(kv.Key.Item1, kv.Key.Item2, kv.Value.Sum(), kv.Value))
                .OrderByDescending(a => a.TotalFrames).ThenBy(a => a.Identifier).ThenBy(a => a.IsExtended)
                .Take(Math.Max(0, options.TopIdCount)).ToList();
            channels.Add(new ChannelBusHealth(channel, buckets, top));
        }

        return new BusHealthResult(origin, width, channels);
    }
}

public sealed record LatencyHistogramBucket(double LowerMs, double? UpperMs, int Count);

public sealed record LatencyDistribution(
    int Count, double MinMs, double MedianMs, double P95Ms, double MaxMs,
    IReadOnlyList<LatencyHistogramBucket> Histogram);

/// <summary>Request/response latency statistics. Percentiles use linear interpolation between order
/// statistics (R-7 / Excel PERCENTILE.INC). Histogram bins are [lower, upper) over caller edges, first bin
/// starts at 0, last bin is open-ended.</summary>
public static class RequestResponseLatency
{
    public static readonly IReadOnlyList<double> DefaultEdgesMs = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000];

    /// <summary>Uses <see cref="DiagnosticTransaction.ResponseTimeMilliseconds"/>; transactions without a
    /// response time are skipped. Returns null when no latency is available.</summary>
    public static LatencyDistribution? FromTransactions(IEnumerable<DiagnosticTransaction> transactions, IReadOnlyList<double>? edgesMs = null) =>
        FromMilliseconds(transactions.Where(t => t.ResponseTimeMilliseconds is not null).Select(t => t.ResponseTimeMilliseconds!.Value), edgesMs);

    /// <summary>Caller-provided (request timestamp, response timestamp) pairs in nanoseconds; pairs with
    /// response before request are skipped.</summary>
    public static LatencyDistribution? FromPairs(IEnumerable<(long RequestNs, long ResponseNs)> pairs, IReadOnlyList<double>? edgesMs = null) =>
        FromMilliseconds(pairs.Where(p => p.ResponseNs >= p.RequestNs).Select(p => (p.ResponseNs - p.RequestNs) / 1e6), edgesMs);

    public static LatencyDistribution? FromMilliseconds(IEnumerable<double> latenciesMs, IReadOnlyList<double>? edgesMs = null)
    {
        var edges = edgesMs ?? DefaultEdgesMs;
        for (var i = 0; i < edges.Count; i++)
        {
            if (edges[i] < 0 || (i > 0 && edges[i] <= edges[i - 1]))
            {
                throw new ArgumentException("Histogram edges must be non-negative and strictly increasing.", nameof(edgesMs));
            }
        }

        var v = latenciesMs.Where(double.IsFinite).OrderBy(x => x).ToArray();
        if (v.Length == 0) { return null; }

        var counts = new int[edges.Count + 1];
        foreach (var x in v)
        {
            var bin = 0;
            while (bin < edges.Count && x >= edges[bin]) { bin++; }
            counts[bin]++;
        }

        var hist = new List<LatencyHistogramBucket>();
        for (var i = 0; i <= edges.Count; i++)
        {
            hist.Add(new LatencyHistogramBucket(i == 0 ? 0 : edges[i - 1], i < edges.Count ? edges[i] : null, counts[i]));
        }
        return new LatencyDistribution(v.Length, v[0], Percentile(v, 0.5), Percentile(v, 0.95), v[^1], hist);
    }

    private static double Percentile(double[] sorted, double p)
    {
        var pos = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (pos - lo) * (sorted[hi] - sorted[lo]);
    }
}
