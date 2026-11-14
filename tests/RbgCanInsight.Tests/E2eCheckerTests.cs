using System.Text;
using RbgCanInsight.Analysis.E2E;
using Xunit;

namespace RbgCanInsight.Tests;

public class E2eCheckerTests
{
    // Independent reference: generic MSB-first CRC of arbitrary width (<= 32), written differently from the product code.
    private static uint RefCrc(int width, uint poly, uint init, uint xorOut, byte[] data)
    {
        var top = 1u << (width - 1);
        var mask = width == 32 ? uint.MaxValue : (1u << width) - 1;
        var reg = init;
        foreach (var b in data)
            for (var bit = 7; bit >= 0; bit--)
            {
                var inBit = (uint)((b >> bit) & 1);
                var topBit = (reg & top) != 0 ? 1u : 0u;
                reg = (reg << 1) & mask;
                if ((topBit ^ inBit) != 0) reg ^= poly;
            }
        return (reg ^ xorOut) & mask;
    }

    private static readonly byte[] Check = Encoding.ASCII.GetBytes("123456789");

    [Fact]
    public void KnownAnswerChecksOverAscii123456789()
    {
        Assert.Equal(0x4Bu, RefCrc(8, 0x1D, 0xFF, 0xFF, Check));
        Assert.Equal(0xDFu, RefCrc(8, 0x2F, 0xFF, 0xFF, Check));
        Assert.Equal(0x29B1u, RefCrc(16, 0x1021, 0xFFFF, 0, Check));
        Assert.Equal(0x4B, E2eCrc.Crc8_1D(Check));
        Assert.Equal(0xDF, E2eCrc.Crc8_2F(Check));
        Assert.Equal(0x29B1, E2eCrc.Crc16_1021(Check));
    }

    [Fact]
    public void ProductCrcMatchesReferenceOnPseudoRandomData()
    {
        var rnd = new Random(1234);
        for (var n = 0; n < 200; n++)
        {
            var d = new byte[rnd.Next(1, 12)];
            rnd.NextBytes(d);
            Assert.Equal(RefCrc(8, 0x1D, 0xFF, 0xFF, d), E2eCrc.Crc8_1D(d));
            Assert.Equal(RefCrc(8, 0x2F, 0xFF, 0xFF, d), E2eCrc.Crc8_2F(d));
            Assert.Equal(RefCrc(16, 0x1021, 0xFFFF, 0, d), E2eCrc.Crc16_1021(d));
        }
    }

    // ---- frame builders using the reference CRC ----
    private const ushort Did = 0x1234;

    private static byte[] P1(int counter, byte b2 = 0x11, byte b3 = 0x22, ushort did = Did)
    {
        var d = new byte[] { 0, (byte)(0xA0 | counter), b2, b3, 0x55, 0x66, 0x77, 0x88 };
        var input = new List<byte> { (byte)(did & 0xFF), (byte)(did >> 8) };
        input.AddRange(d.Skip(1));
        d[0] = (byte)RefCrc(8, 0x1D, 0xFF, 0xFF, input.ToArray());
        return d;
    }

    private static readonly byte[] IdList = Enumerable.Range(0, 16).Select(i => (byte)(0x30 + i)).ToArray();

    private static byte[] P2(int counter)
    {
        var d = new byte[] { 0, (byte)(0xC0 | counter), 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
        var input = d.Skip(1).Append(IdList[counter]).ToArray();
        d[0] = (byte)RefCrc(8, 0x2F, 0xFF, 0xFF, input);
        return d;
    }

    private static byte[] P5(int counter)
    {
        var d = new byte[] { 0, 0, (byte)counter, 0x10, 0x20, 0x30, 0x40, 0x50 };
        var input = d.Skip(2).Concat(new byte[] { 0x78, 0x56 }).ToArray(); // data ID 0x5678 little endian
        var crc = (ushort)RefCrc(16, 0x1021, 0xFFFF, 0, input);
        d[0] = (byte)(crc & 0xFF);
        d[1] = (byte)(crc >> 8);
        return d;
    }

    private static E2eFrame F(double t, byte[] d, uint id = 0x100) => new(1, id, false, t, d);

    private static E2eSummary Run(E2eConfig cfg, params E2eFrame[] frames)
    {
        var rep = E2eChecker.Check(frames, new[] { cfg });
        Assert.Null(rep.ConfigError);
        return Assert.Single(rep.Summaries);
    }

    [Fact]
    public void Profile1_OkWithWrap0To14()
    {
        var frames = new List<E2eFrame>();
        for (var i = 0; i < 31; i++) frames.Add(F(i * 0.01, P1(i % 15)));
        var s = Run(E2eConfig.P01(1, 0x100, false, Did), frames.ToArray());
        Assert.Equal(31, s.Ok);
        Assert.Equal(31, s.LongestOkRun);
        Assert.Null(s.FirstFailingFrameIndex);
    }

    [Fact]
    public void Profile1_CounterFifteenIsInvalid()
    {
        var s = Run(E2eConfig.P01(1, 0x100, false, Did), F(0, P1(14)), F(0.01, P1(15)));
        Assert.Equal(E2eStatus.InvalidCounter, s.Results[1].Status);
        Assert.Equal(1, s.FirstFailingFrameIndex);
    }

    [Fact]
    public void Profile1_DataIdModesLowHighAlt()
    {
        // Low mode: CRC over low data-ID byte only.
        var d = new byte[] { 0, 0xA3, 1, 2, 3, 4, 5, 6 };
        d[0] = (byte)RefCrc(8, 0x1D, 0xFF, 0xFF, new byte[] { 0x34 }.Concat(d.Skip(1)).ToArray());
        var cfg = E2eConfig.P01(1, 0x100, false, Did) with { P1DataIdMode = E2eP1DataIdMode.Low };
        Assert.Equal(1, Run(cfg, F(0, d)).Ok);
        // High mode.
        d[0] = (byte)RefCrc(8, 0x1D, 0xFF, 0xFF, new byte[] { 0x12 }.Concat(d.Skip(1)).ToArray());
        Assert.Equal(1, Run(cfg with { P1DataIdMode = E2eP1DataIdMode.High }, F(0, d)).Ok);
        // Alt: odd counter (3) -> high byte.
        Assert.Equal(1, Run(cfg with { P1DataIdMode = E2eP1DataIdMode.Alt }, F(0, d)).Ok);
        // Wrong mode must fail.
        Assert.Equal(1, Run(cfg with { P1DataIdMode = E2eP1DataIdMode.Both }, F(0, d)).WrongCrc);
    }

    [Fact]
    public void Profile1_WrongCrcDoesNotUpdateCounter()
    {
        var bad = P1(2); bad[2] ^= 0xFF; // corrupt payload, CRC stale
        var s = Run(E2eConfig.P01(1, 0x100, false, Did), F(0, P1(1)), F(0.01, bad), F(0.02, P1(2)));
        Assert.Equal(E2eStatus.WrongCrc, s.Results[1].Status);
        Assert.Equal(E2eStatus.Ok, s.Results[2].Status);
        Assert.Equal(1, s.FirstFailingFrameIndex);
        Assert.Equal(0.01, s.FirstFailingTimestamp);
        Assert.Equal(1, s.LongestOkRun);
    }

    [Fact]
    public void Repeated_LostAndWrongSequence()
    {
        var cfg = E2eConfig.P01(1, 0x100, false, Did) with { MaxDeltaCounter = 3 };
        var s = Run(cfg, F(0, P1(1)), F(0.01, P1(1)), F(0.02, P1(2)), F(0.03, P1(4)), F(0.04, P1(10)));
        Assert.Equal(new[] { E2eStatus.Ok, E2eStatus.Repeated, E2eStatus.Ok, E2eStatus.OkSomeLost, E2eStatus.WrongSequence },
            s.Results.Select(r => r.Status).ToArray());
        Assert.Equal(1, s.Results[3].LostFrames);
        Assert.Equal(1, s.TotalLostFrames);
        Assert.Equal(1, s.FirstFailingFrameIndex);
    }

    [Fact]
    public void LostFramesAcrossWrap_Profile1()
    {
        var cfg = E2eConfig.P01(1, 0x100, false, Did) with { MaxDeltaCounter = 2 };
        var s = Run(cfg, F(0, P1(13)), F(0.01, P1(0))); // 13 -> 0 is delta 2 on a 15-value cycle
        Assert.Equal(E2eStatus.OkSomeLost, s.Results[1].Status);
        Assert.Equal(1, s.Results[1].LostFrames);
    }

    [Fact]
    public void Profile2_OkWrapAt16AndDataIdListBySelectedCounter()
    {
        var frames = Enumerable.Range(0, 34).Select(i => F(i * 0.01, P2(i % 16))).ToArray();
        var s = Run(E2eConfig.P02(1, 0x100, false, IdList), frames);
        Assert.Equal(34, s.Ok);
        // Wrong list entry -> CRC failure.
        var other = IdList.ToArray(); other[5] ^= 1;
        Assert.Equal(1, Run(E2eConfig.P02(1, 0x100, false, other), F(0, P2(5))).WrongCrc);
    }

    [Fact]
    public void Profile5_OkWrapAt256_AndWrongCrc()
    {
        var frames = new List<E2eFrame> { F(0, P5(254)), F(0.01, P5(255)), F(0.02, P5(0)) };
        var cfg = E2eConfig.P05(1, 0x100, false, 0x5678);
        Assert.Equal(3, Run(cfg, frames.ToArray()).Ok);
        var bad = P5(1); bad[0] ^= 0x01;
        frames.Add(F(0.03, bad));
        var s = Run(cfg, frames.ToArray());
        Assert.Equal(1, s.WrongCrc);
        Assert.Equal(3, s.FirstFailingFrameIndex);
    }

    [Fact]
    public void Timeouts_AreDetectedFromCycleTime()
    {
        var cfg = E2eConfig.P01(1, 0x100, false, Did) with { CycleTimeSeconds = 0.01, TimeoutFactor = 2.0 };
        var s = Run(cfg, F(0.00, P1(1)), F(0.01, P1(2)), F(0.08, P1(3)), F(0.09, P1(4)));
        Assert.Equal(1, s.TimeoutCount);
        Assert.True(s.Results[2].TimeoutBefore);
        Assert.False(s.Results[1].TimeoutBefore);
        // Without cycle time nothing is flagged.
        Assert.Equal(0, Run(cfg with { CycleTimeSeconds = null }, F(0, P1(1)), F(1, P1(2))).TimeoutCount);
    }

    [Fact]
    public void OtherStreamsAreIgnoredAndShortFramesFlagged()
    {
        var cfg = E2eConfig.P01(1, 0x100, false, Did);
        var s = Run(cfg, F(0, P1(1), id: 0x200), F(0.01, P1(1)), F(0.02, new byte[] { 1, 2 }));
        Assert.Equal(2, s.FrameCount);
        Assert.Equal(E2eStatus.WrongLength, s.Results[1].Status);
        Assert.Equal(1, s.OtherErrors);
    }

    [Fact]
    public void InvalidConfigIsRejected()
    {
        var bad = E2eConfig.P01(1, 0x100, false, Did) with { CrcBitOffset = 3 };
        Assert.NotNull(E2eChecker.Check(Array.Empty<E2eFrame>(), new[] { bad }).ConfigError);
    }

    [Fact]
    public void SecOc_SplitsFieldsAndFlagsRepeatedAndBackwardFreshness()
    {
        // 2 payload bytes, 4-bit freshness, 12-bit MAC => 3 bytes... total 2*8+4+12 = 32 bits = 4 bytes.
        static byte[] Pdu(int fv, int mac) => new byte[] { 0xAA, 0xBB, (byte)((fv << 4) | ((mac >> 8) & 0xF)), (byte)(mac & 0xFF) };
        var frames = new List<(double, byte[])>
        {
            (0.0, Pdu(1, 0xABC)), (0.1, Pdu(2, 0x123)), (0.2, Pdu(2, 0x456)), // repeated
            (0.3, Pdu(15, 0x001)), // +13 modulo 16 => backward
            (0.4, Pdu(0, 0x002))   // wrap forward
        };
        var rep = SecOcInspector.Inspect(frames, new SecOcConfig(2, 4, 12));
        Assert.Null(rep.Error);
        Assert.False(rep.MacVerified);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, rep.Parsed[0].Payload);
        Assert.Equal(1UL, rep.Parsed[0].TruncatedFreshness);
        Assert.Equal(new byte[] { 0xAB, 0xC0 }, rep.Parsed[0].TruncatedMac);
        Assert.Equal(1, rep.RepeatedFreshness);
        Assert.Equal(1, rep.BackwardFreshness);
        Assert.Equal(2, rep.FirstAnomalyFrameIndex);
        Assert.Equal(1, rep.PossibleWraps);
    }

    [Fact]
    public void SecOc_ShortFramesAreIncomplete()
    {
        var rep = SecOcInspector.Inspect(new List<(double, byte[])> { (0, new byte[] { 1, 2, 3 }) }, new SecOcConfig(2, 8, 24));
        Assert.Equal(1, rep.Incomplete);
        Assert.False(rep.Parsed[0].Complete);
    }
}
