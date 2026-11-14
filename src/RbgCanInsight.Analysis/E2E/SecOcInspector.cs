namespace RbgCanInsight.Analysis.E2E;

/// <summary>
/// Secured I-PDU layout: authentic payload, then truncated freshness value, then truncated MAC,
/// concatenated as an MSB-first bit stream after the authentic payload. Trailing bits are ignored.
/// </summary>
public sealed record SecOcConfig(int AuthenticPayloadBytes, int FreshnessBits, int MacBits);

public sealed record SecOcFrame(int FrameIndex, double TimestampSeconds, byte[] Payload, ulong TruncatedFreshness, byte[] TruncatedMac, bool Complete);

public sealed record SecOcReport(
    int Frames, int Incomplete, int RepeatedFreshness, int BackwardFreshness, int PossibleWraps,
    int? FirstAnomalyFrameIndex, IReadOnlyList<SecOcFrame> Parsed, string? Error)
{
    /// <summary>Always false: no keys are available, MACs are only split out, never verified.</summary>
    public bool MacVerified => false;
}

/// <summary>
/// Splits secured I-PDUs and reports freshness behaviour. Freshness is truncated, so a numeric decrease
/// may be a legitimate wrap; "backward" means the modular step is at least half the value range.
/// </summary>
public static class SecOcInspector
{
    public static SecOcReport Inspect(IReadOnlyList<(double Timestamp, byte[] Data)> frames, SecOcConfig cfg)
    {
        if (cfg.AuthenticPayloadBytes < 0 || cfg.FreshnessBits < 0 || cfg.FreshnessBits > 64 || cfg.MacBits < 0)
            return new SecOcReport(0, 0, 0, 0, 0, null, Array.Empty<SecOcFrame>(), "Invalid SecOC configuration (freshness 0..64 bits).");
        var parsed = new List<SecOcFrame>();
        int incomplete = 0, rep = 0, back = 0, wraps = 0;
        int? firstAnom = null;
        ulong? last = null;
        var mask = cfg.FreshnessBits == 64 ? ulong.MaxValue : (1UL << cfg.FreshnessBits) - 1;
        var half = cfg.FreshnessBits == 0 ? 0UL : 1UL << (cfg.FreshnessBits - 1);
        var needBits = cfg.AuthenticPayloadBytes * 8 + cfg.FreshnessBits + cfg.MacBits;
        for (var i = 0; i < frames.Count; i++)
        {
            var d = frames[i].Data;
            if (d.Length * 8 < needBits)
            {
                incomplete++;
                parsed.Add(new SecOcFrame(i, frames[i].Timestamp, d, 0, Array.Empty<byte>(), false));
                continue;
            }
            var payload = d.AsSpan(0, cfg.AuthenticPayloadBytes).ToArray();
            var bit = cfg.AuthenticPayloadBytes * 8;
            ulong fv = 0;
            for (var k = 0; k < cfg.FreshnessBits; k++) fv = (fv << 1) | Bit(d, bit + k);
            bit += cfg.FreshnessBits;
            var mac = new byte[(cfg.MacBits + 7) / 8];
            for (var k = 0; k < cfg.MacBits; k++)
                mac[k / 8] |= (byte)(Bit(d, bit + k) << (7 - k % 8));
            parsed.Add(new SecOcFrame(i, frames[i].Timestamp, payload, fv, mac, true));
            if (cfg.FreshnessBits > 0)
            {
                if (last.HasValue)
                {
                    var delta = (fv - last.Value) & mask;
                    if (delta == 0) { rep++; firstAnom ??= i; }
                    else if (delta >= half) { back++; firstAnom ??= i; }
                    else if (fv < last.Value) wraps++;
                }
                last = fv;
            }
        }
        return new SecOcReport(frames.Count, incomplete, rep, back, wraps, firstAnom, parsed, null);
    }

    private static ulong Bit(byte[] d, int pos) => (ulong)((d[pos / 8] >> (7 - pos % 8)) & 1);
}
