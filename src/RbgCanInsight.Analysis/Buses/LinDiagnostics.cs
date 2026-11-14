using System.Globalization;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis.Buses;

public enum LinDiagDirection { Request, Response }

public enum LinDiagStatus { Complete, Incomplete, SequenceError, OrphanConsecutive, Interrupted, GoToSleep, BadFrame }

/// <summary>One diagnostic transaction half (request or response) rebuilt from LIN frames 0x3C / 0x3D.
/// Payload excludes NAD, PCI and padding. Indices refer to the LIN frame list.</summary>
public sealed record LinDiagMessage(
    int Channel, long StartTimestampNanoseconds, LinDiagDirection Direction, byte Nad, int DeclaredLength,
    byte[] Payload, LinDiagStatus Status, string Decoded, IReadOnlyList<int> FrameIndices);

/// <summary>LIN transport layer (ISO 17987-2 / LIN 2.x) reassembly of single, first and consecutive frames and
/// node-configuration / UDS-on-LIN decoding. Only recorded bytes are interpreted.</summary>
public static class LinDiagnosticsDecoder
{
    private sealed class Open
    {
        public byte Nad; public int Length; public List<byte> Data = []; public int NextSeq = 1;
        public long Start; public List<int> Indices = [];
    }

    public static IReadOnlyList<LinDiagMessage> Decode(IReadOnlyList<LinFrame> frames)
    {
        var result = new List<LinDiagMessage>();
        var open = new Dictionary<(int, LinDiagDirection), Open>();

        void Emit(int ch, LinDiagDirection dir, Open o, LinDiagStatus status)
        {
            var payload = o.Data.Take(Math.Min(o.Length, o.Data.Count)).ToArray();
            result.Add(new(ch, o.Start, dir, o.Nad, o.Length, payload, status,
                status is LinDiagStatus.Complete ? DecodePayload(dir, payload) : "", o.Indices.ToArray()));
        }

        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (f.Event != LinEventKind.Message || !LinProtocol.IsDiagnosticId(f.FrameId)) continue;
            var dir = f.FrameId == LinProtocol.MasterRequestId ? LinDiagDirection.Request : LinDiagDirection.Response;
            var key = (f.Channel, dir);
            if (f.Data.Length != 8 || f.DeclaredLength != 8)
            {
                result.Add(new(f.Channel, f.TimestampNanoseconds, dir, f.Data.Length > 0 ? f.Data[0] : (byte)0, 0, [],
                    LinDiagStatus.BadFrame, $"diagnostic frame with {f.Data.Length} recorded byte(s) (8 required)", [i]));
                continue;
            }

            var d = f.Data;
            if (dir == LinDiagDirection.Request && d[0] == 0x00 && d.Skip(1).All(b => b == 0xFF))
            {
                result.Add(new(f.Channel, f.TimestampNanoseconds, dir, 0, 0, [], LinDiagStatus.GoToSleep,
                    "Go-to-sleep command (NAD 0x00, remaining bytes 0xFF)", [i]));
                continue;
            }

            var type = d[1] >> 4;
            if (type != 2 && open.Remove(key, out var stale))
                Emit(f.Channel, dir, stale, LinDiagStatus.Interrupted);

            switch (type)
            {
                case 0: // single frame
                {
                    var len = d[1] & 0x0F;
                    if (len is < 1 or > 6)
                    {
                        result.Add(new(f.Channel, f.TimestampNanoseconds, dir, d[0], len, [], LinDiagStatus.BadFrame,
                            $"single frame with invalid length {len}", [i]));
                        break;
                    }
                    var one = new Open { Nad = d[0], Length = len, Start = f.TimestampNanoseconds, Indices = [i] };
                    one.Data.AddRange(d.AsSpan(2, len).ToArray());
                    Emit(f.Channel, dir, one, LinDiagStatus.Complete);
                    break;
                }
                case 1: // first frame
                {
                    var len = ((d[1] & 0x0F) << 8) | d[2];
                    if (len < 7)
                    {
                        result.Add(new(f.Channel, f.TimestampNanoseconds, dir, d[0], len, [], LinDiagStatus.BadFrame,
                            $"first frame with length {len} (must be above 6)", [i]));
                        break;
                    }
                    var first = new Open { Nad = d[0], Length = len, Start = f.TimestampNanoseconds, Indices = [i] };
                    first.Data.AddRange(d.AsSpan(3, 5).ToArray());
                    open[key] = first;
                    break;
                }
                case 2: // consecutive frame
                {
                    var seq = d[1] & 0x0F;
                    if (!open.TryGetValue(key, out var o) || o.Nad != d[0])
                    {
                        result.Add(new(f.Channel, f.TimestampNanoseconds, dir, d[0], 0, [], LinDiagStatus.OrphanConsecutive,
                            "consecutive frame without a matching first frame", [i]));
                        break;
                    }
                    o.Indices.Add(i);
                    if (seq != (o.NextSeq & 0x0F))
                    {
                        open.Remove(key);
                        Emit(f.Channel, dir, o, LinDiagStatus.SequenceError);
                        result[^1] = result[^1] with { Decoded = $"expected sequence {o.NextSeq & 0x0F}, got {seq}" };
                        break;
                    }
                    o.NextSeq++;
                    o.Data.AddRange(d.AsSpan(2, 6).ToArray());
                    if (o.Data.Count >= o.Length)
                    {
                        open.Remove(key);
                        Emit(f.Channel, dir, o, LinDiagStatus.Complete);
                    }
                    break;
                }
                default:
                    result.Add(new(f.Channel, f.TimestampNanoseconds, dir, d[0], 0, [], LinDiagStatus.BadFrame,
                        $"unknown PCI type 0x{d[1]:X2}", [i]));
                    break;
            }
        }

        foreach (var ((ch, dir), o) in open.OrderBy(p => p.Value.Start))
            Emit(ch, dir, o, LinDiagStatus.Incomplete);
        return result.OrderBy(m => m.StartTimestampNanoseconds).ToArray();
    }

    private static readonly Dictionary<byte, string> NodeConfig = new()
    {
        [0xB0] = "Assign NAD", [0xB1] = "Assign frame identifier", [0xB2] = "Read by identifier",
        [0xB3] = "Conditional change NAD", [0xB4] = "Data dump", [0xB5] = "Assign NAD via SNPD",
        [0xB6] = "Save configuration", [0xB7] = "Assign frame identifier range"
    };

    private static readonly Dictionary<byte, string> Uds = new()
    {
        [0x10] = "DiagnosticSessionControl", [0x11] = "ECUReset", [0x14] = "ClearDiagnosticInformation",
        [0x19] = "ReadDTCInformation", [0x22] = "ReadDataByIdentifier", [0x27] = "SecurityAccess",
        [0x28] = "CommunicationControl", [0x2E] = "WriteDataByIdentifier", [0x2F] = "InputOutputControlByIdentifier",
        [0x31] = "RoutineControl", [0x34] = "RequestDownload", [0x36] = "TransferData",
        [0x37] = "RequestTransferExit", [0x3E] = "TesterPresent", [0x85] = "ControlDTCSetting"
    };

    private static readonly Dictionary<byte, string> Nrc = new()
    {
        [0x10] = "generalReject", [0x11] = "serviceNotSupported", [0x12] = "subFunctionNotSupported",
        [0x13] = "incorrectMessageLengthOrInvalidFormat", [0x22] = "conditionsNotCorrect",
        [0x31] = "requestOutOfRange", [0x33] = "securityAccessDenied", [0x35] = "invalidKey",
        [0x78] = "requestCorrectlyReceivedResponsePending"
    };

    public static string NadMeaning(byte nad) => nad switch
    {
        0x00 => "go-to-sleep", 0x7E => "functional (broadcast)", 0x7F => "wildcard (any node)",
        >= 0x80 => "free/reserved range", _ => "slave node"
    };

    private static string H(IEnumerable<byte> b) => string.Join(' ', b.Select(x => x.ToString("X2", CultureInfo.InvariantCulture)));
    private static int U16(byte[] p, int o) => p[o] | (p[o + 1] << 8);

    public static string DecodePayload(LinDiagDirection dir, byte[] p)
    {
        if (p.Length == 0) return "";
        var sid = p[0];
        if (sid == 0x7F)
        {
            if (p.Length < 3) return "negative response (truncated)";
            var name = Nrc.TryGetValue(p[2], out var n) ? n : "NRC not in table";
            return $"Negative response to SID 0x{p[1]:X2}: NRC 0x{p[2]:X2} ({name})";
        }
        var requestSid = dir == LinDiagDirection.Response && sid >= 0x40 ? (byte)(sid - 0x40) : sid;
        var positive = dir == LinDiagDirection.Response && sid >= 0x40;
        var suffix = positive ? " (positive response)" : "";
        if (NodeConfig.TryGetValue(requestSid, out var cfg))
        {
            var detail = requestSid switch
            {
                0xB0 when !positive && p.Length >= 6 => $"supplier 0x{U16(p, 1):X4}, function 0x{U16(p, 3):X4}, new NAD 0x{p[5]:X2}",
                0xB2 when !positive && p.Length >= 6 => $"identifier {p[1]}, supplier 0x{U16(p, 2):X4}, function 0x{U16(p, 4):X4}",
                0xB3 when !positive && p.Length >= 6 => $"id {p[1]}, byte {p[2]}, mask 0x{p[3]:X2}, invert 0x{p[4]:X2}, new NAD 0x{p[5]:X2}",
                0xB7 when !positive && p.Length >= 6 => $"start index {p[1]}, PIDs {H(p.Skip(2).Take(4))}",
                _ => H(p.Skip(1))
            };
            if (requestSid == 0xB2 && positive)
                detail = p.Length >= 6
                    ? $"{H(p.Skip(1))}; if identifier 0 was requested: supplier 0x{U16(p, 1):X4}, function 0x{U16(p, 3):X4}, variant {p[5]}"
                    : H(p.Skip(1)) + " (meaning depends on the requested identifier)";
            return $"{cfg}{suffix}: {detail}".TrimEnd(' ', ':');
        }
        if (Uds.TryGetValue(requestSid, out var uds))
        {
            var detail = requestSid == 0x22 && !positive
                ? "DID " + string.Join(", ", Enumerable.Range(0, (p.Length - 1) / 2).Select(k => $"0x{(p[1 + 2 * k] << 8 | p[2 + 2 * k]):X4}"))
                : H(p.Skip(1));
            return $"{uds}{suffix}: {detail}".TrimEnd(' ', ':');
        }
        return $"SID 0x{sid:X2}{suffix}: {H(p.Skip(1))}".TrimEnd(' ', ':');
    }
}
