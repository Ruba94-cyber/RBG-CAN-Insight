using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RbgCanInsight.PerfBench;

/// <summary>
/// Deterministic synthetic CAN model. Frame i is a pure function of i, so the generator
/// computes the expected statistics, digest and decoded-value checksums WITHOUT using any
/// RbgCanInsight reader, writer or decoder (independent oracle).
/// Layout per 1000-frame block (j = i % 1000):
///   j 500..504  ISO-TP read of DID F190 (SF request, FF, FC, CF, CF) on ch0, 0x7E0 / 0x7E8
///   j 600..601  UDS DiagnosticSessionControl 10 03 + positive response 50 03 ...
///   j 700..701  UDS ReadDataByIdentifier DEAD + negative response 7F 22 31
///   j 250 when (i/1000)%100==0  bus error frame
///   otherwise   cyclic signal traffic over 40 message definitions (i % 40) on 2 channels.
/// </summary>
public readonly struct Rec
{
    public int Channel { get; init; }
    public uint Id { get; init; }
    public bool Ext { get; init; }
    public bool Fd { get; init; }
    public bool Brs { get; init; }
    public bool Err { get; init; }
    public bool Tx { get; init; }
    public int Len { get; init; }
    public long Ts { get; init; }
    public bool Signal { get; init; }
    public int Msg { get; init; } // signal message index 0..39, else -1
}

public static class Model
{
    public const long BaseTs = 1_000_000_000L;
    public const long Step = 100_000L;
    public const string Vin = "RBGBENCH";

    public static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    public static long Timestamp(long i) => BaseTs + i * Step + (i % 7) * 1_000L;
    public static string VinFor(long block) => Vin + block.ToString("D9");

    /// <summary>Number of complete blocks whose last frame (offset) lies inside n frames.</summary>
    public static long Blocks(long n, int lastOffset) =>
        n <= lastOffset ? 0 : (n - lastOffset - 1) / 1000 + 1;

    public static int Channel(long i) => (int)((i / 3) & 1);
    public static int MsgIndex(long i) => (int)(i % 40);

    public static void MessageShape(int m, out uint id, out bool ext, out bool fd, out bool brs, out int len)
    {
        ext = false; fd = false; brs = false; len = 8;
        if (m < 20) id = 0x100u + (uint)m * 0x10u;
        else if (m < 30) { id = 0x18FEF100u + (uint)(m - 20); ext = true; }
        else if (m < 35) { id = 0x300u + (uint)(m - 30); fd = true; brs = (m & 1) == 0; len = 16; }
        else if (m < 38) { id = 0x380u + (uint)(m - 35); fd = true; brs = true; len = m == 35 ? 32 : m == 36 ? 48 : 64; }
        else id = 0x500u + (uint)(m - 38);
    }

    public static bool IsDiagSlot(int j) => j is >= 500 and <= 504 or 600 or 601 or 700 or 701;

    /// <summary>Describe frame i and fill its payload (buffer of at least 64 bytes).</summary>
    public static Rec Describe(long i, Span<byte> payload)
    {
        var ts = Timestamp(i);
        var j = (int)(i % 1000);
        var block = i / 1000;
        if (IsDiagSlot(j))
        {
            payload[..8].Fill(0xAA);
            uint id;
            if (j == 500) { id = 0x7E0; Set(payload, 0x03, 0x22, 0xF1, 0x90); }
            else if (j == 501)
            {
                id = 0x7E8; var v = VinFor(block);
                Set(payload, 0x10, 0x14, 0x62, 0xF1, 0x90, (byte)v[0], (byte)v[1], (byte)v[2]);
            }
            else if (j == 502) { id = 0x7E0; Set(payload, 0x30, 0x00, 0x00); }
            else if (j == 503)
            {
                id = 0x7E8; var v = VinFor(block); payload[0] = 0x21;
                for (var k = 0; k < 7; k++) payload[1 + k] = (byte)v[3 + k];
            }
            else if (j == 504)
            {
                id = 0x7E8; var v = VinFor(block); payload[0] = 0x22;
                for (var k = 0; k < 7; k++) payload[1 + k] = (byte)v[10 + k];
            }
            else if (j == 600) { id = 0x7E0; Set(payload, 0x02, 0x10, 0x03); }
            else if (j == 601) { id = 0x7E8; Set(payload, 0x06, 0x50, 0x03, 0x00, 0x32, 0x01, 0xF4); }
            else if (j == 700) { id = 0x7E0; Set(payload, 0x03, 0x22, 0xDE, 0xAD); }
            else { id = 0x7E8; Set(payload, 0x03, 0x7F, 0x22, 0x31); }
            return new Rec { Channel = 0, Id = id, Len = 8, Ts = ts, Msg = -1, Tx = id == 0x7E0 };
        }
        if (j == 250 && block % 100 == 0)
            return new Rec { Channel = (int)(block & 1), Err = true, Ts = ts, Msg = -1 };

        var m = MsgIndex(i);
        MessageShape(m, out var mid, out var ext, out var fd, out var brs, out var l);
        var h = Mix((ulong)i);
        for (var k = 0; k < l; k += 8)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(payload[k..], h);
            h = Mix(h);
        }
        payload[0] |= 0x30; // keep PCI nibble >= 3 so background traffic is never an ISO-TP candidate
        return new Rec
        {
            Channel = Channel(i), Id = mid, Ext = ext, Fd = fd, Brs = brs, Len = l, Ts = ts,
            Tx = i % 5 == 0, Signal = true, Msg = m
        };
    }

    private static void Set(Span<byte> p, params byte[] b)
    {
        for (var k = 0; k < b.Length; k++) p[k] = b[k];
    }

    public static byte Flags(in Rec r) =>
        (byte)((r.Ext ? 1 : 0) | (r.Err ? 4 : 0) | (r.Fd ? 8 : 0) | (r.Brs && r.Fd ? 16 : 0) | (r.Tx ? 32 : 0));

    /// <summary>Same canonical digest as tools/RbgCanInsight.TraceVerify.</summary>
    public static void Digest(IncrementalHash h, Span<byte> header, in Rec r, ReadOnlySpan<byte> payload)
    {
        BinaryPrimitives.WriteInt32LittleEndian(header, r.Channel);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], r.Id);
        BinaryPrimitives.WriteInt64LittleEndian(header[8..], r.Ts);
        header[16] = Flags(r);
        header[17] = (byte)r.Len;
        h.AppendData(header[..18]);
        h.AppendData(payload[..r.Len]);
    }

    /// <summary>Independent bit math for the four DBC signals defined in <see cref="DbcText"/>.</summary>
    public static void DecodeRaw(ReadOnlySpan<byte> p, out long a, out long b, out long c, out long d)
    {
        a = p[0] | (p[1] << 8);
        b = p[2];
        var c12 = p[3] | ((p[4] & 0x0F) << 8);
        c = c12 >= 0x800 ? c12 - 0x1000 : c12;
        d = (p[5] << 8) | p[6];
    }

    public static string DbcText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("VERSION \"\"\n\nNS_ :\n\nBS_:\n\nBU_: BenchEcu\n");
        for (var m = 0; m < 35; m++)
        {
            MessageShape(m, out var id, out var ext, out _, out _, out var len);
            var dbcId = ext ? id | 0x80000000u : id;
            sb.AppendLine($"BO_ {dbcId} Msg{m:D2}: {len} BenchEcu");
            sb.AppendLine(" SG_ SigA : 0|16@1+ (0.25,-100) [0|0] \"\" Vector__XXX");
            sb.AppendLine(" SG_ SigB : 16|8@1+ (1,0) [0|0] \"\" Vector__XXX");
            sb.AppendLine(" SG_ SigC : 24|12@1- (0.5,0) [0|0] \"\" Vector__XXX");
            sb.AppendLine(" SG_ SigD : 47|16@0+ (1,0) [0|0] \"\" Vector__XXX");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
