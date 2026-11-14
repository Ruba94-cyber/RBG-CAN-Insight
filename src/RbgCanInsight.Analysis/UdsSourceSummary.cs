using RbgCanInsight.Diagnostics;

namespace RbgCanInsight.Analysis;

/// <summary>Per-physical-logger, per-recorded-service UDS transaction counts.
/// Physical sources are never inferred from shared CAN IDs, ECU names or times.</summary>
public sealed record UdsSourceSummaryRow(
    int SourceIndex, string SourcePath, byte ServiceId, string ServiceName,
    long Total, long Positive, long Negative, long NoResponse,
    long Suppressed, long Malformed, long OrphanResponses,
    long FirstTimestampNanoseconds, long LastTimestampNanoseconds,
    int WitnessFrameIndex)
{
    public string SourceName => Path.GetFileName(SourcePath);
    public string ServiceText => $"0x{ServiceId:X2}";
    public string TotalText => Total.ToString("N0");
    public string PositiveText => Positive.ToString("N0");
    public string NegativeText => Negative.ToString("N0");
    public string NoResponseText => NoResponse.ToString("N0");
    public string MalformedText => Malformed.ToString("N0");
    public string OrphanText => OrphanResponses.ToString("N0");
    public string TimeRangeText =>
        $"{FirstTimestampNanoseconds / 1_000_000_000d:F6} – {LastTimestampNanoseconds / 1_000_000_000d:F6} s";
}

public sealed record UdsSourceSummaryReport(
    long ObservedTransactions, int SourceServiceGroups, bool Truncated,
    IReadOnlyList<UdsSourceSummaryRow> Rows)
{
    public string Description =>
        $"{ObservedTransactions:N0} source-verified UDS transaction(s) in " +
        $"{SourceServiceGroups:N0} source/service group(s)" +
        (Truncated ? $" · showing top {Rows.Count:N0} only" : "");
}

/// <summary>
/// Consume source-isolated UDS transactions and verify that each transaction's
/// original frame indices actually belong to one physical recording before
/// grouping. Rejects contamination rather than silently counting it.
/// O(total source frame references + number of transactions) work, memory
/// bounded by distinct source+service keys (not raw trace length).
/// </summary>
public static class UdsSourceSummary
{
    public const int MaxGroups = 16384;
    public const int MaxDisplayRows = 300;

    private sealed class Counts
    {
        public long Total, Positive, Negative, NoResponse, Suppressed, Malformed, Orphan;
        public long FirstTime = long.MaxValue;
        public long LastTime = long.MinValue;
        public int Witness = -1;
        public string ServiceName = "";
    }

    public static UdsSourceSummaryReport Calculate(
        IReadOnlyList<DiagnosticTransaction> transactions,
        int recordedFrameCount,
        IReadOnlyList<string> sourcePaths,
        Func<int, int>? sourceAt = null,
        int maxRows = MaxDisplayRows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (recordedFrameCount < 0 || sourcePaths.Count < 1)
            throw new ArgumentOutOfRangeException(nameof(recordedFrameCount));
        if (maxRows < 1 || maxRows > MaxDisplayRows)
            throw new ArgumentOutOfRangeException(nameof(maxRows));
        if (sourcePaths.Count > 1 && sourceAt is null)
            throw new ArgumentException(
                "A multi-logger UDS summary requires an original-frame source index.",
                nameof(sourceAt));

        var results = new Dictionary<(int Source, byte Service), Counts>();
        for (var i = 0; i < transactions.Count; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var transaction = transactions[i];
            if (transaction.FrameIndices.Count == 0)
                throw new InvalidOperationException(
                    "UDS transaction has no recorded CAN frame witnesses.");
            var first = transaction.FrameIndices[0];
            if ((uint)first >= (uint)recordedFrameCount)
                throw new InvalidOperationException(
                    "UDS transaction references a frame outside the imported recording.");
            var source = sourceAt?.Invoke(first) ?? 0;
            if ((uint)source >= (uint)sourcePaths.Count)
                throw new InvalidOperationException("UDS transaction has an invalid physical source.");
            // Cross-file ISO-TP or request/response links are evidence
            // corruption, not a normal aggregate. Verify every witness.
            foreach (var frameIndex in transaction.FrameIndices)
            {
                if ((uint)frameIndex >= (uint)recordedFrameCount ||
                    (sourceAt?.Invoke(frameIndex) ?? 0) != source)
                    throw new InvalidOperationException(
                        "UDS transaction contains evidence from different logger files.");
            }

            var key = (source, transaction.Decoded.ServiceId);
            if (!results.TryGetValue(key, out var count))
            {
                if (results.Count == MaxGroups)
                    throw new InvalidOperationException(
                        "Too many source/service groups. Narrow the recordings and retry.");
                results[key] = count = new Counts();
            }
            count.Total++;
            count.ServiceName = transaction.Decoded.ServiceName;
            switch (transaction.Outcome)
            {
                case DiagnosticOutcome.Positive: count.Positive++; break;
                case DiagnosticOutcome.Negative: count.Negative++; break;
                case DiagnosticOutcome.NoResponse: count.NoResponse++; break;
                case DiagnosticOutcome.SuppressedResponse: count.Suppressed++; break;
                case DiagnosticOutcome.Malformed: count.Malformed++; break;
                case DiagnosticOutcome.OrphanResponse: count.Orphan++; break;
            }
            if (transaction.StartTimestampNanoseconds < count.FirstTime)
            {
                count.FirstTime = transaction.StartTimestampNanoseconds;
                count.Witness = first;
            }
            if (transaction.EndTimestampNanoseconds > count.LastTime)
                count.LastTime = transaction.EndTimestampNanoseconds;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var rows = results.OrderByDescending(pair => pair.Value.Total)
            .ThenBy(pair => pair.Key.Source)
            .ThenBy(pair => pair.Key.Service)
            .Take(maxRows)
            .Select(pair => new UdsSourceSummaryRow(
                pair.Key.Source, sourcePaths[pair.Key.Source],
                pair.Key.Service, pair.Value.ServiceName,
                pair.Value.Total, pair.Value.Positive, pair.Value.Negative,
                pair.Value.NoResponse, pair.Value.Suppressed, pair.Value.Malformed,
                pair.Value.Orphan, pair.Value.FirstTime, pair.Value.LastTime,
                pair.Value.Witness))
            .ToArray();
        return new UdsSourceSummaryReport(transactions.Count, results.Count,
            results.Count > rows.Length, rows);
    }
}
