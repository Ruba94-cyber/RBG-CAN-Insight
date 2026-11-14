using RbgCanInsight.Correlation;
using RbgCanInsight.Diagnostics;

namespace RbgCanInsight.App.ViewModels;

/// <summary>One evidence-backed DTC history in Data Studio. All source frames
/// and catalog definitions are references to the already-built analysis.</summary>
public sealed class DataStudioFaultRow(FaultMemoryEntry fault,
    IReadOnlyList<string> sourcePaths)
{
    public FaultMemoryEntry Fault { get; } = fault;
    public int OriginalSourceIndex => Fault.OriginalSourceIndex;
    public string OriginalSourcePath => OriginalSourceIndex >= 0 &&
        OriginalSourceIndex < sourcePaths.Count ? sourcePaths[OriginalSourceIndex] : "";
    public string Recording => OriginalSourceIndex < 0 ? "Current recording"
        : $"Logger {OriginalSourceIndex + 1} · " +
          System.IO.Path.GetFileName(OriginalSourcePath);
    public string Code => Fault.CodeText;
    public string Ecu => Fault.Ecu;
    public string Bus => Fault.ChannelText;
    public string Name => string.IsNullOrWhiteSpace(Fault.Name)
        ? Fault.DefinitionEcuMatched
            ? "DTC identified; no short name declared"
            : "No unambiguous ECU-matched DTC definition"
        : Fault.Name;
    public string Description => Fault.Description;
    /// <summary>OEM table statements, shown separately from observed status and CAN evidence.</summary>
    public string DtcTableDetails => Fault.DefinitionAttributes.Count == 0 ? string.Empty :
        "OEM DTC table reference (not observed ECU data): " +
        string.Join(" · ", Fault.DefinitionAttributes.OrderBy(item => item.Key,
            StringComparer.OrdinalIgnoreCase).Select(item => $"{item.Key}: {item.Value}"));
    public string DtcDefinitionConflictText => Fault.DefinitionConflicts.Count == 0
        ? string.Empty
        : "Conflicting ECU-matched DTC reference definitions — not used as confirmed interpretation: " +
          string.Join(" | ", Fault.DefinitionConflicts);
    public string CurrentStatus => Fault.CurrentStatusText;
    public bool CurrentlyCleared => Fault.CurrentStatus == 0 && Fault.WasCleared;
    public string StateText => CurrentlyCleared ? "Cleared" : "Observed / not confirmed cleared";
    public int Reads => Fault.Occurrences;
    public int RecordCount => Fault.LatestRecords.Count;
    public string RecordText => RecordCount == 0 ? "No decoded snapshot/extended records"
        : $"{RecordCount} latest snapshot / extended-data record(s)";
    public bool HasCatalogDefinition => Fault.DefinitionEcuMatched;
    public string Catalog => Fault.DefinitionText;
    public string CatalogPath => (Fault.DefinitionSourceFiles.Count > 0
        ? string.Join("; ", Fault.DefinitionSourceFiles)
        : Fault.DefinitionSourceFile) +
        (string.IsNullOrWhiteSpace(Fault.DefinitionSourceSheet) ? string.Empty :
            $" · Excel worksheet: {Fault.DefinitionSourceSheet}");
    public string SourceEvidence => Fault.Timeline.Count == 0
        ? "No original frame evidence"
        : string.Join("; ", Fault.Timeline.Take(8).Select(item =>
              $"{item.Operation} @ {item.TimestampNanoseconds / 1e9:F6}s · " +
              $"frames {string.Join(", ", item.FrameIndices.Take(6).Select(index => index + 1))}"));
    public bool Matches(string text) =>
        Code.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        Ecu.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        Bus.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        Description.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        DtcTableDetails.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        DtcDefinitionConflictText.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        Catalog.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        CatalogPath.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        OriginalSourcePath.Contains(text, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One evidence-checked lead from a selected DTC snapshot DID
/// to an independently fitted DBC signal. A lead is not a diagnosis.</summary>
public sealed class DataStudioSnapshotCanRow(DtcSnapshotCanLink link)
{
    public DtcSnapshotCanLink Link { get; } = link;
    public string Did => $"0x{Link.Did:X4}";
    public string RecordedField => Link.SnapshotField.Name;
    public string MatchScope => Link.FieldScope;
    public string SnapshotValue => string.IsNullOrWhiteSpace(Link.SnapshotField.Physical)
        ? Link.SnapshotField.Raw
        : Link.SnapshotField.Physical +
          (string.IsNullOrWhiteSpace(Link.SnapshotField.Unit)
              ? "" : " " + Link.SnapshotField.Unit);
    public string CddOdx => System.IO.Path.GetFileName(Link.Correlation.Candidate.Did.SourceFile);
    public string Dbc => Link.Correlation.Candidate.Database;
    public string CanSignal => Link.Correlation.SignalText;
    public string CanSource => Link.Correlation.SignalSourceText;
    public string Verdict => Link.Correlation.Verdict.ToString();
    public int Pairs => Link.Correlation.PairCount;
    public string Explanation => Link.EvidenceSummary + " " + Link.Correlation.Explanation;
    public string SnapshotFrameEvidence => Link.SnapshotField.SourceEvidenceText;
}

/// <summary>A DID-to-DBC hypothesis is NOT a verified ECU mapping: show
/// its metadata definitions and original frame evidence independently.</summary>
public sealed class DataStudioMappingRow(UdsCanCorrelationResult result,
    IReadOnlyList<string> sourcePaths)
{
    public UdsCanCorrelationResult Result { get; } = result;
    public int OriginalSourceIndex => Result.OriginalSourceIndex;
    public string OriginalSourcePath => OriginalSourceIndex >= 0 &&
        OriginalSourceIndex < sourcePaths.Count ? sourcePaths[OriginalSourceIndex] : "";
    public string Recording => OriginalSourceIndex < 0
        ? "Recording unverified" : $"Logger {OriginalSourceIndex + 1} · " +
          System.IO.Path.GetFileName(OriginalSourcePath);
    public string Did => Result.Candidate.Did.DidHex;
    public string DidName => Result.Candidate.Did.DisplayName;
    public string Ecu => Result.Candidate.Did.Ecu;
    public string CddOdx => System.IO.Path.GetFileName(Result.Candidate.Did.SourceFile);
    public string CddOdxPath => Result.Candidate.Did.SourceFile;
    public string Dbc => Result.Candidate.Database;
    public string Signal => Result.SignalText;
    public string CanSource => Result.SignalSourceText;
    public string Verdict => Result.Verdict.ToString();
    public int PairCount => Result.PairCount;
    public string Fit => Result.PairCount == 0 ? "No recorded value pairs"
        : Result.MetricsText;
    public string Explanation => Result.Explanation;
    public bool Matches(string text) =>
        Did.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        DidName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        Ecu.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        CddOdxPath.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        Dbc.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        Signal.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        CanSource.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        Verdict.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        OriginalSourcePath.Contains(text, StringComparison.OrdinalIgnoreCase);
}
