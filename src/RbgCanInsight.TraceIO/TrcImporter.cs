using System.Globalization;
using System.Runtime.CompilerServices;
using RbgCanInsight.Domain;

namespace RbgCanInsight.TraceIO;

/// <summary>
/// PCAN-View .trc trace reader. Supports the common 1.x layout
/// ("1)  123.4  Rx  0100  8  01 02 ...") and the 2.x layout
/// ("1  123.456 DT 0100 Rx 8 01 02 ..."), tolerating minor column variations.
/// </summary>
public static class TrcImporter
{
    public static async IAsyncEnumerable<CanFrame> ReadAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        TraceImportHealth? diagnostics = null)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1 << 20, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, bufferSize: 1 << 20);
        long sequence = 0;
        long lineNumber = 0;
        string[]? columns = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            diagnostics?.RecordTextLine();
            if (string.IsNullOrWhiteSpace(line) ||
                line.AsSpan().TrimStart().StartsWith(";", StringComparison.Ordinal))
            {
                // PCAN-View 2.x declares its exact column order: ";$COLUMNS=N,O,T,B,I,d,R,L,D".
                var meta = line.Trim();
                if (meta.StartsWith(";$COLUMNS=", StringComparison.OrdinalIgnoreCase))
                    columns = meta[10..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                diagnostics?.RecordMetadataLine();
                continue;
            }

            CanFrame? parsed;
            // Only the standard PEAK layout (offset + length columns) is parsed by column;
            // any other header keeps the tolerant heuristic parser.
            try { parsed = columns is null || !columns.Contains("O") || !columns.Contains("L") ? Parse(line) : ParseWithColumns(line, columns); }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                parsed = null;
            }

            if (parsed is { } frame)
                yield return frame with { Sequence = ++sequence };
            else
                diagnostics?.RecordRejectedLine(
                    "Unrecognized, invalid DLC or incomplete PCAN-TRC data record.", lineNumber);
        }
    }

    /// <summary>
    /// Column-driven PCAN-TRC 2.x record. Honors the declared column order
    /// (including the bus column), takes the bus as the channel, treats 8-digit
    /// identifiers as 29-bit and skips status/error-counter rows without an ID.
    /// </summary>
    internal static CanFrame? ParseWithColumns(string line, string[] columns)
    {
        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var cursor = 0;
        string? type = null, idText = null;
        var transmitted = false;
        int bus = 0, length = -1;
        double milliseconds = 0;
        var seenNumber = false;
        foreach (var column in columns)
        {
            if (cursor >= tokens.Length)
            {
                // Remote frames legitimately have no data column content.
                if (column == "D") break;
                return null;
            }
            var token = tokens[cursor];
            switch (column)
            {
                case "N":
                    if (!long.TryParse(token.TrimEnd(')'), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                        return null;
                    seenNumber = true;
                    break;
                case "O":
                    if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out milliseconds))
                        return null;
                    break;
                case "T": type = token; break;
                case "B":
                    if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out bus)) return null;
                    break;
                case "I": idText = token; break;
                case "d": transmitted = token.Equals("Tx", StringComparison.OrdinalIgnoreCase); break;
                case "R": break;
                case "L":
                    if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out length)) return null;
                    break;
                case "D":
                    // Data bytes are the remaining tokens; handled below.
                    cursor--;
                    break;
                default: break; // unknown column: consume one token, never guess meaning
            }
            cursor++;
            if (column == "D") break;
        }

        if (!seenNumber || type is null) return null;
        // Status (ST), error (ER) and error-counter (EC) rows carry no CAN identifier.
        if (type is "ST" or "ER" or "EC" || idText is null || idText == "-") return null;
        if (!uint.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
            return null;
        var isRemote = type == "RR";
        var isFd = type is "FD" or "FB" or "FE" or "BI";
        var isBrs = type is "FB" or "BI";
        if (length is < 0 or > 64) return null;
        var byteCount = isFd ? DlcToLength(length) : length;
        var dataTokens = tokens.Length - cursor;
        if (!isRemote && dataTokens < byteCount) return null;
        var data = new byte[isRemote ? 0 : byteCount];
        for (var i = 0; i < data.Length; i++)
            if (!byte.TryParse(tokens[cursor + i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out data[i]))
                return null;

        var flags = isRemote ? CanFrameFlags.Remote : CanFrameFlags.None;
        // PEAK writes 8 hex digits for 29-bit identifiers and 4 for 11-bit ones.
        if (idText.Length > 4 || id > 0x7FF) flags |= CanFrameFlags.Extended;
        if (isFd) flags |= CanFrameFlags.FlexibleDataRate;
        if (isBrs) flags |= CanFrameFlags.BitRateSwitch;
        if (transmitted) flags |= CanFrameFlags.Transmit;
        if (isRemote && length > 8) return null;
        return new CanFrame(Math.Max(bus, 0), id, data,
            checked((long)Math.Round(milliseconds * 1_000_000d)), flags,
            declaredDataLength: isRemote ? (byte)length : null);
    }

    private static int DlcToLength(int dlc) => dlc switch
    {
        <= 8 => dlc, 9 => 12, 10 => 16, 11 => 20, 12 => 24, 13 => 32, 14 => 48, 15 => 64, _ => dlc
    };

    internal static CanFrame? Parse(string line)
    {
        var span = line.AsSpan().Trim();
        if (span.IsEmpty || span[0] == ';')
        {
            return null;
        }

        Span<Range> cells = stackalloc Range[80];
        var count = TraceImporter.Tokenize(span, cells);
        if (count < 4)
        {
            return null;
        }

        // Column 0 is the message number: "1)" in 1.x, "1" in 2.x.
        var first = span[cells[0]].TrimEnd(')');
        if (!long.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        if (!double.TryParse(span[cells[1]], NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
        {
            return null;
        }

        // Walk the remaining columns: skip direction/type mnemonics, take the first hex token as
        // the identifier, then the first small integer as the DLC, then the data bytes.
        uint? identifier = null;
        var dataLength = -1;
        var dataStart = -1;
        var isFd = false;
        var isBrs = false;
        var isRemote = false;
        var isTransmit = false;
        for (var index = 2; index < count; index++)
        {
            var token = span[cells[index]];
            if (identifier is null)
            {
                if (IsMnemonic(token))
                {
                    isFd |= token is "FD" || token is "FB" || token is "FE" || token is "BI";
                    isBrs |= token is "FB" || token is "BI";
                    isRemote |= token is "RR";
                    isTransmit |= token is "Tx";
                    continue;
                }

                // Error frames and bus state lines have no parsable ID; skip the whole line.
                if (uint.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
                {
                    identifier = id;
                    continue;
                }

                return null;
            }

            if (dataLength < 0)
            {
                if (IsMnemonic(token))
                {
                    isRemote |= token is "RR";
                    isTransmit |= token is "Tx";
                    continue;
                }

                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
                {
                    if (length is < 0 or > 64) return null;
                    dataLength = length;
                    dataStart = index + 1;
                    break;
                }

                return null;
            }
        }

        if (identifier is null || dataLength < 0 || dataStart < 0)
        {
            return null;
        }

        if (isRemote && dataLength > 8) return null;
        if (!isRemote && count - dataStart < dataLength)
            return null; // A short capture row must not become a full CAN payload.
        var data = new byte[isRemote ? 0 : dataLength];
        for (var index = 0; index < data.Length; index++)
        {
            if (!byte.TryParse(span[cells[dataStart + index]],
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out data[index]))
                return null;
        }

        var flags = isRemote ? CanFrameFlags.Remote : CanFrameFlags.None;
        if (isTransmit) flags |= CanFrameFlags.Transmit;
        if (identifier > 0x7FF)
        {
            flags |= CanFrameFlags.Extended;
        }

        if (isFd)
        {
            flags |= CanFrameFlags.FlexibleDataRate;
        }

        if (isBrs)
        {
            flags |= CanFrameFlags.BitRateSwitch;
        }

        return new CanFrame(
            0,
            identifier.Value,
            data,
            checked((long)Math.Round(milliseconds * 1_000_000d)),
            flags,
            declaredDataLength: isRemote ? (byte)dataLength : null);
    }

    private static bool IsMnemonic(ReadOnlySpan<char> token) =>
        token is "Rx" or "Tx" or "DT" or "FD" or "FB" or "FE" or "BI" or "RR" or "ST" or "ER" or "EC";
}
