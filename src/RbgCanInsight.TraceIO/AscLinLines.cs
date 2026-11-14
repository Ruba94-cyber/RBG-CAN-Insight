using System.Globalization;
using RbgCanInsight.Domain;

namespace RbgCanInsight.TraceIO;

/// <summary>
/// ASC LIN records. Only the shapes seen in files written by Vector's own BLF-to-ASC converter (version 7.2.60 output
/// paired with the same data in BLF) are parsed:
/// <c>time Li id Rx|Tx len data... checksum = XX ...</c> and <c>time Li id TransmErr ...</c>.
/// These lines carry no channel number, so the frame is placed on channel 0. Any other LIN line is counted as
/// unsupported by the caller; nothing is guessed.
/// </summary>
internal static class AscLinLines
{
    public static LinFrame? TryParse(ReadOnlySpan<char> line)
    {
        Span<Range> c = stackalloc Range[40];
        var count = TraceImporter.Tokenize(line, c);
        if (count < 4 || !line[c[1]].Equals("Li", StringComparison.Ordinal)) return null;
        if (!double.TryParse(line[c[0]], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds < 0 || seconds > 9e9) return null;
        if (!byte.TryParse(line[c[2]], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) || id > 0x3F) return null;
        var timestamp = (long)Math.Round(seconds * 1_000_000_000d);

        var kind = line[c[3]];
        if (kind.Equals("TransmErr", StringComparison.Ordinal))
            return new LinFrame
            {
                Channel = 0, TimestampNanoseconds = timestamp, Event = LinEventKind.SendError, RawIdByte = id,
                Detail = "transmission error (ASC)"
            };

        var direction = kind.Equals("Rx", StringComparison.Ordinal) ? BusDirection.Rx
            : kind.Equals("Tx", StringComparison.Ordinal) ? BusDirection.Tx : (BusDirection?)null;
        if (direction is null || count < 5 ||
            !int.TryParse(line[c[4]], NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length > 8 ||
            count < 5 + length + 3) return null;
        var data = new byte[length];
        for (var i = 0; i < length; i++)
            if (!byte.TryParse(line[c[5 + i]], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out data[i])) return null;
        var at = 5 + length;
        if (!line[c[at]].Equals("checksum", StringComparison.Ordinal) || !line[c[at + 1]].Equals("=", StringComparison.Ordinal) ||
            !byte.TryParse(line[c[at + 2]], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var checksum)) return null;
        return new LinFrame
        {
            Channel = 0, TimestampNanoseconds = timestamp, Event = LinEventKind.Message, RawIdByte = id,
            DeclaredLength = length, Data = data, Checksum = checksum, Direction = direction.Value
        };
    }
}
