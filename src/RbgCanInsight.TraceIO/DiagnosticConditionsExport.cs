using System.Globalization;
using RbgCanInsight.DiagData;

namespace RbgCanInsight.TraceIO;

/// <summary>
/// Documentation tables for diagnostic conditions and access requirements declared in loaded
/// CDD/ODX files: which session/security/other states each service declares, the request layout
/// and response fields of every DID and routine, and an explicit statement of what was NOT
/// extracted. "No condition listed" is never presented as "no requirement": it only means the file
/// declares none (or, for CDD, that the format's conditions are not extracted here).
/// </summary>
public static class DiagnosticConditionsExport
{
    public static IReadOnlyList<ExportSheet> CreateSheets(IReadOnlyList<DidCatalog> catalogs)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        var conditions = new List<IReadOnlyList<string>>();
        var definitions = new List<IReadOnlyList<string>>();
        foreach (var catalog in catalogs.OrderBy(item => item.Ecu, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            var file = Path.GetFileName(catalog.SourcePath);
            var isOdx = string.Equals(Path.GetExtension(catalog.SourcePath), ".odx", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetExtension(catalog.SourcePath), ".pdx", StringComparison.OrdinalIgnoreCase);
            foreach (var condition in catalog.ConditionDefinitions
                         .OrderBy(item => item.ServiceId).ThenBy(item => item.Identifier ?? -1)
                         .ThenBy(item => item.Kind).ThenBy(item => item.State, StringComparer.Ordinal))
                conditions.Add([catalog.Ecu, file, $"0x{condition.ServiceId:X2}", condition.ServiceName,
                    condition.Identifier is { } id ? $"0x{id:X4}" : string.Empty,
                    condition.Kind.ToString(), condition.StateChart, condition.State]);

            string Status(byte service, int identifier)
            {
                if (!isOdx) return "Not extracted: CDD pre-condition data is not read by this version";
                return catalog.ConditionDefinitions.Any(item => item.ServiceId == service && item.Identifier == identifier)
                    ? "Pre-conditions declared in the file"
                    : "No pre-condition declared in the file (this does not prove none is required)";
            }

            string Conditions(byte service, int identifier, DiagnosticConditionKind kind) =>
                string.Join(" | ", catalog.ConditionDefinitions
                    .Where(item => item.ServiceId == service && item.Identifier == identifier && item.Kind == kind)
                    .Select(item => item.State).Distinct());

            foreach (var did in catalog.Parameters.OrderBy(item => item.Did))
            {
                // A DID can be read (0x22) and/or written (0x2E); document each declared service.
                var services = catalog.ServiceDefinitions
                    .Where(item => item.ServiceId is 0x22 or 0x2E && item.Identifier == did.Did).ToArray();
                if (services.Length == 0)
                    services = [new DiagnosticServiceDefinition(0x22, null, did.Did, did.Name, did.LongName, did.Description, did.Ecu, did.SourceFile)];
                foreach (var service in services)
                {
                    var layout = service.ServiceId == 0x22
                        ? $"22 {did.Did >> 8:X2} {did.Did & 0xFF:X2}  →  62 {did.Did >> 8:X2} {did.Did & 0xFF:X2} <data record>"
                        : $"2E {did.Did >> 8:X2} {did.Did & 0xFF:X2} <data record>  →  6E {did.Did >> 8:X2} {did.Did & 0xFF:X2}";
                    var fields = string.Join("; ", did.Fields.Select(field =>
                        $"{field.Name} [{(field.BitOffset < 0 ? "position not verified" : $"bit {field.BitOffset.ToString(CultureInfo.InvariantCulture)}")}, " +
                        $"{field.BitLength.ToString(CultureInfo.InvariantCulture)} bit]"));
                    definitions.Add([catalog.Ecu, file, service.ServiceId == 0x22 ? "DID read" : "DID write",
                        $"0x{did.Did:X4}", did.DisplayName, layout, fields,
                        Conditions(service.ServiceId, did.Did, DiagnosticConditionKind.Session),
                        Conditions(service.ServiceId, did.Did, DiagnosticConditionKind.Security),
                        Conditions(service.ServiceId, did.Did, DiagnosticConditionKind.Other),
                        Status(service.ServiceId, did.Did)]);
                }
            }

            foreach (var routine in catalog.RoutineDefinitions.OrderBy(item => item.Identifier))
            {
                var inputs = string.Join("; ", routine.InputFields.Select(field => field.Name));
                var outputs = string.Join("; ", routine.OutputFields.Select(field => field.Name));
                definitions.Add([catalog.Ecu, file, "Routine", $"0x{routine.Identifier:X4}", routine.DisplayName,
                    $"31 <01 start|02 stop|03 results> {routine.Identifier >> 8:X2} {routine.Identifier & 0xFF:X2} <input record>  →  71 …",
                    (inputs.Length > 0 ? "Input: " + inputs : string.Empty) +
                    (outputs.Length > 0 ? (inputs.Length > 0 ? " | " : string.Empty) + "Output: " + outputs : string.Empty),
                    Conditions(0x31, routine.Identifier, DiagnosticConditionKind.Session),
                    Conditions(0x31, routine.Identifier, DiagnosticConditionKind.Security),
                    Conditions(0x31, routine.Identifier, DiagnosticConditionKind.Other),
                    Status(0x31, routine.Identifier)]);
            }
        }

        return
        [
            new ExportSheet("Definitions",
                ["ECU", "Source file", "Type", "Identifier", "Name", "Request / response layout", "Fields",
                 "Declared session", "Declared security", "Other declared conditions", "Condition status"],
                definitions),
            new ExportSheet("Declared conditions",
                ["ECU", "Source file", "Service ID", "Service", "Identifier", "Kind", "State chart", "State"],
                conditions),
            new ExportSheet("About",
                ["Topic", "Statement"],
                [
                    ["Scope", "Conditions are the PRE-CONDITION-STATE-REF states declared on ODX/PDX services (ISO 22901). Each state is classified by the semantic of its state chart."],
                    ["Meaning of 'none'", "An empty condition column only says the file declares no condition. It does not prove the service can be used in any session or security state."],
                    ["CDD files", "Pre-condition data is not extracted from CDD files by this version; the status column says so explicitly."],
                    ["Negative responses", "Declared negative-response codes are not extracted. Recorded negative responses are reported by the Spec vs trace view from the actual traffic."],
                    ["Layouts", "Request layouts are the standard UDS frame for the identifier; response fields show only positions that the source verifies (otherwise 'position not verified')."],
                    ["Verification", "Declared prerequisites are compared with the session/security state reconstructed from positive 0x10/0x11/0x27 responses in a trace (Spec vs trace)."]
                ])
        ];
    }
}
