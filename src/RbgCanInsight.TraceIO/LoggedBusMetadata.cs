using System.Globalization;
using System.Text.RegularExpressions;

namespace RbgCanInsight.TraceIO;

/// <summary>A bus statistic line written by the logger itself (Vector ASC "Statistic:"). Values are
/// what the logging hardware measured - not estimated from the frames in the file.</summary>
/// <param name="Channel">Zero-based channel (ASC channels are 1-based).</param>
public sealed record LoggedBusStatistic(
    long TimestampNanoseconds, int Channel,
    long DataFrames, long RemoteFrames, long ExtendedDataFrames, long ExtendedRemoteFrames,
    long ErrorFrames, long OverloadFrames, double BusLoadPercent);

/// <summary>A controller status line written by the logger (Vector ASC "Status:"), e.g.
/// "chip status error passive - TxErr: 132 RxErr: 0".</summary>
public sealed record LoggedControllerStatus(
    long TimestampNanoseconds, int Channel, string State, int? TxErrorCounter, int? RxErrorCounter);

/// <summary>Parses logged "Statistic:" / "Status:" lines of Vector ASC files.</summary>
public static partial class LoggedBusMetadata
{
    [GeneratedRegex(@"^\s*(?<t>[0-9]+(?:\.[0-9]*)?)\s+(?:CAN\s+)?(?<ch>[0-9]+)\s+Statistic:\s*(?<body>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StatisticLine();

    [GeneratedRegex(@"^\s*(?<t>[0-9]+(?:\.[0-9]*)?)\s+(?:CAN\s+)?(?<ch>[0-9]+)\s+Status:\s*(?<body>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StatusLine();

    [GeneratedRegex(@"(?<k>XD|XR|D|R|E|O|B)\s+(?<v>[0-9]+(?:\.[0-9]*)?)%?", RegexOptions.CultureInvariant)]
    private static partial Regex StatisticPair();

    [GeneratedRegex(@"^(?:chip status\s+)?(?<state>.*?)(?:\s*-\s*TxErr:\s*(?<tx>[0-9]+)\s+RxErr:\s*(?<rx>[0-9]+))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StatusBody();

    public static bool TryParseStatistic(string line, out LoggedBusStatistic statistic)
    {
        statistic = null!;
        var match = StatisticLine().Match(line);
        if (!match.Success || !TryTime(match, out var ns) || !TryChannel(match, out var channel)) return false;
        var values = new Dictionary<string, double>();
        foreach (Match pair in StatisticPair().Matches(match.Groups["body"].Value))
            if (double.TryParse(pair.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                values[pair.Groups["k"].Value] = v;
        if (!values.ContainsKey("B")) return false;
        long Count(string key) => values.TryGetValue(key, out var v) ? (long)v : 0;
        statistic = new LoggedBusStatistic(ns, channel, Count("D"), Count("R"), Count("XD"), Count("XR"),
            Count("E"), Count("O"), Math.Clamp(values["B"], 0, 100));
        return true;
    }

    public static bool TryParseStatus(string line, out LoggedControllerStatus status)
    {
        status = null!;
        var match = StatusLine().Match(line);
        if (!match.Success || !TryTime(match, out var ns) || !TryChannel(match, out var channel)) return false;
        var body = StatusBody().Match(match.Groups["body"].Value);
        var state = body.Groups["state"].Value.Trim();
        if (state.Length == 0) return false;
        int? tx = body.Groups["tx"].Success && int.TryParse(body.Groups["tx"].Value, out var t) ? t : null;
        int? rx = body.Groups["rx"].Success && int.TryParse(body.Groups["rx"].Value, out var r) ? r : null;
        status = new LoggedControllerStatus(ns, channel, state, tx, rx);
        return true;
    }

    private static bool TryTime(Match match, out long ns)
    {
        ns = 0;
        if (!double.TryParse(match.Groups["t"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            seconds > 9e9) return false;
        ns = (long)Math.Round(seconds * 1_000_000_000d);
        return true;
    }

    private static bool TryChannel(Match match, out int channel)
    {
        channel = 0;
        if (!int.TryParse(match.Groups["ch"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var one) ||
            one < 1) return false;
        channel = one - 1;
        return true;
    }

    /// <summary>Logged bus load for a channel over [startNs, startNs+widthNs): the mean of the logged
    /// statistics falling in the window, or null when the logger wrote none there.</summary>
    public static double? LoggedLoadInWindow(IReadOnlyList<LoggedBusStatistic>? statistics,
        int channel, long startNs, long widthNs)
    {
        if (statistics is null || statistics.Count == 0) return null;
        double sum = 0;
        var n = 0;
        foreach (var s in statistics)
            if (s.Channel == channel && s.TimestampNanoseconds >= startNs && s.TimestampNanoseconds < startNs + widthNs)
            { sum += s.BusLoadPercent; n++; }
        return n == 0 ? null : sum / n;
    }

    /// <summary>Mean logged bus load of a channel over the whole file, or null if none logged.</summary>
    public static double? LoggedMeanLoad(IReadOnlyList<LoggedBusStatistic>? statistics, int channel)
    {
        var values = statistics?.Where(s => s.Channel == channel).Select(s => s.BusLoadPercent).ToList();
        return values is { Count: > 0 } ? values.Average() : null;
    }

    /// <summary>Last logged controller state of a channel at or before <paramref name="timeNs"/>.</summary>
    public static LoggedControllerStatus? StatusAt(IReadOnlyList<LoggedControllerStatus>? statuses, int channel, long timeNs)
    {
        LoggedControllerStatus? best = null;
        if (statuses is null) return null;
        foreach (var s in statuses)
            if (s.Channel == channel && s.TimestampNanoseconds <= timeNs &&
                (best is null || s.TimestampNanoseconds >= best.TimestampNanoseconds)) best = s;
        return best;
    }
}
