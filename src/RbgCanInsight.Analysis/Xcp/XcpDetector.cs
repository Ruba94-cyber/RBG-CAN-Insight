using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis.Xcp;

/// <summary>
/// Heuristic discovery of XCP/CCP identifier pairs. A pair is a candidate when a master frame that looks like
/// CONNECT (XCP: FF 00|01; CCP: 01 CTR ...) is followed within <see cref="HandshakeWindowNanoseconds"/> by a
/// positive response on a different identifier (XCP: FF ...; CCP: FF 00 CTR with the same CTR).
/// Confidence is capped at 0.90: this is evidence for a human to confirm, never proof.
/// Score: 0.40 for a handshake, +0.20 response layout plausible, +0.15 for two or more handshakes,
/// +0.15 when later frames on the command id look like commands of the same protocol.
/// </summary>
public static class XcpDetector
{
    public const long HandshakeWindowNanoseconds = 200L * 1_000_000;

    private readonly record struct Key(XcpProtocol Protocol, int Channel, uint CmdId, bool CmdExt, uint ResId, bool ResExt);

    private sealed class Acc
    {
        public int Handshakes;
        public bool LayoutOk;
        public readonly List<int> Evidence = [];
    }

    public static IReadOnlyList<XcpPairCandidate> Detect(IReadOnlyList<CanFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var found = new Dictionary<Key, Acc>();

        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (f.IsError || f.Flags.HasFlag(CanFrameFlags.Remote) || f.DataLength < 2) continue;
            var b0 = f.GetByte(0);
            var b1 = f.GetByte(1);
            XcpProtocol? proto = null;
            if (b0 == 0xFF && b1 <= 1) proto = XcpProtocol.Xcp;
            else if (b0 == 0x01 && f.DataLength == 8) proto = XcpProtocol.Ccp;
            if (proto is null) continue;

            for (var j = i + 1; j < frames.Count; j++)
            {
                var g = frames[j];
                if (g.TimestampNanoseconds - f.TimestampNanoseconds > HandshakeWindowNanoseconds) break;
                if (g.Channel != f.Channel || g.IsError || g.Flags.HasFlag(CanFrameFlags.Remote)) continue;
                if (g.Identifier == f.Identifier && g.IsExtended == f.IsExtended) continue;
                if (g.DataLength < 8 || g.GetByte(0) != 0xFF) continue;

                bool layoutOk;
                if (proto == XcpProtocol.Xcp)
                {
                    var resource = g.GetByte(1);
                    var comm = g.GetByte(2);
                    var maxCto = g.GetByte(3);
                    layoutOk = (resource & 0xE2) == 0 && (comm & 0x38) == 0 && maxCto >= 8 && maxCto <= 64 &&
                               (g.Flags.HasFlag(CanFrameFlags.FlexibleDataRate) || maxCto == 8);
                }
                else
                {
                    if (g.DataLength != 8 || g.GetByte(1) != 0 || g.GetByte(2) != f.GetByte(1)) continue;
                    layoutOk = true; // CTR echo already verified above
                }

                var key = new Key(proto.Value, f.Channel, f.Identifier, f.IsExtended, g.Identifier, g.IsExtended);
                if (!found.TryGetValue(key, out var acc)) found[key] = acc = new Acc();
                acc.Handshakes++;
                acc.LayoutOk |= layoutOk;
                if (acc.Evidence.Count < 8) { acc.Evidence.Add(i); acc.Evidence.Add(j); }
                break;
            }
        }

        var result = new List<XcpPairCandidate>();
        foreach (var (key, acc) in found)
        {
            var reasons = new List<string> { $"{acc.Handshakes} CONNECT-like request/response handshake(s) within {HandshakeWindowNanoseconds / 1_000_000} ms" };
            var score = 0.40;
            if (acc.LayoutOk)
            {
                score += 0.20;
                reasons.Add(key.Protocol == XcpProtocol.Xcp
                    ? "response has a plausible CONNECT layout (reserved bits clear, MAX_CTO 8..64)"
                    : "CRM echoes the CTR of the CONNECT command with no error");
            }

            if (acc.Handshakes >= 2) { score += 0.15; reasons.Add("handshake repeated"); }
            var (plausible, total) = FollowUp(frames, key);
            if (total >= 2 && plausible >= 0.8 * total)
            {
                score += 0.15;
                reasons.Add($"{plausible} of {total} frames on the command id look like {key.Protocol} commands");
            }
            else if (total >= 2)
                reasons.Add($"only {plausible} of {total} frames on the command id look like {key.Protocol} commands");

            result.Add(new XcpPairCandidate(key.Protocol, key.Channel, key.CmdId, key.CmdExt, key.ResId, key.ResExt,
                Math.Round(Math.Min(score, 0.90), 2), acc.Handshakes, acc.Evidence.Distinct().Order().ToList(), reasons));
        }

        return result.OrderByDescending(c => c.Confidence).ThenByDescending(c => c.Handshakes)
            .ThenBy(c => c.Channel).ThenBy(c => c.CommandId).ToList();
    }

    private static (int Plausible, int Total) FollowUp(IReadOnlyList<CanFrame> frames, Key key)
    {
        int plausible = 0, total = 0;
        foreach (var f in frames)
        {
            if (f.Channel != key.Channel || f.Identifier != key.CmdId || f.IsExtended != key.CmdExt || f.IsError || f.DataLength == 0) continue;
            total++;
            var code = f.GetByte(0);
            if (key.Protocol == XcpProtocol.Xcp ? XcpCatalog.XcpCommandName(code) is not null : XcpCatalog.CcpCommandName(code) is not null)
                plausible++;
        }

        return (plausible, total);
    }
}
