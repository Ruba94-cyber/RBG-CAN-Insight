using System.Globalization;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;

namespace RbgCanInsight.TraceVerify;

/// <summary>
/// Audit-only CSV stream. One row is one actually decoded signal on an
/// original CAN frame. Never derives values from pixel-decimated charts.
/// </summary>
public sealed class DbcSignalEvidenceDump
{
    public const string CsvHeader =
        "frame_index,timestamp_ns,channel,can_id,extended,dbc_sha256,dbc_name," +
        "message,signal,start_bit,bit_length,byte_order,value_type,raw_bits_hex," +
        "raw_coded,raw_exact_in_double,physical,unit,named_value";

    private readonly DbcDecoder _decoder;
    private readonly string _databaseSha256;
    private readonly Dictionary<DbcMessage, Dictionary<string, DbcSignal>> _byMessage =
        new(ReferenceEqualityComparer.Instance);

    public DbcSignalEvidenceDump(DbcDecoder decoder, string databaseSha256)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        if (databaseSha256.Length != 64 ||
            databaseSha256.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Expected SHA-256 hexadecimal DBC identity.",
                nameof(databaseSha256));
        _databaseSha256 = databaseSha256.ToUpperInvariant();

        foreach (var message in _decoder.Database.AllMessageDefinitions)
        {
            // A same-name duplicate must not be arbitrarily selected as the
            // source of a decoded signal: decoder already suppresses it.
            var unique = message.Signals
                .GroupBy(signal => signal.Name, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(),
                    StringComparer.Ordinal);
            _byMessage.TryAdd(message, unique);
        }
    }

    public long WriteFrame(TextWriter writer, long frameIndex, in CanFrame frame)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (frameIndex < 0) throw new ArgumentOutOfRangeException(nameof(frameIndex));
        if (frame.IsError || frame.Flags.HasFlag(CanFrameFlags.Remote) ||
            !_decoder.TryGetMessage(frame, out var message) ||
            !_byMessage.TryGetValue(message, out var signals))
            return 0;

        var decoded = _decoder.Decode(frame);
        if (decoded.Count == 0) return 0;
        Span<byte> payload = stackalloc byte[64];
        frame.CopyPayloadTo(payload);
        var count = 0L;

        // Stable ordering within the same frame makes independent reference
        // comparisons reproducible without globally sorting million-frame data.
        foreach (var value in decoded.OrderBy(item => item.Signal, StringComparer.Ordinal))
        {
            if (!signals.TryGetValue(value.Signal, out var signal)) continue;
            var exact = DbcSignalInspector.InspectRecordedRaw(
                signal, payload[..frame.DataLength]);
            if (exact is null) continue;

            var row = new[]
            {
                frameIndex.ToString(CultureInfo.InvariantCulture),
                frame.TimestampNanoseconds.ToString(CultureInfo.InvariantCulture),
                frame.Channel.ToString(CultureInfo.InvariantCulture),
                frame.Identifier.ToString(CultureInfo.InvariantCulture),
                frame.IsExtended ? "1" : "0",
                _databaseSha256,
                Csv(_decoder.Database.Name),
                Csv(message.Name),
                Csv(signal.Name),
                signal.StartBit.ToString(CultureInfo.InvariantCulture),
                signal.BitLength.ToString(CultureInfo.InvariantCulture),
                signal.ByteOrder.ToString(),
                signal.ValueType.ToString(),
                exact.RawHex,
                Csv(exact.RawText),
                exact.IsExactInDouble ? "1" : "0",
                value.PhysicalValue.ToString("G17", CultureInfo.InvariantCulture),
                Csv(signal.Unit),
                Csv(exact.DeclaredMeaning ?? "")
            };
            writer.WriteLine(string.Join(",", row));
            count++;
        }
        return count;
    }

    // Untrusted DBC text must not become a spreadsheet formula if someone
    // opens the local audit CSV in Excel; preserve RFC 4180 quoting.
    private static string Csv(string value)
    {
        var meaningful = value.AsSpan().TrimStart();
        if (meaningful.Length > 0 &&
            meaningful[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
