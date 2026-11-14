using System.Buffers.Binary;
using System.IO.Compression;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Tests;

/// <summary>Hand-built BLF byte streams for LIN / FlexRay objects. Layouts mirror the public vector_blf
/// definitions; no file written by a Vector tool was available.</summary>
internal static class BusFrameTestData
{
    public static byte[] Wrap(uint type, byte[] payload, ulong timestampNs = 1_000_000)
    {
        var body = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), timestampNs);
        payload.CopyTo(body.AsSpan(16));
        var obj = new byte[16 + body.Length];
        "LOBJ"u8.CopyTo(obj);
        BinaryPrimitives.WriteUInt16LittleEndian(obj.AsSpan(4), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(obj.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(obj.AsSpan(8), (uint)obj.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(obj.AsSpan(12), type);
        body.CopyTo(obj.AsSpan(16));
        return obj;
    }

    public static byte[] LinMessage(ushort channel, byte id, byte[] data, byte checksum, byte dir = 0, ulong ts = 1_000_000, uint type = 11)
    {
        var p = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(p, channel);
        p[2] = id; p[3] = (byte)data.Length;
        data.CopyTo(p.AsSpan(4));
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(16), checksum);
        p[18] = dir;
        return Wrap(type, p, ts);
    }

    public static byte[] LinMessage2(ushort channel, byte id, byte[] data, byte checksum, ulong ts = 1_000_000)
    {
        var p = new byte[152];
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), 19200);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(12), channel);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(16), 750_000);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(24), 50_000);
        p[37] = id; p[38] = (byte)data.Length;
        data.CopyTo(p.AsSpan(112));
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(120), checksum);
        p[122] = 1;
        return Wrap(57, p, ts);
    }

    public static byte[] LinSendError(ushort channel, byte id, uint type = 15)
    {
        var p = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(p, channel);
        p[2] = id; p[3] = 4; p[6] = 11; p[7] = 22;
        return Wrap(type, p);
    }

    public static byte[] LinReceiveError(ushort channel, byte id)
    {
        var p = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(p, channel);
        p[2] = id; p[3] = 2; p[8] = 3; p[9] = 0xAB;
        return Wrap(14, p);
    }

    public static byte[] LinWakeup(ushort channel) { var p = new byte[8]; p[0] = (byte)channel; p[2] = 1; return Wrap(21, p); }
    public static byte[] LinSleep(ushort channel) { var p = new byte[8]; p[0] = (byte)channel; p[2] = 2; p[3] = 0x10; return Wrap(20, p); }

    public static byte[] FlexRayV6(ushort channel, ushort slot, byte cycle, byte[] data, ushort state = 0, ulong ts = 1_000_000)
    {
        var p = new byte[32 + 64];
        BinaryPrimitives.WriteUInt16LittleEndian(p, channel);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(20), slot);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(22), 0x155);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(24), state);
        p[26] = (byte)data.Length; p[27] = cycle;
        data.CopyTo(p.AsSpan(32));
        return Wrap(41, p, ts);
    }

    public static byte[] FlexRayRcv(ushort channel, ushort mask, ushort slot, byte cycle, byte[] data, uint frameFlags, bool ex = false, ulong ts = 1_000_000)
    {
        var offset = ex ? 84 : 44;
        var p = new byte[offset + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(p, channel);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), mask);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(16), slot);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(18), 0x111);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(20), 0x222);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(22), (ushort)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(24), (ushort)data.Length);
        p[26] = cycle;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(36), frameFlags);
        if (ex) BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(44), 0xABCDEF);
        data.CopyTo(p.AsSpan(offset));
        return Wrap(ex ? 66u : 50u, p, ts);
    }

    public static byte[] FlexRayLegacy(bool sync, ushort channel, ushort slot, byte[] data, byte cycle = 0)
    {
        var p = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(p, channel);
        p[2] = 7; p[3] = (byte)data.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), slot);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), 0x2AA);
        data.CopyTo(p.AsSpan(12));
        if (sync) p[23] = cycle;
        return Wrap(sync ? 30u : 29u, p);
    }

    public static byte[] CanObject(uint id, byte[] data, ulong ts)
    {
        var p = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(p, 1);
        p[3] = (byte)data.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), id);
        data.CopyTo(p.AsSpan(8));
        return Wrap(1, p, ts);
    }

    public static byte[] Blf(params byte[][] objects)
    {
        using var stream = new MemoryStream();
        var header = new byte[144];
        "LOGG"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 144);
        stream.Write(header);
        foreach (var obj in objects)
        {
            stream.Write(obj);
            stream.Write(new byte[(4 - obj.Length % 4) % 4]);
        }
        return stream.ToArray();
    }

    public static byte[] BlfCompressed(params byte[][] objects)
    {
        using var inner = new MemoryStream();
        foreach (var obj in objects) { inner.Write(obj); inner.Write(new byte[(4 - obj.Length % 4) % 4]); }
        var raw = inner.ToArray();
        using var z = new MemoryStream();
        using (var zlib = new ZLibStream(z, CompressionMode.Compress, leaveOpen: true)) zlib.Write(raw);
        var packed = z.ToArray();
        var body = new byte[16 + packed.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), (uint)raw.Length);
        packed.CopyTo(body.AsSpan(16));
        var container = new byte[16 + body.Length];
        "LOBJ"u8.CopyTo(container);
        BinaryPrimitives.WriteUInt16LittleEndian(container.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(container.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(8), (uint)container.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(12), 10);
        body.CopyTo(container.AsSpan(16));
        return Blf(container);
    }

    public static LinFrame Lin(int index, byte id, byte[] data, byte? checksum = null, long ts = 0, int channel = 0,
        LinEventKind kind = LinEventKind.Message, bool protectedId = false) => new()
    {
        Channel = channel, TimestampNanoseconds = ts == 0 ? index * 10_000_000L : ts, RawIdByte = id, IdIsProtected = protectedId,
        DeclaredLength = data.Length, Data = data, Checksum = checksum, Event = kind
    };
}
