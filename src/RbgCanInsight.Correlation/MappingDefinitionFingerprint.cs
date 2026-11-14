using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;

namespace RbgCanInsight.Correlation;

/// <summary>
/// Portable, deterministic SHA-256 fingerprints of the subset of DBC/CDD/ODX
/// definitions understood by this application. They are NOT source-file hashes,
/// attestations of source authenticity or OEM conformance certificates. A changed
/// fingerprint invalidates prior reviews; an equal fingerprint cannot certify that
/// unknown vendor metadata is unchanged.
/// </summary>
public static class MappingDefinitionFingerprint
{
    private const string Version = "mapping-semantics-v1";

    public static string ForDid(DidParameter did)
    {
        ArgumentNullException.ThrowIfNull(did);
        var b = new StringBuilder();
        Add(b, Version);
        Add(b, "DID");
        Add(b, did.Did);
        Add(b, did.Ecu);
        Add(b, PortableFileName(did.SourceFile));
        Add(b, did.Name);
        Add(b, did.LongName);
        Add(b, did.Description);
        Add(b, did.Unit);
        Add(b, did.Minimum);
        Add(b, did.Maximum);
        Add(b, did.Factor);
        Add(b, did.Offset);
        Add(b, did.DataType);
        Add(b, did.BitLength);
        Add(b, did.Fields.Count);
        foreach (var field in did.Fields)
        {
            Add(b, field.Name);
            Add(b, field.LongName);
            Add(b, field.Description);
            Add(b, field.BitOffset);
            Add(b, field.BitLength);
            Add(b, field.DataType);
            Add(b, field.Unit);
            Add(b, field.Minimum);
            Add(b, field.Maximum);
            Add(b, field.Factor);
            Add(b, field.Offset);
            Add(b, field.IsSigned);
            Add(b, field.ByteOrder);
            Add(b, field.SupportsPhysicalConversion);
            if (field.Odx is not null) Add(b, field.Odx.Signature); // CDD fields keep their former fingerprint
            Add(b, field.EnumValues.Count);
            foreach (var meaning in field.EnumValues)
            {
                Add(b, meaning.Value);
                Add(b, meaning.Text);
                Add(b, meaning.Description);
            }
        }
        Add(b, did.EngineeringIssues.Count);
        foreach (var issue in did.EngineeringIssues) Add(b, issue);
        Add(b, did.EngineeringParameters.Count);
        foreach (var p in did.EngineeringParameters)
        {
            Add(b, p.Name);
            Add(b, p.LongName);
            Add(b, p.Description);
            Add(b, p.Path);
            Add(b, p.ParentPath);
            Add(b, p.Depth);
            Add(b, p.Kind);
            Add(b, p.SourceId);
            Add(b, p.TypeReference);
            Add(b, p.DataType);
            Add(b, p.Unit);
            Add(b, p.DeclaredBitLength);
            Add(b, p.ReferenceStatus);
            Add(b, p.DefinitionOrigin);
            Add(b, p.DeclaredParCode);
            Add(b, p.DeclaredBytePosition);
            Add(b, p.DeclaredBitPosition);
            Add(b, p.DeclaredBitOffset);
            Add(b, p.DeclaredValues.Count);
            foreach (var value in p.DeclaredValues)
            {
                Add(b, value.LowerRaw);
                Add(b, value.UpperRaw);
                Add(b, value.Meaning);
                Add(b, value.Description);
                Add(b, value.Status);
            }
        }
        return Digest(b);
    }

    public static string ForSignal(string database, DbcMessage message, DbcSignal signal)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(signal);
        var b = new StringBuilder();
        Add(b, Version);
        Add(b, "CAN");
        Add(b, database);
        Add(b, message.Identifier);
        Add(b, message.IsExtended);
        Add(b, message.Name);
        Add(b, message.Length);
        Add(b, message.Transmitter);
        Add(b, message.Comment);
        Add(b, message.NominalCycleTimeMilliseconds);
        Add(b, signal.Name);
        Add(b, signal.StartBit);
        Add(b, signal.BitLength);
        Add(b, signal.ByteOrder);
        Add(b, signal.IsSigned);
        Add(b, signal.ValueType);
        Add(b, signal.Factor);
        Add(b, signal.Offset);
        Add(b, signal.Minimum);
        Add(b, signal.Maximum);
        Add(b, signal.Unit);
        Add(b, signal.Receiver);
        Add(b, signal.IsMultiplexor);
        Add(b, signal.MultiplexValue);
        Add(b, signal.Comment);
        Add(b, signal.MultiplexConditions?.Count ?? 0);
        if (signal.MultiplexConditions is not null)
            foreach (var condition in signal.MultiplexConditions)
            {
                Add(b, condition.Multiplexor);
                Add(b, condition.Ranges.Count);
                foreach (var range in condition.Ranges)
                {
                    Add(b, range.Minimum);
                    Add(b, range.Maximum);
                }
            }
        Add(b, signal.ValueDescriptions?.Count ?? 0);
        if (signal.ValueDescriptions is not null)
            foreach (var value in signal.ValueDescriptions.OrderBy(item => item.Key))
            {
                Add(b, value.Key);
                Add(b, value.Value);
            }
        AddAttributes(b, message.SourceAttributes);
        AddAttributes(b, signal.SourceAttributes);
        return Digest(b);
    }

    private static void AddAttributes(StringBuilder b, IReadOnlyList<DbcSourceAttribute> attributes)
    {
        Add(b, attributes.Count);
        foreach (var attribute in attributes.OrderBy(item => item.Name, StringComparer.Ordinal)
                     .ThenBy(item => item.RawValue, StringComparer.Ordinal))
        {
            Add(b, attribute.Name);
            Add(b, attribute.RawValue);
        }
    }

    private static string PortableFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Replace('\\', '/');
        return normalized[(normalized.LastIndexOf('/') + 1)..];
    }

    private static string Digest(StringBuilder b) =>
        "sem-v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(b.ToString())));

    // Length-prefixed tokens prevent ambiguous concatenations; invariant number
    // formatting avoids locale-dependent changes to saved fingerprints.
    private static void Add(StringBuilder b, object? value)
    {
        string? text = value switch
        {
            null => null,
            bool flag => flag ? "true" : "false",
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
        if (text is null) b.Append("-1:");
        else b.Append(text.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':').Append(text);
    }
}
