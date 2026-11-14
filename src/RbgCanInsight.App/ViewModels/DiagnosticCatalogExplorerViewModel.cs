using System.Globalization;
using System.IO;
using RbgCanInsight.App.Common;
using RbgCanInsight.DiagData;

namespace RbgCanInsight.App.ViewModels;

/// <summary>A source-backed field definition from a CDD/ODX DID, DTC or routine.
/// Offset is relative to the diagnostic data-record payload, not the ISO-TP frame.</summary>
public sealed record DiagnosticFieldInspectorRow(string Section, DiagnosticFieldDefinition Field)
{
    public string Name => Field.DisplayName;
    public string RawType => Field.DataType;
    public string Unit => Field.Unit;
    public string ByteOrder => string.IsNullOrWhiteSpace(Field.ByteOrder)
        ? "Not declared" : Field.ByteOrder;
    public string BitOffsetText => Field.BitOffset >= 0
        ? Field.BitOffset.ToString(CultureInfo.InvariantCulture) : "Not declared";
    public string BitLengthText => Field.BitLength > 0
        ? Field.BitLength.ToString(CultureInfo.InvariantCulture) : "Variable/unknown";
    public string PositionText => Field.BitOffset >= 0 && Field.BitLength > 0
        ? $"Payload bits {Field.BitOffset}–{(long)Field.BitOffset + Field.BitLength - 1} " +
          $"(starts at byte {Field.BitOffset / 8}, bit {Field.BitOffset % 8}; " +
          $"spans bytes {Field.BitOffset / 8}–{((long)Field.BitOffset + Field.BitLength - 1) / 8})"
        : "No reliable fixed payload bit boundaries in this description.";
    public string EncodingText => $"{Field.DataType} · {(Field.IsSigned ? "signed" : "unsigned/unspecified")} · {ByteOrder}";
    public string ScalingText => !Field.SupportsPhysicalConversion
        ? "No verified physical conversion: coded raw value only"
        : Field.Factor is { } factor && Field.Offset is { } offset
        ? $"physical = raw × {factor:G9} {(offset < 0 ? "−" : "+")} {Math.Abs(offset):G9}"
        : "Scaling not completely declared";
    public string RangeText => Field.Minimum is { } min && Field.Maximum is { } max
        ? $"{min:G9} .. {max:G9}" + (string.IsNullOrEmpty(Unit) ? "" : $" {Unit}")
        : "Limits not declared";
    public string Description => Field.Description;
    public int EnumCount => Field.EnumValues.Count;
    public IReadOnlyList<DiagnosticEnumValue> Values => Field.EnumValues;
}

/// <summary>One source-backed engineering hierarchy row; no bit offset is inferred.</summary>
public sealed record EngineeringParameterInspectorRow(DiagnosticEngineeringParameter Parameter)
{
    public string IndentedName =>
        new string('\u00A0', Math.Min(Parameter.Depth, 24) * 2) +
        (Parameter.Depth > 0 ? "↳ " : "") + Parameter.DisplayName;
    public string SourceId => Parameter.SourceId;
    public string ParCode => Parameter.DeclaredParCode;
    /// <summary>Raw CDD/ODX coordinates, not a verified payload bit offset.</summary>
    public string SourcePositionText => string.Join(", ", new[]
    {
        Parameter.DeclaredBytePosition.Length > 0
            ? "byte=" + Parameter.DeclaredBytePosition : "",
        Parameter.DeclaredBitPosition.Length > 0
            ? "bit=" + Parameter.DeclaredBitPosition : "",
        Parameter.DeclaredBitOffset.Length > 0
            ? "bitOffset=" + Parameter.DeclaredBitOffset : ""
    }.Where(value => value.Length > 0));
    public int ValueMeaningCount => Parameter.DeclaredValues.Count;
    public int Depth => Parameter.Depth;
    public string Kind => Parameter.Kind;
    public string Origin => Parameter.DefinitionOrigin;
    public string ReferenceStatus => Parameter.ReferenceStatus;
    public string Description => Parameter.Description;
    public string Type => Parameter.DataType;
    public string Reference => Parameter.TypeReference;
    public string Path => Parameter.Path;
}

public sealed record DiagnosticDefinitionRow(
    string Kind,
    string Identifier,
    string Name,
    string Ecu,
    string SourceFile,
    string Summary,
    IReadOnlyList<DiagnosticFieldInspectorRow> Fields,
    int? Did = null,
    int EngineeringParameterCount = 0,
    string EngineeringSearchText = "",
    int EngineeringIssueCount = 0,
    IReadOnlyList<EngineeringParameterInspectorRow>? EngineeringNodes = null)
{
    public string Key => $"{SourceFile}|{Ecu}|{Kind}|{Identifier}|{Name}";
    public int FieldCount => Fields.Count;
    public IReadOnlyList<EngineeringParameterInspectorRow> EngineeringRows =>
        EngineeringNodes ?? [];
    public string FieldsAndParCount => $"{FieldCount}/{EngineeringParameterCount}";
    // Immutable precomputed index: a mutable cache field would alter record
    // value equality after first access and destabilize WPF selection.
    public string SearchBlob { get; } = string.Join(' ',
        Kind, Identifier, Name, Ecu, SourceFile, Summary, EngineeringSearchText,
        string.Join(' ', Fields.Select(field => field.Name)));
}

/// <summary>
/// Standalone technical browsing of the existing offline CDD/ODX model.
/// Complements UDS transaction playback; no ECU communication or DOP inference.
/// </summary>
public sealed class DiagnosticCatalogExplorerViewModel : ObservableObject
{
    private IReadOnlyList<DiagnosticDefinitionRow> _all = [];
    private IReadOnlyList<DiagnosticDefinitionRow> _rows = [];
    private IReadOnlyList<DiagnosticFieldInspectorRow> _fields = [];
    private IReadOnlyList<DiagnosticEnumValue> _enumValues = [];
    private IReadOnlyList<DiagnosticDeclaredValueMeaning> _engineeringValues = [];
    private IReadOnlyList<EngineeringParameterInspectorRow> _engineeringRows = [];
    private EngineeringParameterInspectorRow? _selectedEngineeringParameter;
    private int _inspectorTabIndex;
    private int _valueInspectorTabIndex;
    private DiagnosticDefinitionRow? _selectedDefinition;
    private DiagnosticFieldInspectorRow? _selectedField;
    private string _searchText = string.Empty;
    private string _engineeringFilterText = string.Empty;
    private IReadOnlyList<string> _ecuFilters = ["All ECUs"];
    private string _selectedEcuFilter = "All ECUs";

    public IReadOnlyList<DiagnosticDefinitionRow> Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    public IReadOnlyList<DiagnosticFieldInspectorRow> Fields
    {
        get => _fields;
        private set => SetProperty(ref _fields, value);
    }

    public IReadOnlyList<DiagnosticEnumValue> EnumValues
    {
        get => _enumValues;
        private set => SetProperty(ref _enumValues, value);
    }

    public IReadOnlyList<EngineeringParameterInspectorRow> EngineeringRows
    {
        get => _engineeringRows;
        private set => SetProperty(ref _engineeringRows, value);
    }

    public IReadOnlyList<DiagnosticDeclaredValueMeaning> EngineeringValues
    {
        get => _engineeringValues;
        private set => SetProperty(ref _engineeringValues, value);
    }

    public int ValueInspectorTabIndex
    {
        get => _valueInspectorTabIndex;
        set => SetProperty(ref _valueInspectorTabIndex, value);
    }

    public EngineeringParameterInspectorRow? SelectedEngineeringParameter
    {
        get => _selectedEngineeringParameter;
        set
        {
            if (!SetProperty(ref _selectedEngineeringParameter, value)) return;
            EngineeringValues = value?.Parameter.DeclaredValues ?? [];
            OnPropertyChanged(nameof(EngineeringDetails));
            OnPropertyChanged(nameof(InspectorDetails));
        }
    }

    public int InspectorTabIndex
    {
        get => _inspectorTabIndex;
        set
        {
            if (!SetProperty(ref _inspectorTabIndex, value)) return;
            EnumValues = value == 0 ? SelectedField?.Values ?? [] : [];
            ValueInspectorTabIndex = value == 1 ? 1 : 0;
            OnPropertyChanged(nameof(InspectorDetails));
        }
    }

    public DiagnosticDefinitionRow? SelectedDefinition
    {
        get => _selectedDefinition;
        set
        {
            if (!SetProperty(ref _selectedDefinition, value)) return;
            Fields = value?.Fields ?? [];
            SelectedField = Fields.FirstOrDefault();
            ApplyEngineeringFilter();
            OnPropertyChanged(nameof(DefinitionDetails));
            OnPropertyChanged(nameof(InspectorDetails));
            OnPropertyChanged(nameof(CanExportSelectedDid));
        }
    }

    public bool CanExportSelectedDid => SelectedDefinition is { Kind: "DID", Did: not null };

    public DiagnosticFieldInspectorRow? SelectedField
    {
        get => _selectedField;
        set
        {
            if (!SetProperty(ref _selectedField, value)) return;
            EnumValues = InspectorTabIndex == 0 ? value?.Values ?? [] : [];
            OnPropertyChanged(nameof(FieldDetails));
            OnPropertyChanged(nameof(InspectorDetails));
        }
    }

    public string EngineeringFilterText
    {
        get => _engineeringFilterText;
        set
        {
            if (SetProperty(ref _engineeringFilterText, value))
                ApplyEngineeringFilter();
        }
    }

    private void ApplyEngineeringFilter()
    {
        var all = SelectedDefinition?.EngineeringRows ?? [];
        var terms = (_engineeringFilterText ?? string.Empty).Split(' ',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        EngineeringRows = terms.Length == 0 ? all : all.Where(row =>
            terms.All(term =>
                row.Parameter.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                row.Parameter.LongName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                row.Parameter.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                row.SourceId.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                row.ParCode.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                row.Type.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                row.Kind.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                row.Reference.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                row.Origin.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                row.ReferenceStatus.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                row.Path.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                row.SourcePositionText.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                row.Parameter.DeclaredValues.Any(meaning =>
                    meaning.Meaning.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    meaning.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    meaning.LowerRaw.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    meaning.UpperRaw.Contains(term, StringComparison.OrdinalIgnoreCase)))).ToArray();
        var selectedPath = SelectedEngineeringParameter?.Path;
        SelectedEngineeringParameter = EngineeringRows.FirstOrDefault(row =>
            row.Path == selectedPath) ?? EngineeringRows.FirstOrDefault();
    }

    public IReadOnlyList<string> EcuFilters
    {
        get => _ecuFilters;
        private set => SetProperty(ref _ecuFilters, value);
    }

    public string SelectedEcuFilter
    {
        get => _selectedEcuFilter;
        set
        {
            if (SetProperty(ref _selectedEcuFilter, value ?? "All ECUs"))
                ApplyFilter();
        }
    }

    public string? SelectedEcuForExport =>
        string.Equals(SelectedEcuFilter, "All ECUs", StringComparison.Ordinal)
            ? null : SelectedEcuFilter;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                ApplyFilter();
        }
    }

    public string Summary => _all.Count == 0 ? "Load a CDD/ODX file to inspect diagnostic definitions."
        : $"{_all.Count:N0} definitions · {_all.Count(item => item.Kind == "DID"):N0} DIDs · " +
          $"{_all.Count(item => item.Kind == "DTC"):N0} DTCs · " +
          $"{_all.Count(item => item.Kind == "Routine"):N0} routines · " +
          $"{_all.Count(item => item.Kind == "Service"):N0} services · " +
          $"{_all.Select(item => item.Ecu).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} ECUs";

    public string DefinitionDetails => SelectedDefinition is { } selected
        ? $"{selected.Kind} {selected.Identifier} · {selected.Name} · ECU {selected.Ecu}" +
          Environment.NewLine + selected.Summary + Environment.NewLine +
          $"{selected.Fields.Count:N0} described fields · " +
          $"{selected.EngineeringParameterCount:N0} engineering PAR/sub-PAR nodes · " +
          $"Source: {Path.GetFileName(selected.SourceFile)}" +
          (selected.EngineeringIssueCount > 0
              ? Environment.NewLine +
                $"ATTENTION: {selected.EngineeringIssueCount} competing or conflicting DID " +
                "definitions. Review the PAR Extraction Coverage workbook before interpreting values."
              : string.Empty)
        : "Select a diagnostic identifier to inspect its source definition.";

    public string EngineeringDetails => SelectedEngineeringParameter is { } selected
        ? $"{selected.Parameter.DisplayName} · {selected.Kind} · level {selected.Depth}" +
          Environment.NewLine + $"Source ID: {selected.SourceId} · PAR code: " +
          $"{(selected.ParCode.Length > 0 ? selected.ParCode : "not declared")}" +
          Environment.NewLine + $"Type: {selected.Type} · " +
          $"{selected.ValueMeaningCount} declared value meaning(s)" +
          Environment.NewLine + $"Source service: {selected.Origin}" +
          Environment.NewLine + $"Type ref: {selected.Reference} · Path: {selected.Path}" +
          (selected.SourcePositionText.Length == 0 ? string.Empty :
              Environment.NewLine + "Source coordinates (record origin unverified): " +
              selected.SourcePositionText) +
          (string.IsNullOrWhiteSpace(selected.ReferenceStatus) ? string.Empty :
              Environment.NewLine + "Reference warning: " + selected.ReferenceStatus) +
          (string.IsNullOrWhiteSpace(selected.Description) ? string.Empty :
              Environment.NewLine + selected.Description)
        : "Select an engineering parameter to inspect its definition and provenance.";

    public string InspectorDetails =>
        InspectorTabIndex == 1 ? EngineeringDetails : FieldDetails;

    public string FieldDetails => SelectedField is { } selected
        ? $"{selected.Section} / {selected.Name} · {selected.PositionText}" + Environment.NewLine +
          $"{selected.EncodingText} · {selected.ScalingText} · {selected.RangeText}" +
          (string.IsNullOrWhiteSpace(selected.Description)
              ? string.Empty : Environment.NewLine + selected.Description)
        : "Select a field for its exact payload offset, data encoding, scaling and enumerated meanings.";

    /// <summary>Focus an already-loaded source-backed diagnostic definition.
    /// Never pick the first of several matching ECU/identifiers. A nested
    /// field/PAR must also have exactly one matching path within that root.</summary>
    public bool TryFocus(DiagnosticDiffNavigationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var matching = _all.Where(row =>
            string.Equals(row.Kind, target.RootKind, StringComparison.Ordinal) &&
            string.Equals(row.Identifier, target.Identifier, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(row.Ecu, target.Ecu, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(row.SourceFile, target.SourceFile, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        if (matching.Length != 1) return false;
        var root = matching[0];
        EngineeringParameterInspectorRow? parameter = null;
        DiagnosticFieldInspectorRow? field = null;
        if (target.ChildScope == "Engineering PAR")
        {
            var matches = root.EngineeringRows.Where(item =>
                string.Equals(item.Path, target.ChildPath, StringComparison.Ordinal))
                .Take(2).ToArray();
            if (matches.Length != 1) return false;
            parameter = matches[0];
        }
        else if (target.ChildScope.Length > 0)
        {
            var section = target.ChildScope switch
            {
                "DID Field" => "DID data record",
                "DTC Snapshot" => "Snapshot",
                "DTC Extended" => "Extended data",
                "Routine Input" => "Input",
                "Routine Output" => "Output",
                _ => string.Empty
            };
            if (section.Length == 0) return false;
            var matches = root.Fields.Where(item =>
                string.Equals(item.Section, section, StringComparison.Ordinal) &&
                string.Equals(item.Field.Name, target.ChildPath, StringComparison.Ordinal))
                .Take(2).ToArray();
            if (matches.Length != 1) return false;
            field = matches[0];
        }

        // Validate the target against _all BEFORE clearing user filters. A
        // failure never destroys their previous search or selection.
        SearchText = string.Empty;
        SelectedEcuFilter = target.Ecu;
        SelectedDefinition = Rows.FirstOrDefault(row =>
            ReferenceEquals(row, root));
        if (!ReferenceEquals(SelectedDefinition, root)) return false;

        if (parameter is not null)
        {
            EngineeringFilterText = string.Empty;
            InspectorTabIndex = 1;
            SelectedEngineeringParameter = EngineeringRows.First(item =>
                string.Equals(item.Path, target.ChildPath, StringComparison.Ordinal));
        }
        else if (field is not null)
        {
            InspectorTabIndex = 0;
            SelectedField = Fields.First(item =>
                string.Equals(item.Section, field.Section, StringComparison.Ordinal) &&
                string.Equals(item.Field.Name, field.Field.Name, StringComparison.Ordinal));
        }
        return true;
    }

    public void SetCatalogs(IReadOnlyList<DidCatalog> catalogs)
    {
        var key = SelectedDefinition?.Key;
        var rows = new List<DiagnosticDefinitionRow>();
        foreach (var catalog in catalogs)
        {
            foreach (var did in catalog.Parameters)
            {
                IReadOnlyList<DiagnosticFieldInspectorRow> fields = did.Fields.Count > 0
                    ? did.Fields.Select(field => new DiagnosticFieldInspectorRow("DID data record", field)).ToArray()
                    : did.BitLength is > 0 and <= 512
                        ? [new DiagnosticFieldInspectorRow("Top-level DID metadata (layout unverified)",
                            new DiagnosticFieldDefinition(did.Name, did.LongName,
                                "The DID declares a length, but no reliable response data-record " +
                                "origin or field encoding. Offset and physical conversion are unverified.",
                                -1, did.BitLength.Value, did.DataType, did.Unit,
                                did.Minimum, did.Maximum, did.Factor, did.Offset,
                                did.DataType.StartsWith("A_INT", StringComparison.OrdinalIgnoreCase),
                                "Not declared", SupportsPhysicalConversion: false))]
                        : [];
                rows.Add(new DiagnosticDefinitionRow(
                    "DID", $"0x{did.Did:X4}", did.DisplayName, did.Ecu, did.SourceFile,
                    $"Data type: {did.DataType}; length: {(did.BitLength is { } len ? len + " bits" : "not declared")}; " +
                    $"unit: {did.Unit}; {did.Description}",
                    fields,
                    Did: did.Did,
                    EngineeringParameterCount: did.EngineeringParameters.Count,
                    EngineeringSearchText: string.Join(' ',
                        did.EngineeringParameters.Select(parameter => string.Join(' ',
                            parameter.Name, parameter.LongName, parameter.Description,
                            parameter.SourceId, parameter.TypeReference,
                            parameter.DefinitionOrigin, parameter.DeclaredParCode,
                            parameter.DeclaredBytePosition, parameter.DeclaredBitPosition,
                            parameter.DeclaredBitOffset,
                            string.Join(' ', parameter.DeclaredValues.Select(meaning =>
                                string.Join(' ', meaning.LowerRaw, meaning.UpperRaw,
                                    meaning.Meaning, meaning.Description, meaning.Status)))))),
                    EngineeringIssueCount: did.EngineeringIssues.Count,
                    EngineeringNodes: did.EngineeringParameters
                        .Select(parameter => new EngineeringParameterInspectorRow(parameter))
                        .ToArray()));
            }

            foreach (var dtc in catalog.DtcDefinitions)
            {
                rows.Add(new DiagnosticDefinitionRow("DTC", dtc.CodeHex,
                    dtc.DisplayName, dtc.Ecu, dtc.SourceFile, dtc.Description,
                    [.. dtc.SnapshotFields.Select(field => new DiagnosticFieldInspectorRow("Snapshot", field)),
                     .. dtc.ExtendedDataFields.Select(field => new DiagnosticFieldInspectorRow("Extended data", field))]));
            }

            foreach (var routine in catalog.RoutineDefinitions)
            {
                rows.Add(new DiagnosticDefinitionRow("Routine",
                    routine.IdentifierHex, routine.DisplayName, routine.Ecu, routine.SourceFile,
                    $"Session: {routine.RequiredSession}; security: {routine.RequiredSecurityLevel}; {routine.Description}",
                    [.. routine.InputFields.Select(field => new DiagnosticFieldInspectorRow("Input", field)),
                     .. routine.OutputFields.Select(field => new DiagnosticFieldInspectorRow("Output", field))]));
            }

            foreach (var service in catalog.ServiceDefinitions)
            {
                rows.Add(new DiagnosticDefinitionRow("Service",
                    $"0x{service.ServiceId:X2}" +
                        (service.SubFunction is { } sub ? $"/0x{sub:X2}" : "") +
                        (service.Identifier is { } id ? $"/0x{id:X4}" : ""),
                    string.IsNullOrEmpty(service.LongName) ? service.Name : service.LongName,
                    service.Ecu, service.SourceFile, service.Description, []));
            }

            foreach (var connection in catalog.ConnectionDefinitions)
            {
                rows.Add(new DiagnosticDefinitionRow("Transport",
                    $"{(connection.IsExtendedIdentifier ? "29-bit" : "11-bit")}",
                    connection.Name, connection.Ecu, connection.SourceFile,
                    $"Request CAN ID: {FormatId(connection.RequestIdentifier)}; response CAN ID: " +
                    $"{FormatId(connection.ResponseIdentifier)}; addressing: {connection.AddressingType}", []));
            }

            foreach (var variant in catalog.VariantDefinitions)
            {
                rows.Add(new DiagnosticDefinitionRow("Variant", variant.Type,
                    variant.Name, catalog.Ecu, catalog.SourcePath, variant.Description, []));
            }
        }

        _all = rows.OrderBy(row => row.Ecu, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Kind, StringComparer.Ordinal)
            .ThenBy(row => row.Identifier, StringComparer.Ordinal).ToArray();
        EcuFilters = ["All ECUs", .. _all.Select(row => row.Ecu)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ecu => ecu, StringComparer.OrdinalIgnoreCase)];
        // Rebind selection to an actual item in the new collection, even
        // when an updated catalog spells the same ECU with different casing.
        var sameEcu = EcuFilters.FirstOrDefault(ecu =>
            string.Equals(ecu, _selectedEcuFilter,
                StringComparison.OrdinalIgnoreCase)) ?? "All ECUs";
        if (!string.Equals(_selectedEcuFilter, sameEcu, StringComparison.Ordinal))
        {
            _selectedEcuFilter = sameEcu;
            OnPropertyChanged(nameof(SelectedEcuFilter));
        }
        ApplyFilter(key);
        OnPropertyChanged(nameof(Summary));
    }

    private static string FormatId(uint? id) => id is { } identifier ? $"0x{identifier:X}" : "not declared";

    private void ApplyFilter(string? preferredKey = null)
    {
        var tokens = SearchText.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Rows = _all.Where(row =>
            (SelectedEcuForExport is null ||
             string.Equals(row.Ecu, SelectedEcuForExport,
                 StringComparison.OrdinalIgnoreCase)) &&
            tokens.All(token =>
                row.SearchBlob.Contains(token, StringComparison.OrdinalIgnoreCase))).ToArray();
        SelectedDefinition = Rows.FirstOrDefault(row =>
            row.Key == (preferredKey ?? _selectedDefinition?.Key)) ?? Rows.FirstOrDefault();
    }
}
