using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using RbgCanInsight.Domain;

namespace RbgCanInsight.TraceIO;

/// <summary>
/// Entry point for offline trace import. Dispatches on the file extension to the matching
/// format reader and yields frames as an async stream so multi-gigabyte traces never need
/// to fit in memory during parsing.
/// </summary>
public static partial class TraceImporter
{
    public static readonly IReadOnlyList<string> SupportedExtensions =
        [".asc", ".blf", ".trc", ".csv", ".log", ".txt", ".mf4", ".mdf", ".pcap", ".pcapng"];

    public static string FileDialogFilter =>
        "CAN traces (*.asc;*.blf;*.trc;*.csv;*.log;*.txt;*.mf4;*.mdf;*.pcap;*.pcapng)|*.asc;*.blf;*.trc;*.csv;*.log;*.txt;*.mf4;*.mdf;*.pcap;*.pcapng|" +
        "Vector ASCII (*.asc)|*.asc|Vector binary log (*.blf)|*.blf|PCAN trace (*.trc)|*.trc|" +
        "SocketCAN capture (*.pcap;*.pcapng)|*.pcap;*.pcapng|CSV (*.csv)|*.csv|candump log (*.log;*.txt)|*.log;*.txt|ASAM MDF4 (*.mf4;*.mdf)|*.mf4;*.mdf|All files (*.*)|*.*";

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static IAsyncEnumerable<CanFrame> ReadAsync(
        string path,
        CancellationToken cancellationToken = default,
        TraceImportHealth? diagnostics = null) =>
        ReadTrackedAsync(path, cancellationToken, diagnostics);

    private static async IAsyncEnumerable<CanFrame> ReadTrackedAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        TraceImportHealth? diagnostics)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        diagnostics?.Start(extension);
        IAsyncEnumerable<CanFrame> source = extension switch
        {
            ".asc" => ReadTextAsync(path, ParseAsc, extension, diagnostics, cancellationToken),
            ".log" or ".txt" => ReadTextAsync(path, ParseCandump, extension, diagnostics, cancellationToken),
            ".csv" => ReadTextAsync(path, ParseCsv, extension, diagnostics, cancellationToken),
            ".trc" => TrcImporter.ReadAsync(path, cancellationToken, diagnostics),
            ".blf" => BlfImporter.ReadAsync(path, cancellationToken, diagnostics),
            ".mf4" or ".mdf" => Mdf4Importer.ReadAsync(path, cancellationToken, diagnostics),
            ".pcap" or ".pcapng" => PcapngCanImporter.ReadAsync(path, cancellationToken, diagnostics),
            _ => throw new NotSupportedException(
                $"Unsupported trace format '{extension}'. Supported: {string.Join(", ", SupportedExtensions)}.")
        };

        // Explicit MoveNextAsync handling lets us record failures before
        // rethrowing. C# forbids yield return in a try block with catch.
        await using var reader = source.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            CanFrame frame = default;
            bool found;
            try
            {
                found = await reader.MoveNextAsync();
                if (found)
                    frame = reader.Current;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics?.Fail(exception);
                throw; // Never label an interrupted partial import as successful.
            }

            if (!found)
            {
                diagnostics?.Complete();
                yield break;
            }

            diagnostics?.RecordFrame(frame);
            yield return frame;
        }
    }

    private static async IAsyncEnumerable<CanFrame> ReadTextAsync(
        string path,
        Func<string, CanFrame?> parser,
        string extension,
        TraceImportHealth? diagnostics,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1 << 20, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, bufferSize: 1 << 20);
        long sequence = 0;
        long lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            diagnostics?.RecordTextLine();
            if (IsTextMetadata(extension, line, lineNumber))
            {
                diagnostics?.RecordMetadataLine();
                if (diagnostics is not null && extension == ".asc")
                    RecordAscLoggedMetadata(line, diagnostics);
                continue;
            }

            if (extension is ".log" or ".txt" && CanXlWire.LooksLikeCandumpXl(line))
            {
                var xl = CanXlWire.ParseCandumpLine(line, out var xlError);
                if (xl is not null) diagnostics?.BusFrames.Add(xl);
                else diagnostics?.RecordRejectedLine(xlError!, lineNumber);
                continue;
            }

            CanFrame? parsed;
            try
            {
                parsed = parser(line);
            }
            catch (Exception exception) when (
                exception is FormatException or OverflowException or ArgumentException)
            {
                // Continue recovering independent source lines while
                // recording loss of data. Never export the raw line.
                diagnostics?.RecordRejectedLine(
                    "Malformed numeric field, DLC, CAN identifier or CAN payload.", lineNumber);
                continue;
            }

            if (parsed is { } frame)
                yield return frame with { Sequence = ++sequence };
            else if (extension == ".asc" && diagnostics is not null && AscLinLines.TryParse(line) is { } linLine)
                diagnostics.BusFrames.Add(linLine);
            else if (extension == ".asc" && AscOtherBusLine(line) is { } otherBus)
                diagnostics?.RecordUnsupportedSourceObject("UNSUPPORTED_ASC_" + otherBus.ToUpperInvariant(),
                    $"ASC {otherBus} record(s) were recognised and counted but not decoded: the Vector ASC line layout " +
                    "for this bus is not established from a public specification. Use BLF for LIN/FlexRay.");
            else
                diagnostics?.RecordRejectedLine(
                    "Unrecognized or incomplete data record for the selected trace format.",
                    lineNumber);
        }
    }

    /// <summary>Recognises ASC lines of another bus (LIN, FlexRay, CAN XL) by the bus keyword in the
    /// second or third token so they are counted as unsupported rather than as malformed CAN.</summary>
    internal static string? AscOtherBusLine(ReadOnlySpan<char> line)
    {
        Span<Range> cells = stackalloc Range[4];
        var count = Tokenize(line, cells);
        for (var i = 1; i < Math.Min(count, 3); i++)
        {
            var token = line[cells[i]];
            if (token.Equals("Lin", StringComparison.OrdinalIgnoreCase) || token.Equals("Li", StringComparison.Ordinal) ||
                token.Equals("L2", StringComparison.Ordinal)) return "LIN";
            if (token.Equals("FlexRay", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("FR", StringComparison.OrdinalIgnoreCase)) return "FlexRay";
            if (token.Equals("CANXL", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("CAN_XL", StringComparison.OrdinalIgnoreCase)) return "CAN_XL";
        }
        return null;
    }

    /// <summary>Non-frame ASC records (measurement start, chip status, bus statistics) are not
    /// lost frames, so they must not be reported as rejected data.</summary>
    private static bool IsAscInformationalRecord(ReadOnlySpan<char> trimmed) =>
        trimmed.Contains("Start of measurement", StringComparison.OrdinalIgnoreCase) ||
        trimmed.Contains(" Status:", StringComparison.OrdinalIgnoreCase) ||
        trimmed.Contains(" Statistic:", StringComparison.OrdinalIgnoreCase);

    private static void RecordAscLoggedMetadata(string line, TraceImportHealth diagnostics)
    {
        if (line.Contains("Statistic:", StringComparison.OrdinalIgnoreCase) &&
            LoggedBusMetadata.TryParseStatistic(line, out var statistic))
            diagnostics.RecordLogged(statistic);
        else if (line.Contains("Status:", StringComparison.OrdinalIgnoreCase) &&
                 LoggedBusMetadata.TryParseStatus(line, out var status))
            diagnostics.RecordLogged(status);
    }

    private static bool IsTextMetadata(string extension, string line, long lineNumber)
    {
        var trimmed = line.AsSpan().Trim();
        if (trimmed.IsEmpty) return true;
        if (extension == ".asc")
            return trimmed.StartsWith("date ", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("base ", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("internal events logged", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("//", StringComparison.Ordinal) ||
                   trimmed.StartsWith("Begin Triggerblock", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("End Triggerblock", StringComparison.OrdinalIgnoreCase) ||
                   IsAscInformationalRecord(trimmed);
        if (extension is ".log" or ".txt")
            return trimmed.StartsWith("#", StringComparison.Ordinal);
        if (extension == ".csv")
            return trimmed.StartsWith("#", StringComparison.Ordinal) ||
                   // A first line that does not start like a number is a column header
                   // ("time,...", "time_s,...", "Timestamp;..."), not a rejected record.
                   lineNumber == 1 && !(char.IsDigit(trimmed[0]) || trimmed[0] is '-' or '+' or '.');
        return false;
    }

    // Span-based whitespace tokenizer used by the hot text parsers so no per-token strings
    // are allocated (only the line string from ReadLineAsync remains).
    internal static int Tokenize(ReadOnlySpan<char> line, Span<Range> ranges)
    {
        var count = 0;
        var start = -1;
        for (var i = 0; i < line.Length && count < ranges.Length; i++)
        {
            var whitespace = line[i] is ' ' or '\t';
            if (!whitespace && start < 0)
            {
                start = i;
            }
            else if (whitespace && start >= 0)
            {
                ranges[count++] = new Range(start, i);
                start = -1;
            }
        }

        if (start >= 0 && count < ranges.Length)
        {
            ranges[count++] = new Range(start, line.Length);
        }

        return count;
    }

    private static CanFrame? ParseAsc(string line)
    {
        var span = line.AsSpan();
        Span<Range> cells = stackalloc Range[80];
        var count = Tokenize(span, cells);
        if (count >= 5 && span[cells[1]].Equals("CANFD", StringComparison.OrdinalIgnoreCase))
            return ParseAscCanFd(span, cells, count);
        if (count >= 3 &&
            double.TryParse(span[cells[0]], NumberStyles.Float, CultureInfo.InvariantCulture, out var errorSeconds) &&
            int.TryParse(span[cells[1]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var errorChannel) &&
            span[cells[2]].Equals("ErrorFrame", StringComparison.OrdinalIgnoreCase))
        {
            // Bus error frames carry no CAN identifier or payload that can be trusted.
            return new CanFrame(Math.Max(0, errorChannel - 1), 0, [],
                checked((long)Math.Round(errorSeconds * 1_000_000_000d)), CanFrameFlags.Error);
        }

        if (count < 5 ||
            !double.TryParse(span[cells[0]], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !int.TryParse(span[cells[1]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var channelNumber))
        {
            return null;
        }

        var idText = span[cells[2]];
        var extended = idText.EndsWith("x", StringComparison.OrdinalIgnoreCase) || idText.Length > 3;
        idText = idText.TrimEnd('x').TrimEnd('X');
        if (!uint.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var identifier))
        {
            return null;
        }

        if (span[cells[4]].Equals("r", StringComparison.OrdinalIgnoreCase))
        {
            // A remote CAN frame requests a DLC without carrying data. Some ASC
            // writers omit the DLC; unknown is then recorded as 0, not inferred
            // from an unrelated trailing "Length =" metadata field.
            byte requestedLength = 0;
            if (count > 5 && byte.TryParse(span[cells[5]], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var parsedLength))
            {
                if (parsedLength > 8) return null;
                requestedLength = parsedLength;
            }
            return new CanFrame(
                Math.Max(0, channelNumber - 1), identifier, [],
                checked((long)Math.Round(seconds * 1_000_000_000d)),
                (extended ? CanFrameFlags.Extended : CanFrameFlags.None) | CanFrameFlags.Remote | AscDirection(span[cells[3]]),
                declaredDataLength: requestedLength);
        }

        if (count < 6) return null;
        var lengthIndex = 5;
        if (!int.TryParse(span[cells[lengthIndex]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
        {
            lengthIndex = -1;
            for (var i = 3; i < count; i++)
            {
                if (int.TryParse(span[cells[i]], NumberStyles.Integer, CultureInfo.InvariantCulture, out length))
                {
                    lengthIndex = i;
                    break;
                }
            }

            if (lengthIndex < 0)
            {
                return null;
            }
        }

        if (length is < 0 or > 64 || count - lengthIndex - 1 < length)
            return null; // Never turn a truncated data line into a complete CAN frame.
        var data = new byte[length];
        for (var index = 0; index < data.Length; index++)
        {
            if (!byte.TryParse(
                    span[cells[lengthIndex + 1 + index]],
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out data[index]))
            {
                return null;
            }
        }

        return new CanFrame(
            Math.Max(0, channelNumber - 1),
            identifier,
            data,
            checked((long)Math.Round(seconds * 1_000_000_000d)),
            (extended ? CanFrameFlags.Extended : CanFrameFlags.None) | AscDirection(span[cells[3]]));
    }

    /// <summary>"Tx" in the ASC direction column marks a transmitted frame; "Rx" (and "TxRq") leave it unset.</summary>
    private static CanFrameFlags AscDirection(ReadOnlySpan<char> token) =>
        token.Equals("Tx", StringComparison.OrdinalIgnoreCase) ? CanFrameFlags.Transmit : CanFrameFlags.None;

    /// <summary>Vector ASC CAN FD record:
    /// <c>time CANFD channel Rx|Tx id[x] [name] BRS ESI dlcCode dataLength data... trailer</c>.</summary>
    private static CanFrame? ParseAscCanFd(ReadOnlySpan<char> span, Span<Range> cells, int count)
    {
        if (!double.TryParse(span[cells[0]], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !int.TryParse(span[cells[2]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var channelNumber))
            return null;
        var timestamp = checked((long)Math.Round(seconds * 1_000_000_000d));
        if (span[cells[4]].Equals("ErrorFrame", StringComparison.OrdinalIgnoreCase))
            return new CanFrame(Math.Max(0, channelNumber - 1), 0, [], timestamp,
                CanFrameFlags.Error | CanFrameFlags.FlexibleDataRate);

        var idText = span[cells[4]];
        var extended = idText.EndsWith("x", StringComparison.OrdinalIgnoreCase);
        idText = idText.TrimEnd('x').TrimEnd('X');
        if (!uint.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var identifier))
            return null;

        static bool IsBit(ReadOnlySpan<char> token) => token.Length == 1 && (token[0] is '0' or '1');
        // An optional symbolic message name (no spaces) may precede the BRS/ESI/DLC/length group.
        for (var i = 5; i + 3 < count; i++)
        {
            var dlcToken = span[cells[i + 2]];
            if (!IsBit(span[cells[i]]) || !IsBit(span[cells[i + 1]]) || dlcToken.Length != 1 ||
                !uint.TryParse(dlcToken, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ||
                !int.TryParse(span[cells[i + 3]], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) ||
                length is < 0 or > 64 || count - (i + 4) < length)
                continue;
            var data = new byte[length];
            for (var index = 0; index < length; index++)
                if (!byte.TryParse(span[cells[i + 4 + index]], NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out data[index]))
                    return null;
            var flags = CanFrameFlags.FlexibleDataRate;
            if (span[cells[i]][0] == '1') flags |= CanFrameFlags.BitRateSwitch;
            if (extended) flags |= CanFrameFlags.Extended;
            flags |= AscDirection(span[cells[3]]);
            return new CanFrame(Math.Max(0, channelNumber - 1), identifier, data, timestamp, flags);
        }
        return null;
    }

    private static CanFrame? ParseCandump(string line)
    {
        var match = CandumpLine().Match(line);
        if (!match.Success)
            return ParseCandumpTerminal(line);

        var timestamp = match.Groups["time"].Success
            ? ParseSeconds(match.Groups["time"].Value)
            : 0;
        var interfaceText = match.Groups["interface"].Value;
        var channelDigits = new string(interfaceText.Where(char.IsDigit).ToArray());
        var channel = int.TryParse(channelDigits, out var parsedChannel) ? parsedChannel : 0;
        var idText = match.Groups["id"].Value;
        var identifier = uint.Parse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        // candump writes 3 hex digits for an 11-bit identifier and 8 for a 29-bit one, so the
        // width (not the numeric value) tells the frame format: 00000123 is an extended frame.
        var flags = idText.Length > 3 || identifier > 0x7FF ? CanFrameFlags.Extended : CanFrameFlags.None;
        // Newer can-utils append the direction: " T" = sent by the logging node, " R" = received.
        if (match.Groups["direction"].Value == "T") flags |= CanFrameFlags.Transmit;

        if (match.Groups["remote"].Success)
        {
            // "123#R" or "123#R4": remote request, optionally with the requested length.
            byte? requested = match.Groups["rlen"].Success ? (byte)(match.Groups["rlen"].Value[0] - '0') : null;
            return new CanFrame(channel, identifier, ReadOnlySpan<byte>.Empty, timestamp,
                flags | CanFrameFlags.Remote, declaredDataLength: requested);
        }

        if (match.Groups["fdflags"].Success)
        {
            // "123##<flags><data>": CAN FD, one hex nibble of flags (bit 0 = bit-rate switch).
            var fdFlags = Convert.ToInt32(match.Groups["fdflags"].Value, 16);
            flags |= CanFrameFlags.FlexibleDataRate;
            if ((fdFlags & 0x1) != 0) flags |= CanFrameFlags.BitRateSwitch;
            return new CanFrame(channel, identifier,
                Convert.FromHexString(match.Groups["fddata"].Value.Replace(".", string.Empty)), timestamp, flags);
        }

        var data = Convert.FromHexString(match.Groups["data"].Value.Replace(".", string.Empty));
        return new CanFrame(channel, identifier, data, timestamp, flags);
    }

    /// <summary>
    /// The default human-readable candump display: "(000.123456)  can0  18FEF100   [8]  11 22 33 ..."
    /// (the timestamp is optional). A 3-digit identifier is 11-bit, an 8-digit one is 29-bit.
    /// </summary>
    private static CanFrame? ParseCandumpTerminal(string line)
    {
        var match = CandumpTerminalLine().Match(line);
        if (!match.Success)
            return null;
        var idText = match.Groups["id"].Value;
        if (!uint.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var identifier))
            return null;
        if (!int.TryParse(match.Groups["len"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) ||
            length is < 0 or > 64)
            return null;
        var bytes = match.Groups["data"].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => Convert.ToByte(token, 16)).ToArray();
        if (bytes.Length != length)
            return null; // never pad or cut a record whose bracketed length disagrees with its bytes
        var interfaceText = match.Groups["interface"].Value;
        var channelDigits = new string(interfaceText.Where(char.IsDigit).ToArray());
        var channel = int.TryParse(channelDigits, out var parsedChannel) ? parsedChannel : 0;
        var timestamp = match.Groups["time"].Success ? ParseSeconds(match.Groups["time"].Value) : 0;
        var extended = idText.Length > 3 || identifier > 0x7FF;
        return new CanFrame(channel, identifier, bytes, timestamp,
            extended ? CanFrameFlags.Extended : CanFrameFlags.None);
    }

    private static CanFrame? ParseCsv(string line)
    {
        var cells = line.Split([',', ';', '\t'], StringSplitOptions.TrimEntries);
        if (cells.Length < 4 ||
            !double.TryParse(cells[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !int.TryParse(cells[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel))
        {
            return null;
        }

        var idText = cells[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? cells[2][2..]
            : cells[2];
        if (!uint.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        // The exporter's own layout: time_s,channel,id_hex,dlc,DATAHEX (contiguous hex, empty for remote frames).
        if (cells.Length == 5 && int.TryParse(cells[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dlc) &&
            dlc is >= 0 and <= 64)
        {
            var hex = cells[4].Replace(" ", string.Empty);
            if (hex.Length == dlc * 2 && hex.All(Uri.IsHexDigit))
            {
                return new CanFrame(channel, id, Convert.FromHexString(hex),
                    ParseSeconds(cells[0]),
                    id > 0x7FF ? CanFrameFlags.Extended : CanFrameFlags.None);
            }

            if (hex.Length == 0 && dlc is > 0 and <= 8)
            {
                return new CanFrame(channel, id, ReadOnlySpan<byte>.Empty,
                    ParseSeconds(cells[0]),
                    (id > 0x7FF ? CanFrameFlags.Extended : CanFrameFlags.None) | CanFrameFlags.Remote,
                    declaredDataLength: (byte)dlc);
            }
        }

        var data = ParseBytes(string.Join(' ', cells.Skip(3))).ToArray();
        return new CanFrame(
            channel,
            id,
            data,
            ParseSeconds(cells[0]),
            id > 0x7FF ? CanFrameFlags.Extended : CanFrameFlags.None);
    }

    private static IEnumerable<byte> ParseBytes(string text) =>
        text.Split([' ', '-', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => byte.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    /// <summary>Seconds text to nanoseconds without going through a double: an epoch timestamp
    /// such as 1612345678.123456 has more significant digits than a double holds, and the
    /// rounding error would show up as jitter in every delta.</summary>
    private static long ParseSeconds(string value)
    {
        if (decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var exact))
            return checked((long)Math.Round(exact * 1_000_000_000m));
        // Exponent notation and out-of-decimal-range values keep the previous behaviour.
        return checked((long)Math.Round(double.Parse(value, CultureInfo.InvariantCulture) * 1_000_000_000d));
    }

    [GeneratedRegex(
        @"^\s*(?:\((?<time>\d+(?:\.\d+)?)\)\s+)?(?<interface>\w+)\s+(?<id>[0-9A-Fa-f]{3}|[0-9A-Fa-f]{8})\s+\[(?<len>\d+)\]\s*(?<data>(?:[0-9A-Fa-f]{2}\s*)*)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CandumpTerminalLine();

    [GeneratedRegex(
        @"^\s*(?:\((?<time>\d+(?:\.\d+)?)\)\s+)?(?<interface>\w+)\s+(?<id>[0-9A-Fa-f]{1,8})(?:#(?<remote>R)(?<rlen>[0-8])?|##(?<fdflags>[0-9A-Fa-f])(?<fddata>(?:[0-9A-Fa-f]{2}\.?)*)|#(?<data>(?:[0-9A-Fa-f]{2}\.?)*))(?:\s+(?<direction>[TR]))?\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CandumpLine();
}
