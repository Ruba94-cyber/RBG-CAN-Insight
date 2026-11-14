namespace RbgCanInsight.Analysis.E2E;

/// <summary>CRC primitives for AUTOSAR E2E (non-reflected, MSB first).</summary>
public static class E2eCrc
{
    public static byte Crc8Update(byte crc, byte poly, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ poly) : (byte)(crc << 1);
        }
        return crc;
    }

    public static ushort Crc16Update(ushort crc, ushort poly, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ poly) : (ushort)(crc << 1);
        }
        return crc;
    }

    /// <summary>CRC-8 0x1D, init 0xFF, xorout 0xFF (check '123456789' = 0x4B).</summary>
    public static byte Crc8_1D(ReadOnlySpan<byte> data) => (byte)(Crc8Update(0xFF, 0x1D, data) ^ 0xFF);

    /// <summary>CRC-8 0x2F, init 0xFF, xorout 0xFF (check '123456789' = 0xDF).</summary>
    public static byte Crc8_2F(ReadOnlySpan<byte> data) => (byte)(Crc8Update(0xFF, 0x2F, data) ^ 0xFF);

    /// <summary>CRC-16 0x1021, init 0xFFFF, no xorout (check '123456789' = 0x29B1).</summary>
    public static ushort Crc16_1021(ReadOnlySpan<byte> data) => Crc16Update(0xFFFF, 0x1021, data);

    /// <summary>Profile 1 CRC: data ID byte(s), then payload without the CRC byte.</summary>
    public static byte Profile1(byte[] payload, int crcByte, ushort dataId, E2eP1DataIdMode mode, int counter)
    {
        byte crc = 0xFF;
        var lo = (byte)(dataId & 0xFF);
        var hi = (byte)(dataId >> 8);
        switch (mode)
        {
            case E2eP1DataIdMode.Both: crc = Crc8Update(crc, 0x1D, new[] { lo, hi }); break;
            case E2eP1DataIdMode.Alt: crc = Crc8Update(crc, 0x1D, new[] { (counter & 1) == 0 ? lo : hi }); break;
            case E2eP1DataIdMode.Low: crc = Crc8Update(crc, 0x1D, new[] { lo }); break;
            case E2eP1DataIdMode.High: crc = Crc8Update(crc, 0x1D, new[] { hi }); break;
        }
        for (var i = 0; i < payload.Length; i++)
            if (i != crcByte) crc = Crc8Update(crc, 0x1D, new[] { payload[i] });
        return (byte)(crc ^ 0xFF);
    }

    /// <summary>Profile 2 CRC: payload without CRC byte, then data-ID-list entry selected by the counter.</summary>
    public static byte Profile2(byte[] payload, int crcByte, byte dataIdForCounter)
    {
        byte crc = 0xFF;
        for (var i = 0; i < payload.Length; i++)
            if (i != crcByte) crc = Crc8Update(crc, 0x2F, new[] { payload[i] });
        crc = Crc8Update(crc, 0x2F, new[] { dataIdForCounter });
        return (byte)(crc ^ 0xFF);
    }

    /// <summary>Profile 5 CRC: payload without the two CRC bytes, then data ID little endian.</summary>
    public static ushort Profile5(byte[] payload, int crcByte, ushort dataId)
    {
        ushort crc = 0xFFFF;
        for (var i = 0; i < payload.Length; i++)
            if (i != crcByte && i != crcByte + 1) crc = Crc16Update(crc, 0x1021, new[] { payload[i] });
        return Crc16Update(crc, 0x1021, new[] { (byte)(dataId & 0xFF), (byte)(dataId >> 8) });
    }
}

/// <summary>
/// Offline AUTOSAR E2E verification (profiles 1, 2, 5) of recorded frames against a user supplied
/// configuration. The CRC is checked first (a wrong-CRC frame does not update the last valid counter),
/// then the counter delta.
/// </summary>
public static class E2eChecker
{
    public static E2eReport Check(IReadOnlyList<E2eFrame> frames, IReadOnlyList<E2eConfig> configs)
    {
        foreach (var cfg in configs)
        {
            var err = Validate(cfg);
            if (err != null) return new E2eReport(Array.Empty<E2eSummary>(), err);
        }
        var summaries = new List<E2eSummary>();
        foreach (var cfg in configs)
        {
            var results = new List<E2eFrameResult>();
            var lastCounter = -1;
            double? lastTs = null;
            var wrap = cfg.Profile == E2eProfile.P05 ? 256 : cfg.Profile == E2eProfile.P01 ? 15 : 16;
            for (var idx = 0; idx < frames.Count; idx++)
            {
                var f = frames[idx];
                if (f.Channel != cfg.Channel || f.Id != cfg.Id || f.Extended != cfg.Extended) continue;
                double? gap = lastTs.HasValue ? f.TimestampSeconds - lastTs.Value : null;
                var timeout = cfg.CycleTimeSeconds is double cyc && gap is double g && g > cyc * cfg.TimeoutFactor;
                lastTs = f.TimestampSeconds;

                if (f.Data == null || f.Data.Length < cfg.PayloadLength)
                {
                    results.Add(new E2eFrameResult(idx, f.TimestampSeconds, E2eStatus.WrongLength, 0, -1, 0, 0, timeout, gap));
                    continue;
                }
                var p = f.Data.AsSpan(0, cfg.PayloadLength).ToArray();
                var counter = ReadCounter(cfg, p);
                var crcByte = cfg.CrcBitOffset / 8;
                uint rx, exp;
                switch (cfg.Profile)
                {
                    case E2eProfile.P01:
                        rx = p[crcByte];
                        exp = E2eCrc.Profile1(p, crcByte, cfg.DataId, cfg.P1DataIdMode, counter);
                        break;
                    case E2eProfile.P02:
                        rx = p[crcByte];
                        exp = E2eCrc.Profile2(p, crcByte, cfg.P2DataIdList != null ? cfg.P2DataIdList[counter] : (byte)(cfg.DataId & 0xFF));
                        break;
                    default:
                        rx = (uint)(p[crcByte] | (p[crcByte + 1] << 8));
                        exp = E2eCrc.Profile5(p, crcByte, cfg.DataId);
                        break;
                }

                E2eStatus st;
                var lost = 0;
                if (rx != exp) st = E2eStatus.WrongCrc;
                else if (cfg.Profile == E2eProfile.P01 && counter == 15) st = E2eStatus.InvalidCounter;
                else if (lastCounter < 0) { st = E2eStatus.Ok; lastCounter = counter; }
                else
                {
                    var delta = ((counter - lastCounter) % wrap + wrap) % wrap;
                    if (delta == 0) st = E2eStatus.Repeated;
                    else if (delta == 1) st = E2eStatus.Ok;
                    else if (delta <= cfg.MaxDeltaCounter) { st = E2eStatus.OkSomeLost; lost = delta - 1; }
                    else st = E2eStatus.WrongSequence;
                    if (st != E2eStatus.Repeated) lastCounter = counter;
                }
                results.Add(new E2eFrameResult(idx, f.TimestampSeconds, st, lost, counter, rx, exp, timeout, gap));
            }
            summaries.Add(Summarize(cfg, results));
        }
        return new E2eReport(summaries, null);
    }

    public static string? Validate(E2eConfig c)
    {
        if (c.PayloadLength < 1 || c.PayloadLength > 64) return "Payload length must be 1..64.";
        if (c.CrcBitOffset < 0 || c.CrcBitOffset % 8 != 0) return "CRC bit offset must be a non-negative multiple of 8.";
        var crcLen = c.Profile == E2eProfile.P05 ? 2 : 1;
        if (c.CrcBitOffset / 8 + crcLen > c.PayloadLength) return "CRC does not fit in the payload.";
        if (c.CounterBitOffset < 0 || c.CounterBitOffset / 8 >= c.PayloadLength) return "Counter does not fit in the payload.";
        var cb = c.CounterBitOffset / 8;
        var rb = c.CrcBitOffset / 8;
        if (c.Profile == E2eProfile.P05)
        {
            if (c.CounterBitOffset % 8 != 0) return "Profile 5 counter must be byte aligned.";
            if (cb == rb || cb == rb + 1) return "Counter overlaps CRC.";
        }
        else
        {
            if (c.CounterBitOffset % 8 != 0 && c.CounterBitOffset % 8 != 4) return "4-bit counter must start at bit 0 or 4 of a byte.";
            if (cb == rb) return "Counter overlaps CRC byte.";
        }
        if (c.Profile == E2eProfile.P02 && c.P2DataIdList != null && c.P2DataIdList.Length != 16) return "Profile 2 data ID list needs 16 entries.";
        if (c.MaxDeltaCounter < 1) return "MaxDeltaCounter must be >= 1.";
        return null;
    }

    private static int ReadCounter(E2eConfig c, byte[] p)
    {
        var b = p[c.CounterBitOffset / 8];
        return c.Profile == E2eProfile.P05 ? b : (c.CounterBitOffset % 8 == 0 ? b & 0x0F : b >> 4);
    }

    private static E2eSummary Summarize(E2eConfig cfg, List<E2eFrameResult> r)
    {
        int Count(E2eStatus s) => r.Count(x => x.Status == s);
        var other = r.Count(x => x.Status is E2eStatus.WrongLength or E2eStatus.InvalidCounter);
        var first = r.FirstOrDefault(x => x.Status != E2eStatus.Ok && x.Status != E2eStatus.OkSomeLost);
        int run = 0, best = 0;
        foreach (var x in r)
        {
            if (x.Status == E2eStatus.Ok) { run++; best = Math.Max(best, run); }
            else run = 0;
        }
        return new E2eSummary(cfg, r.Count, Count(E2eStatus.Ok), Count(E2eStatus.WrongCrc), Count(E2eStatus.WrongSequence),
            Count(E2eStatus.Repeated), Count(E2eStatus.OkSomeLost), other, r.Sum(x => x.LostFrames),
            r.Count(x => x.TimeoutBefore), first?.FrameIndex, first?.TimestampSeconds, best, r);
    }
}
