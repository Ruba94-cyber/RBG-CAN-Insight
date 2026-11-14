using System.Text.Json;
using System.Text.Json.Serialization;

namespace RbgCanInsight.DiagData;

public enum MappingStatus
{
    /// <summary>Automatically proposed by the correlation engine, awaiting review.</summary>
    Suggested,
    /// <summary>Reviewed and confirmed by the user.</summary>
    Confirmed,
    /// <summary>Proposed candidate exists but needs verification (medium confidence).</summary>
    ToVerify,
    /// <summary>Explicitly rejected by the user.</summary>
    Rejected,
    /// <summary>No plausible CAN signal was found for this DID.</summary>
    Unmatched
}

/// <summary>One persisted DID ↔ CAN signal association.
/// Legacy Confidence is the raw candidate ranking score, not identity probability.
/// Status records a separate analyst review decision.</summary>
public sealed record DidSignalMapping(
    int Did,
    string DidName,
    string Ecu,
    string Database,
    string Message,
    string Signal,
    double Confidence,
    string Reason,
    MappingStatus Status,
    string Notes = "",
    uint? MessageIdentifier = null,
    bool? IsExtended = null,
    string? DidDefinitionFingerprint = null,
    string? CanSignalFingerprint = null)
{
    public string DidHex => $"0x{Did:X4}";
}

/// <summary>Serializable DID ↔ CAN mapping document (save, reload, exchange between users).</summary>
public sealed record MappingDocument(
    string Title,
    DateTimeOffset SavedAt,
    IReadOnlyList<DidSignalMapping> Mappings)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public const string FileExtension = ".rbgmap.json";

    public static string FileDialogFilter =>
        "RBG CAN Insight mapping (*.rbgmap.json)|*.rbgmap.json|JSON (*.json)|*.json|All files (*.*)|*.*";

    /// <summary>Write a complete replacement beside the destination before
    /// promoting it, so an interrupted write cannot truncate an existing map.
    /// A .partial file is removed on failure; source DBC/CDD/ODX inputs are untouched.</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var content = JsonSerializer.Serialize(this, Options);
        var fullPath = Path.GetFullPath(path);
        var staging = fullPath + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(staging, content);
            File.Move(staging, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    // Saved maps are untrusted input, even when they originated from a prior
    // session. Bound deserialization before allocating arbitrarily large trees.
    private const long MaxDocumentBytes = 128L * 1024 * 1024;
    private const int MaxMappingRows = 200_000;

    public static MappingDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (new FileInfo(path).Length > MaxDocumentBytes)
            throw new InvalidDataException("Mapping file exceeds the 128 MiB safety limit.");

        // Stream instead of first materializing an additional whole-file JSON string.
        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize<MappingDocument>(stream, Options)
            ?? throw new InvalidDataException("The mapping file is empty or malformed.");
        if (document.Title is null || document.Mappings is null)
            throw new InvalidDataException("Mapping file has missing required document fields.");
        if (document.Mappings.Count > MaxMappingRows)
            throw new InvalidDataException("Mapping file exceeds the 200,000-row safety limit.");

        foreach (var mapping in document.Mappings)
        {
            if (mapping is null)
                throw new InvalidDataException("Mapping file contains a null association record.");
            if (mapping.Did is < 0 or > 0xFFFF)
                throw new InvalidDataException("Mapping file contains an invalid 16-bit DID.");
            if (mapping.DidName is null || mapping.Ecu is null ||
                mapping.Database is null || mapping.Message is null ||
                mapping.Signal is null || mapping.Reason is null || mapping.Notes is null)
                throw new InvalidDataException("Mapping file contains a null identity or review field.");
            if (!Enum.IsDefined(typeof(MappingStatus), mapping.Status))
                throw new InvalidDataException("Mapping file contains an unknown review status.");
            if (!double.IsFinite(mapping.Confidence) ||
                mapping.Confidence is < 0 or > 1)
                throw new InvalidDataException("Mapping candidate score must be finite and between 0 and 1.");
            if (mapping.MessageIdentifier is { } id &&
                (id > 0x1FFF_FFFFu || mapping.IsExtended == false && id > 0x7FFu))
                throw new InvalidDataException(
                    "Mapping file contains a CAN identifier outside its declared frame format.");
        }
        return document;
    }
}
