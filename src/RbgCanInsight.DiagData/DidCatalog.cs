namespace RbgCanInsight.DiagData;

public sealed record DiagnosticEnumValue(long Value, string Text, string Description = "");

/// <summary>One decodable field inside a DID, routine option/status record, snapshot or
/// extended-data record. Offsets are expressed in bits from the start of the data payload.</summary>
public sealed record DiagnosticFieldDefinition(
    string Name,
    string LongName,
    string Description,
    int BitOffset,
    int BitLength,
    string DataType,
    string Unit,
    double? Minimum,
    double? Maximum,
    double? Factor,
    double? Offset,
    bool IsSigned,
    string ByteOrder,
    IReadOnlyList<DiagnosticEnumValue>? Values = null,
    bool SupportsPhysicalConversion = true,
    OdxFieldCoding? Odx = null)
{
    /// <summary>True when the field's physical value is produced by the ODX COMPU-METHOD evaluator.</summary>
    public bool HasOdxConversion => Odx is { IsEvaluable: true };
    public string DisplayName => string.IsNullOrWhiteSpace(LongName) ? Name : LongName;
    public IReadOnlyList<DiagnosticEnumValue> EnumValues => Values ?? [];
}

public sealed record DiagnosticConnection(
    string Name,
    uint? RequestIdentifier,
    uint? ResponseIdentifier,
    bool IsExtendedIdentifier,
    string AddressingType,
    string Ecu,
    string SourceFile);

public sealed record DiagnosticVariant(string Name, string Type, string Description);

/// <summary>
/// One diagnostic data identifier (DID) as documented in a CDD/ODX description: identity plus
/// whatever physical metadata the file provides (unit, range, scaling, coded length).
/// All fields except <see cref="Did"/> and <see cref="Name"/> are best-effort.
/// </summary>
public sealed record DidParameter(
    int Did,
    string Name,
    string LongName,
    string Description,
    string Unit,
    double? Minimum,
    double? Maximum,
    double? Factor,
    double? Offset,
    string DataType,
    int? BitLength,
    string Ecu,
    string SourceFile,
    IReadOnlyList<DiagnosticFieldDefinition>? FieldDefinitions = null,
    IReadOnlyList<DiagnosticEngineeringParameter>? EngineeringParameterDefinitions = null,
    IReadOnlyList<string>? DefinitionIssues = null)
{
    /// <summary>Warnings when several service definitions share a DID or have
    /// conflicting physical layouts. Never treat merged engineering metadata as
    /// evidence that those layouts are interchangeable.</summary>
    public IReadOnlyList<string> EngineeringIssues => DefinitionIssues ?? [];

    /// <summary>Nested CDD/ODX engineering definitions; not necessarily physically decodable.</summary>
    public IReadOnlyList<DiagnosticEngineeringParameter> EngineeringParameters =>
        EngineeringParameterDefinitions ?? [];

    public string DidHex => $"0x{Did:X4}";
    public string DisplayName => string.IsNullOrWhiteSpace(LongName) ? Name : LongName;
    public IReadOnlyList<DiagnosticFieldDefinition> Fields => FieldDefinitions ?? [];
}

/// <summary>One OEM DTC definition recovered from CDD/ODX.</summary>
public sealed record DiagnosticTroubleCode(
    int Code,
    string Name,
    string DisplayText,
    string Description,
    string Ecu,
    string SourceFile,
    IReadOnlyList<DiagnosticFieldDefinition>? SnapshotDefinitions = null,
    IReadOnlyList<DiagnosticFieldDefinition>? ExtendedDataDefinitions = null)
{
    /// <summary>All exact source files used when compatible DTC metadata was combined.
    /// SourceFile remains the primary definition path for existing callers.</summary>
    public IReadOnlyList<string>? SourceFilesOverride { get; init; }
    public IReadOnlyList<string> DefinitionFiles => SourceFilesOverride ?? [SourceFile];
    /// <summary>Original Excel worksheet containing an imported DTC, when available.</summary>
    public string SourceSheet { get; init; } = string.Empty;
    /// <summary>Uninterpreted, source-declared OEM DTC-table columns such as severity,
    /// condition or service action. These are reference text, never observed ECU evidence.</summary>
    public IReadOnlyDictionary<string, string>? TableAttributesOverride { get; init; }
    public IReadOnlyDictionary<string, string> TableAttributes =>
        TableAttributesOverride ?? new Dictionary<string, string>();
    public string CodeHex => $"0x{Code:X6}";
    public string DisplayName => string.IsNullOrWhiteSpace(DisplayText) ? Name : DisplayText;
    public IReadOnlyList<DiagnosticFieldDefinition> SnapshotFields => SnapshotDefinitions ?? [];
    public IReadOnlyList<DiagnosticFieldDefinition> ExtendedDataFields => ExtendedDataDefinitions ?? [];
}

/// <summary>One RoutineControl identifier and its catalog metadata.</summary>
public sealed record DiagnosticRoutine(
    int Identifier,
    string Name,
    string LongName,
    string Description,
    string Ecu,
    string SourceFile,
    IReadOnlyList<DiagnosticFieldDefinition>? InputDefinitions = null,
    IReadOnlyList<DiagnosticFieldDefinition>? OutputDefinitions = null,
    string RequiredSession = "",
    string RequiredSecurityLevel = "")
{
    public string IdentifierHex => $"0x{Identifier:X4}";
    public string DisplayName => string.IsNullOrWhiteSpace(LongName) ? Name : LongName;
    public IReadOnlyList<DiagnosticFieldDefinition> InputFields => InputDefinitions ?? [];
    public IReadOnlyList<DiagnosticFieldDefinition> OutputFields => OutputDefinitions ?? [];
}

/// <summary>A diagnostic service entry retained for trace analysis, including its fixed
/// discriminator when the source description exposes one.</summary>
public sealed record DiagnosticServiceDefinition(
    byte ServiceId,
    byte? SubFunction,
    int? Identifier,
    string Name,
    string LongName,
    string Description,
    string Ecu,
    string SourceFile,
    DiagnosticServiceIdSource Source = DiagnosticServiceIdSource.Unspecified);

/// <summary>How a service identifier was determined.</summary>
public enum DiagnosticServiceIdSource
{
    /// <summary>The reader did not record provenance.</summary>
    Unspecified = 0,
    /// <summary>Read from the request PDU (a constant SID component) in the description file.</summary>
    Structural,
    /// <summary>Guessed from service/qualifier names because the structure gave no answer. Unreliable.</summary>
    NameHeuristic
}

public enum DiagnosticConditionKind { Session, Security, Other }

/// <summary>A pre-condition state a diagnostic service declares (ISO 22901 PRE-CONDITION-STATE-REF):
/// the state chart semantic decides whether it is a session, a security or another state.</summary>
public sealed record DiagnosticServiceCondition(
    byte ServiceId,
    int? Identifier,
    string ServiceName,
    DiagnosticConditionKind Kind,
    string StateChart,
    string State,
    string Ecu,
    string SourceFile);
/// <summary>A set of DID definitions loaded from one diagnostic description file.</summary>
public sealed record DidCatalog(
    string Name,
    string Ecu,
    string SourcePath,
    IReadOnlyList<DidParameter> Parameters,
    IReadOnlyList<DiagnosticTroubleCode>? TroubleCodes = null,
    IReadOnlyList<DiagnosticRoutine>? Routines = null,
    IReadOnlyList<DiagnosticServiceDefinition>? Services = null,
    IReadOnlyList<DiagnosticConnection>? Connections = null,
    IReadOnlyList<DiagnosticVariant>? Variants = null,
    IReadOnlyList<DiagnosticServiceCondition>? ServiceConditions = null)
{
    public IReadOnlyList<DiagnosticTroubleCode> DtcDefinitions => TroubleCodes ?? [];
    /// <summary>Pre-conditions (session, security, other states) declared on services. An empty list means none were declared or the format does not carry them; it is never proof that none are required.</summary>
    public IReadOnlyList<DiagnosticServiceCondition> ConditionDefinitions => ServiceConditions ?? [];
    public IReadOnlyList<DiagnosticRoutine> RoutineDefinitions => Routines ?? [];
    public IReadOnlyList<DiagnosticServiceDefinition> ServiceDefinitions => Services ?? [];
    public IReadOnlyList<DiagnosticConnection> ConnectionDefinitions => Connections ?? [];
    public IReadOnlyList<DiagnosticVariant> VariantDefinitions => Variants ?? [];
    /// <summary>Loads the first ECU description in a file. See <see cref="LoadAll"/> for files that
    /// hold several ECU variants.</summary>
    public static DidCatalog Load(string path) => LoadAll(path)[0];

    /// <summary>Loads every ECU description in a CDD, ODX or PDX file: one catalog per ECU variant
    /// when the ODX layers use inheritance, otherwise exactly one.</summary>
    public static IReadOnlyList<DidCatalog> LoadAll(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".cdd" => [CddCatalogReader.Load(path)],
            ".odx" or ".odx-d" or ".xml" or ".pdx" => OdxCatalogReader.LoadAll(path),
            ".csv" or ".tsv" => DtcTableReader.LoadAll(path),
            ".xlsx" => ExcelDtcTableReader.LoadAll(path),
            _ => throw new NotSupportedException(
                $"Unsupported diagnostic description '{extension}'. Supported: .cdd, .odx, .odx-d, .pdx, .csv, .tsv and .xlsx.")
        };
    }

    public static readonly IReadOnlyList<string> SupportedExtensions = [".cdd", ".odx", ".odx-d", ".pdx", ".csv", ".tsv", ".xlsx"];

    public static string FileDialogFilter =>
        "Diagnostic data (*.cdd;*.odx;*.odx-d;*.pdx;*.csv;*.tsv;*.xlsx)|*.cdd;*.odx;*.odx-d;*.pdx;*.csv;*.tsv;*.xlsx|CANdela (*.cdd)|*.cdd|ODX (*.odx;*.odx-d)|*.odx;*.odx-d|PDX package (*.pdx)|*.pdx|DTC table (*.csv;*.tsv;*.xlsx)|*.csv;*.tsv;*.xlsx|All files (*.*)|*.*";
}
