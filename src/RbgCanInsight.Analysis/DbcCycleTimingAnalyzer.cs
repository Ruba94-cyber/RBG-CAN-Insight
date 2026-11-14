using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis;

/// <summary>Relative-to-DBC cycle analysis. A nominal GenMsgCycleTime is a
/// database declaration, not a measured network timing guarantee. The analyzer
/// reports evidence and explicitly labels potential frame loss as a hypothesis.</summary>
public sealed record DbcCycleTimingRow(
    string Database,
    string Message,
    string Transmitter,
    int Channel,
    uint Identifier,
    bool IsExtended,
    int NominalCycleMs,
    long RecordedFrameCount,
    double AveragePeriodMs,
    double MinPeriodMs,
    double MaxPeriodMs,
    double StandardDeviationMs,
    int EarlyGapCount,
    int LateGapCount,
    int PotentialMissingFrames,
    int NonMonotonicTimestampCount,
    int WorstGapFrameIndex)
{
    public string IdentifierText =>
        "0x" + Identifier.ToString(IsExtended ? "X8" : "X3") + (IsExtended ? "x" : "");
    public double AverageDeviationPercent => NominalCycleMs > 0
        ? (AveragePeriodMs / NominalCycleMs - 1) * 100d : 0;
    public double MaxGapRatio => NominalCycleMs > 0
        ? MaxPeriodMs / NominalCycleMs : 0;
    public string Status => RecordedFrameCount < 2 ? "Insufficient evidence" :
        NonMonotonicTimestampCount > 0 ? "Timestamp disorder" :
        LateGapCount > 0 ? "Late gap observed" :
        EarlyGapCount > 0 ? "Fast/event frames" : "Within configured tolerance";
}

/// <summary>One source-backed late gap, preserving the frame index for Trace navigation.</summary>
public sealed record DbcCycleTimingIncident(
    string Database,
    string Message,
    int Channel,
    uint Identifier,
    bool IsExtended,
    long TimestampNanoseconds,
    int FrameIndex,
    int PreviousFrameIndex,
    double ObservedGapMilliseconds,
    int NominalCycleMilliseconds,
    int PotentialMissingFrames)
{
    public string IdentifierText =>
        "0x" + Identifier.ToString(IsExtended ? "X8" : "X3") + (IsExtended ? "x" : "");
    public double GapRatio => NominalCycleMilliseconds == 0 ? 0 :
        ObservedGapMilliseconds / NominalCycleMilliseconds;
    public string Note => PotentialMissingFrames == 0
        ? "Longer than the declared cycle; not proof of frame loss."
        : $"~{PotentialMissingFrames} potentially absent cycles if transmission was strictly periodic; event-driven schedules may differ.";
}

public sealed record DbcCycleTimingResult(
    IReadOnlyList<DbcCycleTimingRow> Rows,
    IReadOnlyList<DbcCycleTimingIncident> Incidents,
    int AmbiguousProfiles,
    int SuppressedIncidentCount)
{
    public static DbcCycleTimingResult Empty { get; } = new([], [], 0, 0);
    public string Summary => Rows.Count == 0
        ? "No uniquely matched nominal DBC cycle definitions in the loaded trace."
        : $"{Rows.Count:N0} DBC/physical-ID profiles · " +
          $"{Rows.Count(row => row.LateGapCount > 0):N0} with late gaps · " +
          $"{Rows.Sum(row => (long)row.LateGapCount):N0} total late intervals" +
          (AmbiguousProfiles > 0 ? $" · {AmbiguousProfiles} ambiguous profile(s) excluded" : "") +
          (SuppressedIncidentCount > 0 ? $" · {SuppressedIncidentCount:N0} individual incident rows omitted; aggregate counts remain complete" : "");
}

public static class DbcCycleTimingAnalyzer
{
    private readonly record struct PhysicalKey(int Channel, uint Identifier, bool Extended);

    private sealed class State(DbcDecoder decoder, DbcMessage message, PhysicalKey key)
    {
        public readonly DbcDecoder Decoder = decoder;
        public readonly DbcMessage Message = message;
        public readonly PhysicalKey Key = key;
        public long Count;
        public long PreviousTimestamp;
        public int PreviousFrameIndex = -1;
        public double Mean;
        public double SumSquaredDeviations;
        public double Min = double.MaxValue;
        public double Max;
        public int Early;
        public int Late;
        public int PotentialMissing;
        public int TimestampDisorders;
        public int WorstFrameIndex = -1;

        public void Add(in CanFrame frame, int frameIndex, double tolerance,
            List<DbcCycleTimingIncident> incidents, int maxIncidents, ref int suppressed)
        {
            if (Count > 0)
            {
                var difference = frame.TimestampNanoseconds - PreviousTimestamp;
                if (difference < 0)
                {
                    TimestampDisorders++;
                    // Do not use a negative period in jitter/rate statistics.
                    // Start a new local timing segment without guessing gaps.
                }
                else
                {
                    var periodMs = difference / 1_000_000d;
                    var periodCount = Count - TimestampDisorders;
                    // Welford's stable online population variance.
                    var delta = periodMs - Mean;
                    Mean += delta / periodCount;
                    SumSquaredDeviations += delta * (periodMs - Mean);
                    Min = Math.Min(Min, periodMs);
                    if (periodMs > Max)
                    {
                        Max = periodMs;
                        WorstFrameIndex = frameIndex;
                    }
                    var nominal = Message.NominalCycleTimeMilliseconds!.Value;
                    if (periodMs > nominal * (1 + tolerance))
                    {
                        Late++;
                        var estimate = periodMs / nominal;
                        // This is a potential slot deficit, never an observed
                        // number of missing CAN frames. Event-triggering can
                        // produce identical patterns without lost messages.
                        var candidate = estimate >= int.MaxValue
                            ? int.MaxValue
                            : Math.Max(0, (int)Math.Round(estimate) - 1);
                        PotentialMissing = (int)Math.Min(int.MaxValue, (long)PotentialMissing + candidate);
                        if (incidents.Count < maxIncidents)
                        {
                            incidents.Add(new DbcCycleTimingIncident(
                                Decoder.Database.Name, Message.Name, frame.Channel,
                                frame.Identifier, frame.IsExtended,
                                frame.TimestampNanoseconds, frameIndex,
                                PreviousFrameIndex, periodMs, nominal, candidate));
                        }
                        else suppressed++;
                    }
                    else if (periodMs < nominal * (1 - tolerance))
                    {
                        Early++; // May be legitimate event-triggered traffic.
                    }
                }
            }

            Count++;
            PreviousTimestamp = frame.TimestampNanoseconds;
            PreviousFrameIndex = frameIndex;
        }

        public DbcCycleTimingRow ToRow()
        {
            var validGaps = Count - 1 - TimestampDisorders;
            return new DbcCycleTimingRow(
                Decoder.Database.Name, Message.Name, Message.Transmitter,
                Key.Channel, Key.Identifier, Key.Extended, Message.NominalCycleTimeMilliseconds!.Value,
                Count,
                validGaps > 0 ? Mean : 0,
                validGaps > 0 ? Min : 0,
                validGaps > 0 ? Max : 0,
                validGaps > 1 ? Math.Sqrt(Math.Max(0, SumSquaredDeviations / validGaps)) : 0,
                Early, Late, PotentialMissing, TimestampDisorders, WorstFrameIndex);
        }
    }

    /// <param name="maxIncidents">Bound for UI event memory. Full per-ID counts
    /// remain accurate even when individual event rows are truncated.</param>
    /// <param name="relativeTolerance">Relative deviation from DBC nominal period.
    /// Default ±20% is a screening heuristic, not an OEM timing requirement.</param>
    public static DbcCycleTimingResult Analyze(
        IReadOnlyList<CanFrame> frames,
        IReadOnlyList<MessageStatisticsRow> statistics,
        IReadOnlyList<DbcDecoder> decoders,
        double relativeTolerance = 0.20,
        int maxIncidents = 20_000,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(relativeTolerance) || relativeTolerance is < 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(relativeTolerance));
        if (maxIncidents < 0) throw new ArgumentOutOfRangeException(nameof(maxIncidents));
        if (frames.Count == 0 || statistics.Count == 0 || decoders.Count == 0)
            return DbcCycleTimingResult.Empty;

        var states = new Dictionary<PhysicalKey, State>();
        var ambiguous = 0;
        foreach (var row in statistics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = new PhysicalKey(row.Channel, row.Identifier, row.IsExtended);
            var matches = decoders
                .Select(decoder =>
                    decoder.TryGetMessage(row.Channel, row.Identifier, row.IsExtended, out var message)
                        ? (Decoder: decoder, Message: message)
                        : (Decoder: decoder, Message: (DbcMessage?)null))
                .Where(result => result.Message is not null)
                .Take(2).ToArray();

            if (matches.Length > 1)
            {
                ambiguous++;
                continue; // multiple DBs; no first-loaded-wins timing identity
            }

            if (matches.Length == 1 && matches[0].Message is { NominalCycleTimeMilliseconds: > 0 } nominalMessage)
            {
                states[key] = new State(matches[0].Decoder, nominalMessage, key);
            }
        }

        var incidents = new List<DbcCycleTimingIncident>();
        var suppressed = 0;
        for (var index = 0; index < frames.Count; index++)
        {
            if ((index & 0x1FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var frame = frames[index];
            if (frame.IsError || frame.Flags.HasFlag(CanFrameFlags.Remote)) continue;
            if (states.TryGetValue(new PhysicalKey(frame.Channel, frame.Identifier, frame.IsExtended), out var state))
                state.Add(frame, index, relativeTolerance, incidents, maxIncidents, ref suppressed);
        }

        return new DbcCycleTimingResult(
            states.Values.Where(state => state.Count > 0).Select(state => state.ToRow())
                .OrderByDescending(row => row.LateGapCount)
                .ThenByDescending(row => row.MaxGapRatio)
                .ThenBy(row => row.Channel)
                .ThenBy(row => row.Identifier).ToArray(),
            incidents.OrderBy(incident => incident.TimestampNanoseconds)
                .ThenBy(incident => incident.FrameIndex).ToArray(),
            ambiguous, suppressed);
    }
}
