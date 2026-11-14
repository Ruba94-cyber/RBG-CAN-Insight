using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace RbgCanInsight.PerfBench;

/// <summary>Writes ASC + BLF + DBC + expected.json for N frames. Independent of the app's readers/writers.</summary>
public static class Gen
{
    public static int Run(string folder, long n)
    {
        Directory.CreateDirectory(folder);
        var tag = n % 1_000_000 == 0 ? $"{n / 1_000_000}M" : n.ToString();
        var asc = Path.Combine(folder, $"trace-{tag}.asc");
        var blf = Path.Combine(folder, $"trace-{tag}.blf");
        File.WriteAllText(Path.Combine(folder, "bench.dbc"), Model.DbcText());
        var sw = Stopwatch.StartNew();

        using var ascOut = new BufferedStream(new FileStream(asc, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16), 1 << 20);
        using var blfOut = new FileStream(blf, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        WriteAscHeader(ascOut);
        var blfWriter = new BlfWriter(blfOut);

        var payload = new byte[64];
        var line = new byte[512];
        var header = new byte[18];
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long fd = 0, brsCount = 0, err = 0, ext = 0, tx = 0;
        var chCount = new long[2];
        long first = long.MaxValue, last = long.MinValue;
        var ids = new HashSet<(int, uint, bool)>();
        long dbcFrames = 0, dbcValues = 0, dbcRaw = 0;
        double dbcPhys = 0;

        for (long i = 0; i < n; i++)
        {
            payload.AsSpan().Clear();
            var r = Model.Describe(i, payload);
            var len = AscLine(line, r, payload);
            ascOut.Write(line, 0, len);
            blfWriter.Add(r, payload);
            Model.Digest(digest, header, r, payload);

            if (r.Fd) fd++;
            if (r.Fd && r.Brs) brsCount++;
            if (r.Err) err++;
            if (r.Ext) ext++;
            if (r.Tx) tx++;
            chCount[r.Channel]++;
            if (r.Ts < first) first = r.Ts;
            if (r.Ts > last) last = r.Ts;
            ids.Add((r.Channel, r.Id, r.Ext));
            if (r.Signal && r.Msg < 35)
            {
                Model.DecodeRaw(payload, out var a, out var b, out var c, out var d);
                dbcFrames++; dbcValues += 4; dbcRaw += a + b + c + d;
                dbcPhys += a * 0.25 - 100 + b + c * 0.5 + d;
            }
        }
        blfWriter.Finish();

        long reqSum = 0, respSum = 0;
        var vinBlocks = Model.Blocks(n, 504);
        var sessBlocks = Model.Blocks(n, 601);
        var nrcBlocks = Model.Blocks(n, 701);
        for (long b = 0; b < vinBlocks; b++)
        {
            reqSum += 0x22 + 0xF1 + 0x90;
            respSum += 0x62 + 0xF1 + 0x90;
            foreach (var ch in Model.VinFor(b)) respSum += (byte)ch;
        }
        reqSum += sessBlocks * (0x10 + 0x03) + nrcBlocks * (0x22 + 0xDE + 0xAD);
        respSum += sessBlocks * (0x50 + 0x03 + 0x00 + 0x32 + 0x01 + 0xF4) + nrcBlocks * (0x7F + 0x22 + 0x31);

        var expected = new
        {
            Frames = n,
            FirstTimestampNs = first,
            LastTimestampNs = last,
            Channel0 = chCount[0],
            Channel1 = chCount[1],
            CanFd = fd,
            BitRateSwitch = brsCount,
            Errors = err,
            Extended = ext,
            Transmit = tx,
            UniqueChannelIds = ids.Count,
            CanonicalSha256 = Convert.ToHexString(digest.GetHashAndReset()),
            DbcDecodedFrames = dbcFrames,
            DbcSignalValues = dbcValues,
            DbcRawSum = dbcRaw,
            DbcPhysicalSum = dbcPhys,
            UdsTransactions = vinBlocks + sessBlocks + nrcBlocks,
            UdsVinReads = vinBlocks,
            UdsSessionControls = sessBlocks,
            UdsNegativeResponses = nrcBlocks,
            UdsRequestByteSum = reqSum,
            UdsResponseByteSum = respSum
        };
        File.WriteAllText(Path.Combine(folder, $"expected-{tag}.json"),
            JsonSerializer.Serialize(expected, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"generated {n:N0} frames in {sw.Elapsed.TotalSeconds:F1}s: asc={new FileInfo(asc).Length / 1048576} MB blf={new FileInfo(blf).Length / 1048576} MB");
        return 0;
    }

    private static void WriteAscHeader(Stream s)
    {
        var text = "date Mon Nov 10 12:00:00.000 am 2026\r\nbase hex  timestamps absolute\r\ninternal events logged\r\n";
        s.Write(System.Text.Encoding.ASCII.GetBytes(text));
    }

    private static readonly byte[] Hex = "0123456789ABCDEF"u8.ToArray();

    private static int Put(byte[] b, int p, long v)
    {
        Span<byte> tmp = stackalloc byte[20];
        var n = 0;
        do { tmp[n++] = (byte)('0' + v % 10); v /= 10; } while (v > 0);
        while (n > 0) b[p++] = tmp[--n];
        return p;
    }

    private static int PutHex(byte[] b, int p, uint v, int minDigits)
    {
        var digits = Math.Max(minDigits, v == 0 ? 1 : (32 - System.Numerics.BitOperations.LeadingZeroCount(v) + 3) / 4);
        for (var k = digits - 1; k >= 0; k--) b[p++] = Hex[(v >> (4 * k)) & 0xF];
        return p;
    }

    private static int PutStr(byte[] b, int p, string s)
    {
        foreach (var c in s) b[p++] = (byte)c;
        return p;
    }

    private static byte DlcCode(int len) => len switch
    {
        <= 8 => (byte)len, <= 12 => 9, <= 16 => 10, <= 20 => 11, <= 24 => 12, <= 32 => 13, <= 48 => 14, _ => 15
    };

    private static int AscLine(byte[] b, in Rec r, ReadOnlySpan<byte> payload)
    {
        var p = 0;
        p = Put(b, p, r.Ts / 1_000_000_000L);
        b[p++] = (byte)'.';
        var us = r.Ts % 1_000_000_000L / 1000;
        for (var d = 100_000; d > 0; d /= 10) b[p++] = (byte)('0' + us / d % 10);
        b[p++] = (byte)' ';
        if (r.Err)
        {
            p = Put(b, p, r.Channel + 1);
            p = PutStr(b, p, " ErrorFrame\r\n");
            return p;
        }
        if (r.Fd)
        {
            p = PutStr(b, p, "CANFD ");
            p = Put(b, p, r.Channel + 1);
            p = PutStr(b, p, r.Tx ? " Tx " : " Rx ");
            p = PutHex(b, p, r.Id, 3);
            b[p++] = (byte)' ';
            b[p++] = (byte)(r.Brs ? '1' : '0');
            b[p++] = (byte)' ';
            b[p++] = (byte)'0';
            b[p++] = (byte)' ';
            b[p++] = Hex[DlcCode(r.Len)];
            b[p++] = (byte)' ';
            p = Put(b, p, r.Len);
        }
        else
        {
            p = Put(b, p, r.Channel + 1);
            b[p++] = (byte)' ';
            p = PutHex(b, p, r.Id, r.Ext ? 8 : 3);
            if (r.Ext) b[p++] = (byte)'x';
            p = PutStr(b, p, r.Tx ? " Tx d " : " Rx d ");
            p = Put(b, p, r.Len);
        }
        for (var k = 0; k < r.Len; k++)
        {
            b[p++] = (byte)' ';
            b[p++] = Hex[payload[k] >> 4];
            b[p++] = Hex[payload[k] & 0xF];
        }
        b[p++] = (byte)'\r';
        b[p++] = (byte)'\n';
        return p;
    }

    /// <summary>Minimal Vector BLF writer: zlib-compressed LOG_CONTAINERs of CAN_MESSAGE (1),
    /// CAN_FD_MESSAGE (100) and CAN_ERROR_EXT (73) objects, nanosecond timestamps.</summary>
    private sealed class BlfWriter
    {
        private const int ContainerLimit = 128 * 1024;
        private readonly FileStream _out;
        private readonly byte[] _buf = new byte[ContainerLimit + 256];
        private int _len;
        private ulong _uncompressed = 144;
        private uint _objects;

        public BlfWriter(FileStream output)
        {
            _out = output;
            var h = new byte[144];
            BinaryPrimitives.WriteUInt32LittleEndian(h, 0x47474F4C);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), 144);
            h[8] = 5; h[12] = 2; h[13] = 6;
            _out.Write(h);
        }

        public void Add(in Rec r, ReadOnlySpan<byte> payload)
        {
            if (_len + 128 > ContainerLimit) Flush();
            var o = _buf.AsSpan(_len);
            var type = r.Err ? 73u : r.Fd ? 100u : 1u;
            var body = r.Err ? 32 : r.Fd ? 84 : 16;
            var size = 32 + body;
            o[..size].Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(o, 0x4A424F4C);
            BinaryPrimitives.WriteUInt16LittleEndian(o[4..], 32);
            BinaryPrimitives.WriteUInt16LittleEndian(o[6..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(o[8..], (uint)size);
            BinaryPrimitives.WriteUInt32LittleEndian(o[12..], type);
            BinaryPrimitives.WriteUInt32LittleEndian(o[16..], 2); // flags: nanosecond ticks
            BinaryPrimitives.WriteUInt64LittleEndian(o[24..], (ulong)r.Ts);
            var p = o[32..];
            var rawId = r.Id | (r.Ext ? 0x8000_0000u : 0);
            BinaryPrimitives.WriteUInt16LittleEndian(p, (ushort)(r.Channel + 1));
            if (r.Err)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(p[16..], 0);
            }
            else if (r.Fd)
            {
                if (r.Tx) p[2] = 1;
                p[3] = DlcCode(r.Len);
                BinaryPrimitives.WriteUInt32LittleEndian(p[4..], rawId);
                p[13] = (byte)(1 | (r.Brs ? 2 : 0));
                p[14] = (byte)r.Len;
                payload[..r.Len].CopyTo(p[20..]);
            }
            else
            {
                if (r.Tx) p[2] = 1;
                p[3] = (byte)r.Len;
                BinaryPrimitives.WriteUInt32LittleEndian(p[4..], rawId);
                payload[..r.Len].CopyTo(p[8..]);
            }
            _len += size;
            _objects++;
        }

        private void Flush()
        {
            if (_len == 0) return;
            using var ms = new MemoryStream(_len / 2 + 64);
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                z.Write(_buf, 0, _len);
            var comp = ms.GetBuffer().AsSpan(0, (int)ms.Length);
            var objectSize = 32 + comp.Length;
            var h = new byte[32];
            BinaryPrimitives.WriteUInt32LittleEndian(h, 0x4A424F4C);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), (uint)objectSize);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), 10);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(16), 2); // zlib
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), (uint)_len);
            _out.Write(h);
            _out.Write(comp);
            var pad = objectSize % 4;
            if (pad != 0) _out.Write(new byte[pad]);
            _uncompressed += (ulong)(32 + _len);
            _len = 0;
        }

        public void Finish()
        {
            Flush();
            var sizes = new byte[20];
            BinaryPrimitives.WriteUInt64LittleEndian(sizes, (ulong)_out.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(sizes.AsSpan(8), _uncompressed);
            BinaryPrimitives.WriteUInt32LittleEndian(sizes.AsSpan(16), _objects);
            _out.Position = 16;
            _out.Write(sizes);
            _out.Flush();
        }
    }
}
