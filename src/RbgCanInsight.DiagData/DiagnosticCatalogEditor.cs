using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RbgCanInsight.DiagData;

/// <summary>Replace the display name and/or description of one diagnostic definition (DID).</summary>
public sealed record DiagnosticTextEdit(
    int Did,
    string CurrentLongName,
    string CurrentDescription,
    string NewLongName,
    string NewDescription);

public sealed record DiagnosticEditResult(
    bool Saved,
    string DestinationPath,
    IReadOnlyList<string> Issues);

/// <summary>
/// Safe editor for CDD/ODX descriptions: changes only the human-readable name and description of
/// definitions that can be located unambiguously, keeps the rest of the XML tree (including unknown
/// vendor elements) intact, writes ONLY to a new file and validates the result by reloading it with
/// the normal catalog readers and diffing it against the original: nothing except the intended
/// definitions may differ. Packages (PDX), files with internal DTD entities and ambiguous matches
/// are refused rather than edited blindly.
/// </summary>
public static class DiagnosticCatalogEditor
{
    private const long MaximumBytes = 64L * 1024 * 1024;

    public static bool CanEdit(string path, out string reason)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".cdd" or ".odx"))
        {
            reason = extension == ".pdx"
                ? "PDX packages cannot be edited safely; edit the contained ODX file instead."
                : "Only .cdd and .odx files can be edited.";
            return false;
        }
        if (!File.Exists(path)) { reason = "The source file does not exist."; return false; }
        if (new FileInfo(path).Length > MaximumBytes) { reason = "The file exceeds the verified 64 MiB edit limit."; return false; }
        try
        {
            var (document, _) = Load(path);
            if (document.DocumentType?.InternalSubset is { } subset &&
                subset.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
            {
                reason = "The file declares XML entities in its DOCTYPE; editing could change their meaning.";
                return false;
            }
        }
        catch (XmlException exception)
        {
            reason = "The file is not well-formed XML: " + exception.Message;
            return false;
        }
        reason = string.Empty;
        return true;
    }

    public static DiagnosticEditResult SaveCopy(
        string sourcePath, string destinationPath, IReadOnlyList<DiagnosticTextEdit> edits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(edits);
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Save the edited copy under a new filename; the original file is read-only.");
        if (!string.Equals(Path.GetExtension(source), Path.GetExtension(destination), StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("The edited copy must keep the source file extension.");
        if (File.Exists(destination))
            throw new IOException("The destination already exists. Select an unused filename to avoid overwriting.");
        if (!CanEdit(source, out var reason)) throw new NotSupportedException(reason);
        if (edits.Count == 0) throw new ArgumentException("No edit was provided.", nameof(edits));

        var original = DidCatalog.LoadAll(source);
        var (document, settings) = Load(source);
        var isOdx = string.Equals(Path.GetExtension(source), ".odx", StringComparison.OrdinalIgnoreCase);
        var issues = new List<string>();
        foreach (var edit in edits)
        {
            var matches = (isOdx ? OdxServices(document) : CddServices(document))
                .Where(service => Same(service.LongName, edit.CurrentLongName) &&
                                  Same(service.Description, edit.CurrentDescription) &&
                                  service.Did == edit.Did)
                .ToArray();
            if (matches.Length != 1)
            {
                issues.Add(matches.Length == 0
                    ? $"DID 0x{edit.Did:X4}: no definition with that identifier, name and description was found."
                    : $"DID 0x{edit.Did:X4}: {matches.Length} identical definitions match; editing would be ambiguous.");
                continue;
            }
            if (!matches[0].Apply(edit.NewLongName, edit.NewDescription, out var problem))
                issues.Add($"DID 0x{edit.Did:X4}: {problem}");
        }
        if (issues.Count > 0)
            throw new InvalidDataException("The edit was not applied: " + string.Join(" | ", issues));

        // Validate in a private folder under the SAME file name (an ODX/CDD without a declared ECU
        // derives its ECU identity from the file name, which the comparison keys on).
        var validationFolder = Path.Combine(Path.GetTempPath(), "rbg-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(validationFolder);
        try
        {
            var candidate = Path.Combine(validationFolder, Path.GetFileName(source));
            Write(document, candidate, settings);
            var problems = Validate(original, DidCatalog.LoadAll(candidate), edits);
            if (problems.Count > 0)
                throw new InvalidDataException("The edited copy failed validation and was not written: " +
                                               string.Join(" | ", problems));
            var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(candidate, temporary);
                File.Move(temporary, destination);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { /* sources stay untouched */ }
            }
        }
        finally
        {
            try { Directory.Delete(validationFolder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new DiagnosticEditResult(true, destination, []);
    }

    // ------------------------------------------------------------------ XML

    private sealed record WriterSettings(Encoding Encoding, bool OmitDeclaration, string NewLine);

    private static (XDocument Document, WriterSettings Settings) Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.UTF8.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        var readerSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Parse,      // keep the DOCTYPE node ...
            XmlResolver = null,                        // ... but never fetch an external DTD
            IgnoreWhitespace = false
        };
        using var reader = XmlReader.Create(new StringReader(text), readerSettings);
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (document.DocumentType is { InternalSubset: { Length: 0 } } doctype)
        {
            // The writer would add an empty internal subset "[]"; keep the declaration as it was.
            doctype.ReplaceWith(new XDocumentType(doctype.Name, doctype.PublicId, doctype.SystemId, null));
        }
        var newLine = text.Contains("\r\n") ? "\r\n" : "\n";
        return (document, new WriterSettings(new UTF8Encoding(hasBom), document.Declaration is null, newLine));
    }

    private static void Write(XDocument document, string path, WriterSettings settings)
    {
        var writerSettings = new XmlWriterSettings
        {
            Encoding = settings.Encoding,
            OmitXmlDeclaration = settings.OmitDeclaration,
            Indent = false,
            NewLineHandling = NewLineHandling.None,
            NewLineChars = settings.NewLine
        };
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = XmlWriter.Create(stream, writerSettings);
        document.Save(writer);
    }

    private static bool Same(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);

    private abstract record EditableService(int Did, string LongName, string Description)
    {
        public abstract bool Apply(string newLongName, string newDescription, out string problem);
    }

    // ------------------------------------------------------------------ CDD

    private static IEnumerable<EditableService> CddServices(XDocument document)
    {
        foreach (var service in document.Descendants().Where(e => e.Name.LocalName == "SERVICE"))
        {
            var did = CddDid(service);
            if (did is null) continue;
            yield return new CddService(service, did.Value,
                CddText(service, "NAME") ?? string.Empty, CddText(service, "DESC") ?? string.Empty);
        }
    }

    private static int? CddDid(XElement service)
    {
        var value = service.Descendants().FirstOrDefault(e => e.Name.LocalName == "STATICVALUE") ??
                    (service.ElementsAfterSelf().FirstOrDefault() is { } next && next.Name.LocalName == "STATICVALUE" ? next : null);
        var text = (string?)value?.Attribute("v");
        if (text is null) return null;
        var parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var hex) ? hex : (int?)null
            : int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var dec) ? dec : null;
        return parsed;
    }

    private static string? CddText(XElement service, string name)
    {
        var child = service.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        if (child is null) return null;
        var tuv = child.Descendants().FirstOrDefault(e => e.Name.LocalName == "TUV");
        return (tuv?.Value ?? child.Value).Trim();
    }

    private sealed record CddService(XElement Element, int DidValue, string Name, string Desc)
        : EditableService(DidValue, Name, Desc)
    {
        public override bool Apply(string newLongName, string newDescription, out string problem)
        {
            problem = string.Empty;
            return Set("NAME", newLongName, Name, ref problem) && Set("DESC", newDescription, Desc, ref problem);
        }

        private bool Set(string elementName, string text, string current, ref string problem)
        {
            if (Same(text, current)) return true;
            var child = Element.Elements().FirstOrDefault(e => e.Name.LocalName == elementName);
            if (child is null)
            {
                problem = $"the service has no {elementName} element; creating one is not supported because the CDD element order is not verified.";
                return false;
            }
            var tuv = child.Descendants().FirstOrDefault(e => e.Name.LocalName == "TUV");
            (tuv ?? child).Value = text;           // attributes such as xml:lang stay as they were
            return true;
        }
    }

    // ------------------------------------------------------------------ ODX

    private static IEnumerable<EditableService> OdxServices(XDocument document)
    {
        var byId = document.Descendants().Where(e => e.Attribute("ID") is not null)
            .GroupBy(e => (string)e.Attribute("ID")!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var service in document.Descendants().Where(e => e.Name.LocalName == "DIAG-SERVICE"))
        {
            var requestRef = service.Elements().FirstOrDefault(e => e.Name.LocalName == "REQUEST-REF")?.Attribute("ID-REF")?.Value;
            if (requestRef is null || !byId.TryGetValue(requestRef, out var request)) continue;
            // Mirror the catalog reader: the DID is the 16-bit coded constant of the request.
            var coded = request.Descendants().Where(e => e.Name.LocalName == "PARAM")
                .Select(param => new
                {
                    Value = ParseUnsigned(param.Elements().FirstOrDefault(e => e.Name.LocalName == "CODED-VALUE")?.Value),
                    Bits = int.TryParse(param.Descendants().FirstOrDefault(e => e.Name.LocalName == "BIT-LENGTH")?.Value,
                        out var bits) ? bits : 0
                })
                .FirstOrDefault(item => item.Value is not null && item.Bits >= 16);
            if (coded?.Value is not { } did || did > 0xFFFF) continue;
            var longName = service.Elements().FirstOrDefault(e => e.Name.LocalName == "LONG-NAME")?.Value.Trim() ?? string.Empty;
            var description = service.Descendants().FirstOrDefault(e => e.Name.LocalName == "DESC")?.Value.Trim() ?? string.Empty;
            yield return new OdxService(service, (int)did, longName, description);
        }
    }

    private static ulong? ParseUnsigned(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var hex) ? hex : null;
        return ulong.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var dec) ? dec : null;
    }

    private sealed record OdxService(XElement Element, int DidValue, string Name, string Desc)
        : EditableService(DidValue, Name, Desc)
    {
        public override bool Apply(string newLongName, string newDescription, out string problem)
        {
            problem = string.Empty;
            var ns = Element.Name.Namespace;
            if (!Same(newLongName, Name))
            {
                var longName = Element.Elements().FirstOrDefault(e => e.Name.LocalName == "LONG-NAME");
                if (longName is null)
                {
                    var shortName = Element.Elements().FirstOrDefault(e => e.Name.LocalName == "SHORT-NAME");
                    if (shortName is null)
                    {
                        problem = "the service has no SHORT-NAME to anchor a new LONG-NAME element.";
                        return false;
                    }
                    shortName.AddAfterSelf(new XElement(ns + "LONG-NAME", newLongName));    // ODX order: SHORT-NAME, LONG-NAME, DESC
                }
                else longName.Value = newLongName;
            }
            if (!Same(newDescription, Desc))
            {
                var desc = Element.Elements().FirstOrDefault(e => e.Name.LocalName == "DESC");
                if (desc is null)
                {
                    var anchor = Element.Elements().FirstOrDefault(e => e.Name.LocalName == "LONG-NAME") ??
                                 Element.Elements().FirstOrDefault(e => e.Name.LocalName == "SHORT-NAME");
                    if (anchor is null)
                    {
                        problem = "the service has no SHORT-NAME to anchor a new DESC element.";
                        return false;
                    }
                    anchor.AddAfterSelf(new XElement(ns + "DESC", new XElement(ns + "p", newDescription)));
                }
                else
                {
                    var paragraph = desc.Descendants().FirstOrDefault(e => e.Name.LocalName == "p");
                    (paragraph ?? desc).Value = newDescription;
                }
            }
            return true;
        }
    }

    // ------------------------------------------------------------------ validation

    private static List<string> Validate(
        IReadOnlyList<DidCatalog> before, IReadOnlyList<DidCatalog> after, IReadOnlyList<DiagnosticTextEdit> edits)
    {
        var problems = new List<string>();
        var allowed = edits.Select(edit => $"0x{edit.Did:X4}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in DiagnosticDefinitionComparer.Compare(before, after).Rows)
        {
            if (row.Change == DiagnosticDefinitionChange.Unchanged) continue;
            var fields = row.ChangedFields.Split([',', ';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var textOnly = fields.Length > 0 && fields.All(field =>
                field.Equals("Long name", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("Description", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("Name", StringComparison.OrdinalIgnoreCase));
            // The DID row and the service row that carries the same identifier mirror the edited text.
            var ownRow = row.Scope == "DID" && allowed.Contains(row.Identifier) ||
                         row.Scope == "Service" && allowed.Any(did => row.Identifier.EndsWith("/" + did, StringComparison.OrdinalIgnoreCase));
            if (row.Change != DiagnosticDefinitionChange.Changed || !ownRow || !textOnly)
                problems.Add($"Unexpected {row.Change} difference: {row.Scope} {row.Identifier} {row.Path} ({row.ChangedFields})".TrimEnd());
        }
        foreach (var edit in edits)
        {
            var found = after.SelectMany(catalog => catalog.Parameters).Where(p => p.Did == edit.Did).ToArray();
            if (!found.Any(parameter => Same(parameter.LongName, edit.NewLongName) && Same(parameter.Description, edit.NewDescription)))
                problems.Add($"DID 0x{edit.Did:X4} does not read back with the new name and description.");
        }
        return problems;
    }
}
