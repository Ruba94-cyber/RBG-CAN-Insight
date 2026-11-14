using RbgCanInsight.Domain;

namespace RbgCanInsight.TraceIO;

/// <summary>Severity of a source-file import observation. Informational skips
/// (headers, unsupported non-CAN object kinds) are never evidence of corruption.</summary>
public enum TraceImportIssueSeverity { Information, Warning, Error }

public sealed record TraceImportIssue(
    TraceImportIssueSeverity Severity,
    string Code,
    string Explanation,
    long? LineNumber = null,
    long? ByteOffset = null);

public sealed record TraceChannelHealth(
    int Channel,
    long Frames,
    long ErrorFrames,
    long RemoteFrames,
    long CanFdFrames,
    long OutOfOrderTimestamps,
    long? FirstTimestampNanoseconds = null,
    long? LastTimestampNanoseconds = null);

/// <summary>Immutable, bounded source provenance collected while reading a
/// CAN file. Never includes original payloads or raw source lines.</summary>
public sealed record TraceImportHealthSnapshot(
    string Format,
    bool Completed,
    string? FailureMessage,
    long Frames,
    long PhysicalLines,
    long MetadataLines,
    long RejectedRecords,
    long UnsupportedObjectRecords,
    long TopLevelBlfObjects,
    long BlfContainers,
    long TopLevelPaddingBytes,
    long UnexpectedTopLevelBytes,
    long ContainerPaddingBytes,
    long UnexpectedContainerBytes,
    long OutOfOrderTimestamps,
    IReadOnlyList<TraceChannelHealth> Channels,
    IReadOnlyList<TraceImportIssue> IssueExamples,
    long LinFrames = 0,
    long FlexRayFrames = 0,
    long CanXlFrames = 0,
    string BusSummary = "",
    IReadOnlyList<LoggedBusStatistic>? LoggedBusStatistics = null,
    IReadOnlyList<LoggedControllerStatus>? LoggedControllerStatuses = null)
{
    public bool RequiresReview => !Completed || RejectedRecords != 0 ||
        UnexpectedTopLevelBytes != 0 || UnexpectedContainerBytes != 0 ||
        FailureMessage is not null ||
        IssueExamples.Any(item => item.Severity is TraceImportIssueSeverity.Warning or TraceImportIssueSeverity.Error);

    public string Status => !Completed
        ? FailureMessage is null ? "Not fully enumerated" : "Import failed"
        : Frames == 0 ? (LinFrames + FlexRayFrames + CanXlFrames > 0 ? "No CAN frames (other bus frames imported)" : "No supported CAN frames")
        : RequiresReview ? "Imported with warnings — review source quality"
        : UnsupportedObjectRecords != 0 ? "CAN extracted; unrelated/unsupported objects skipped"
        : "CAN import complete (supported records)";

    public string Summary =>
        $"{Status} · {Frames:N0} CAN frames · {RejectedRecords:N0} rejected data records" +
        (BusSummary.Length > 0 ? " · " + BusSummary : "") +
        (UnsupportedObjectRecords > 0 ? $" · {UnsupportedObjectRecords:N0} unsupported object(s)" : "") +
        (OutOfOrderTimestamps > 0 ? $" · {OutOfOrderTimestamps:N0} out-of-order timestamp(s)" : "");
}

/// <summary>Single-reader mutable collector, not shared between imports. Only
/// incremented as the async reader is enumerated. Snapshot can be stored after
/// import, without retaining file data or millions of per-record diagnostics.</summary>
public sealed class TraceImportHealth
{
    private const int MaxIssueExamples = 64;
    private readonly List<TraceImportIssue> _examples = [];
    private readonly Dictionary<int, ChannelAccumulator> _channels = [];
    private string _format = "unknown";
    private bool _completed;
    private string? _failure;
    private long? _previousTimestamp;
    private long _physicalLines;
    private long _metadataLines;
    private long _rejectedRecords;
    private long _unsupportedObjects;
    private long _topLevelBlfObjects;
    private long _blfContainers;
    private long _paddingBytes;
    private long _unexpectedBytes;
    private long _containerPaddingBytes;
    private long _unexpectedContainerBytes;
    private long _outOfOrder;
    private long _frames;
    private readonly List<LoggedBusStatistic> _loggedStatistics = [];
    private readonly List<LoggedControllerStatus> _loggedStatuses = [];
    private const int MaxLoggedEntries = 500_000;

    /// <summary>Records a logger-written bus statistic line (bounded).</summary>
    public void RecordLogged(LoggedBusStatistic statistic)
    {
        if (_loggedStatistics.Count < MaxLoggedEntries) _loggedStatistics.Add(statistic);
    }

    /// <summary>Records a logger-written controller status line (bounded).</summary>
    public void RecordLogged(LoggedControllerStatus status)
    {
        if (_loggedStatuses.Count < MaxLoggedEntries) _loggedStatuses.Add(status);
    }

    /// <summary>Side collection for LIN / FlexRay / CAN XL records found while importing.
    /// The CAN frame stream is unaffected; this is only filled when a collector is supplied.</summary>
    public BusFrameCollection BusFrames { get; } = new();

    private sealed class ChannelAccumulator
    {
        public long Frames;
        public long Errors;
        public long Remote;
        public long Fd;
        public long OutOfOrder;
        public long First = long.MaxValue;
        public long Last = long.MinValue;
    }

    public void Start(string extension) => _format = extension.TrimStart('.').ToUpperInvariant();

    public void RecordTextLine() => _physicalLines++;
    public void RecordMetadataLine() => _metadataLines++;

    public void RecordRejectedLine(string explanation, long lineNumber)
    {
        _rejectedRecords++;
        AddIssue(TraceImportIssueSeverity.Warning, "MALFORMED_RECORD",
            explanation, lineNumber);
    }

    public void RecordUnsupportedObject(uint objectType) =>
        RecordUnsupportedSourceObject("UNSUPPORTED_BLF_OBJECT",
            $"BLF object type {objectType} was skipped: it is not supported CAN frame data.");

    public void RecordUnsupportedSourceObject(string code, string explanation)
    {
        _unsupportedObjects++;
        AddIssue(TraceImportIssueSeverity.Information, code, explanation);
    }

    public void RecordRejectedSourceRecord(string code, string explanation)
    {
        _rejectedRecords++;
        AddIssue(TraceImportIssueSeverity.Warning, code, explanation);
    }

    public void RecordFileMetadataWarning(string code, string explanation) =>
        AddIssue(TraceImportIssueSeverity.Warning, code, explanation);

    public void RecordMalformedObject(uint objectType, string reason)
    {
        _rejectedRecords++;
        AddIssue(TraceImportIssueSeverity.Warning, "MALFORMED_BLF_OBJECT",
            $"BLF object type {objectType}: {reason}");
    }

    public void RecordBlfObject(bool isContainer)
    {
        _topLevelBlfObjects++;
        if (isContainer) _blfContainers++;
    }

    public void RecordTopLevelBytes(long count, bool nonZero, long? firstOffset = null)
    {
        if (count <= 0) return;
        if (nonZero)
        {
            _unexpectedBytes += count;
            AddIssue(TraceImportIssueSeverity.Warning, "BLF_RESYNCHRONIZATION",
                "Nonzero bytes outside BLF objects required resynchronization; inspect file integrity.",
                byteOffset: firstOffset);
        }
        else
            _paddingBytes += count;
    }

    public void RecordContainerBytes(int count, bool nonzero)
    {
        if (count <= 0) return;
        if (nonzero)
        {
            _unexpectedContainerBytes += count;
            AddIssue(TraceImportIssueSeverity.Warning, "BLF_CONTAINER_RESYNCHRONIZATION",
                "Nonpadding bytes between embedded BLF objects required resynchronization. " +
                "Original file offset is unavailable after container decompression.");
        }
        else
            _containerPaddingBytes += count;
    }

    public void RecordIncompleteBlfTail(int count)
    {
        if (count <= 0) return;
        _rejectedRecords++;
        AddIssue(TraceImportIssueSeverity.Warning, "BLF_INCOMPLETE_CONTAINER_TAIL",
            $"A BLF container ended with {count} unconsumed nonpadding byte(s).");
    }

    public void RecordFrame(in CanFrame frame)
    {
        _frames++;
        if (!_channels.TryGetValue(frame.Channel, out var channel))
            _channels[frame.Channel] = channel = new ChannelAccumulator();
        channel.Frames++;
        if (frame.TimestampNanoseconds < channel.First)
            channel.First = frame.TimestampNanoseconds;
        if (frame.TimestampNanoseconds > channel.Last)
            channel.Last = frame.TimestampNanoseconds;
        if (frame.IsError) channel.Errors++;
        if (frame.Flags.HasFlag(CanFrameFlags.Remote)) channel.Remote++;
        if (frame.Flags.HasFlag(CanFrameFlags.FlexibleDataRate)) channel.Fd++;
        if (_previousTimestamp is { } previous &&
            frame.TimestampNanoseconds < previous)
        {
            _outOfOrder++;
            channel.OutOfOrder++;
            AddIssue(TraceImportIssueSeverity.Information, "TIMESTAMP_ORDER",
                "A recorded timestamp precedes the preceding imported frame. " +
                "This may reflect multi-channel logger ordering rather than corruption.");
        }
        _previousTimestamp = frame.TimestampNanoseconds;
    }

    public void Complete() => _completed = true;

    public void Fail(Exception error)
    {
        _failure = error.GetType().Name + ": " + error.Message;
        AddIssue(TraceImportIssueSeverity.Error, "IMPORT_FAILED",
            "The importer raised an error before completing the source file. " +
            "The loaded frame list must not be treated as a complete capture.");
    }

    private void AddIssue(TraceImportIssueSeverity severity, string code, string explanation,
        long? lineNumber = null, long? byteOffset = null)
    {
        // Keep a first example for each code, plus a few line-specific examples.
        // Never store payloads, VINs, source lines or unbounded traces here.
        if (_examples.Count >= MaxIssueExamples) return;
        if (lineNumber is null && _examples.Any(item => item.Code == code)) return;
        if (lineNumber is not null && _examples.Count(item => item.Code == code) >= 8) return;
        _examples.Add(new TraceImportIssue(severity, code, explanation, lineNumber, byteOffset));
    }

    public TraceImportHealthSnapshot Snapshot() => new(
        _format,
        _completed,
        _failure,
        _frames,
        _physicalLines,
        _metadataLines,
        _rejectedRecords,
        _unsupportedObjects,
        _topLevelBlfObjects,
        _blfContainers,
        _paddingBytes,
        _unexpectedBytes,
        _containerPaddingBytes,
        _unexpectedContainerBytes,
        _outOfOrder,
        _channels.OrderBy(pair => pair.Key).Select(pair =>
            new TraceChannelHealth(pair.Key, pair.Value.Frames,
                pair.Value.Errors, pair.Value.Remote, pair.Value.Fd,
                pair.Value.OutOfOrder,
                pair.Value.Frames > 0 ? pair.Value.First : null,
                pair.Value.Frames > 0 ? pair.Value.Last : null)).ToArray(),
        _examples.ToArray(),
        BusFrames.Lin.Count,
        BusFrames.FlexRay.Count,
        BusFrames.CanXl.Count,
        BusFrames.Summary(),
        _loggedStatistics.ToArray(),
        _loggedStatuses.ToArray());
}
