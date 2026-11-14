using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using RbgCanInsight.Domain;

namespace RbgCanInsight.TraceIO;

/// <summary>
/// Parsers for the two public CAN XL carriers of Linux SocketCAN. Layouts verified against
/// linux/can.h (struct canxl_frame), the tcpdump.org LINKTYPE_CAN_SOCKETCAN page and can-utils lib.c;
/// see docs/CAN_XL_SUPPORT_NOTES.md.
/// </summary>
public static partial class CanXlWire
{
    public const byte FlagSec = 0x01, FlagRrs = 0x02, FlagXlf = 0x80;
    private const byte KnownFlags = FlagSec | FlagRrs | FlagXlf;
    public const int PcapHeaderBytes = 12, PcapMinPacket = 13, PcapMaxPacket = 2060;

    /// <summary>True when a LINKTYPE_CAN_SOCKETCAN packet is CAN XL: byte 4 has bit 0x80 (never valid as a classic
    /// or FD length byte).</summary>
    public static bool IsPcapXl(ReadOnlySpan<byte> packet) => packet.Length > 4 && (packet[4] & FlagXlf) != 0;

    /// <summary>Parses one pcap packet. Header: prio/VCID u32 big-endian, flags, SDT, payload length u16
    /// little-endian, acceptance field u32 little-endian, payload, trailing padding. Returns null and an error text on failure.</summary>
    public static CanXlFrame? ParsePcapPacket(ReadOnlySpan<byte> packet, int channel, long timestampNs, out string? error)
    {
        error = null;
        if (packet.Length is < PcapMinPacket or > PcapMaxPacket)
        {
            error = $"A CAN XL packet of {packet.Length} bytes is outside the valid 13..2060 range; skipped.";
            return null;
        }
        var length = BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]);
        if (!CanXlFrame.IsValidPayloadLength(length))
        {
            error = $"A CAN XL packet declares payload length {length}; valid is 1..2048; skipped.";
            return null;
        }
        if (PcapHeaderBytes + length > packet.Length)
        {
            error = $"A CAN XL packet declares {length} payload bytes but carries only {packet.Length - PcapHeaderBytes}; skipped.";
            return null;
        }
        var flags = packet[4];
        if ((flags & ~KnownFlags) != 0)
        {
            error = $"A CAN XL packet has undefined flag bits (0x{flags:X2}); skipped.";
            return null;
        }
        var prio = BinaryPrimitives.ReadUInt32BigEndian(packet);
        return new CanXlFrame
        {
            Channel = channel,
            TimestampNanoseconds = timestampNs,
            PriorityId = prio & 0x7FF,
            Vcid = (byte)((prio >> 16) & 0xFF),
            Sec = (flags & FlagSec) != 0,
            Rrs = (flags & FlagRrs) != 0,
            Sdt = packet[5],
            AcceptanceField = BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]),
            Payload = packet.Slice(PcapHeaderBytes, length).ToArray()
        };
    }

    // candump -L:  (1234.567890) can0 VVPPP#FF:SS:AAAAAAAA#data   (VCID 2 hex, PRIO 3 hex, optional '.' between data bytes)
    [GeneratedRegex(@"^\s*(?:\((?<time>\d+(?:\.\d+)?)\)\s+)?(?<interface>\w+)\s+(?<vcid>[0-9A-Fa-f]{2})(?<prio>[0-9A-Fa-f]{3})#(?<flags>[0-9A-Fa-f]{2}):(?<sdt>[0-9A-Fa-f]{2}):(?<af>[0-9A-Fa-f]{8})#(?<data>[0-9A-Fa-f.]*)\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex XlLine();

    // Cheap pre-check: five id digits, '#', two hex digits and ':' (the XL header start).
    [GeneratedRegex(@"\s[0-9A-Fa-f]{5}#[0-9A-Fa-f]{2}:", RegexOptions.CultureInvariant)]
    private static partial Regex XlHint();

    public static bool LooksLikeCandumpXl(string line) => XlHint().IsMatch(line);

    /// <summary>Parses one candump -L CAN XL line; returns null with an error text when it is malformed.</summary>
    public static CanXlFrame? ParseCandumpLine(string line, out string? error)
    {
        error = null;
        var m = XlLine().Match(line);
        if (!m.Success)
        {
            error = "CAN XL candump line does not match VVPPP#FF:SS:AAAAAAAA#data.";
            return null;
        }
        var hex = m.Groups["data"].Value;
        // The '.' separator is allowed only between whole bytes.
        var digits = new System.Text.StringBuilder(hex.Length);
        for (var i = 0; i < hex.Length;)
        {
            if (hex[i] == '.') { i++; continue; }
            if (i + 1 >= hex.Length || hex[i + 1] == '.')
            {
                error = "CAN XL candump data has an odd number of hex digits.";
                return null;
            }
            digits.Append(hex, i, 2);
            i += 2;
        }
        var n = digits.Length / 2;
        if (!CanXlFrame.IsValidPayloadLength(n))
        {
            error = $"CAN XL candump line carries {n} data bytes; valid is 1..2048.";
            return null;
        }
        var flags = byte.Parse(m.Groups["flags"].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if ((flags & ~KnownFlags) != 0)
        {
            error = $"CAN XL candump line has undefined flag bits (0x{flags:X2}).";
            return null;
        }
        var ifaceDigits = new string(m.Groups["interface"].Value.Where(char.IsDigit).ToArray());
        var channel = int.TryParse(ifaceDigits, out var c) ? c : 0;
        long ts = 0;
        if (m.Groups["time"].Success)
        {
            var seconds = double.Parse(m.Groups["time"].Value, CultureInfo.InvariantCulture);
            if (seconds > 9.0e9)
            {
                error = "CAN XL candump timestamp is out of range.";
                return null;
            }
            ts = (long)Math.Round(seconds * 1_000_000_000d);
        }
        return new CanXlFrame
        {
            Channel = channel,
            TimestampNanoseconds = ts,
            PriorityId = uint.Parse(m.Groups["prio"].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            Vcid = byte.Parse(m.Groups["vcid"].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            Sec = (flags & FlagSec) != 0,
            Rrs = (flags & FlagRrs) != 0,
            Sdt = byte.Parse(m.Groups["sdt"].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            AcceptanceField = uint.Parse(m.Groups["af"].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            Payload = Convert.FromHexString(digits.ToString())
        };
    }
}
