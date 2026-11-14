using RbgCanInsight.Analysis.Buses;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;
using static RbgCanInsight.Tests.BusFrameTestData;

namespace RbgCanInsight.Tests;

/// <summary>Import of LIN / FlexRay objects from hand-built BLF streams (no real Vector file was available).</summary>
public sealed class BusFrameImportTests
{
    private static async Task<TraceImportResult> Import(byte[] blf)
    {
        var path = Path.Combine(Path.GetTempPath(), "bus-" + Guid.NewGuid().ToString("N") + ".blf");
        await File.WriteAllBytesAsync(path, blf);
        try { return await TraceImporter.ReadAllAsync(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ImportsLinMessagesAndKeepsCanPipelineUntouched()
    {
        var data = new byte[] { 1, 2 };
        var enhanced = LinProtocol.Checksum(data, LinProtocol.ProtectedId(0x10));
        var result = await Import(Blf(
            CanObject(0x123, [9, 8], 500_000),
            LinMessage(1, 0x10, data, enhanced, ts: 1_000_000),
            LinMessage2(2, 0x11, [5], LinProtocol.Checksum([5], null), ts: 2_000_000)));

        var can = Assert.Single(result.CanFrames);
        Assert.Equal(0x123u, can.Identifier);
        Assert.Equal(2, result.BusFrames.Lin.Count);
        var lin = result.BusFrames.Lin[0];
        Assert.Equal(0x10, lin.FrameId);
        Assert.Equal(0, lin.Channel);
        Assert.Equal(data, lin.Data);
        Assert.Equal(BusDirection.Rx, lin.Direction);
        var lin2 = result.BusFrames.Lin[1];
        Assert.Equal(1, lin2.Channel);
        Assert.Equal(19200u, lin2.Baudrate);
        Assert.Equal(750_000, lin2.BreakLengthNs);
        Assert.Equal(BusDirection.Tx, lin2.Direction);
        Assert.Contains("2 LIN frames imported", result.Health.Summary);
        Assert.Equal(2, result.Health.LinFrames);
    }

    [Fact]
    public async Task ImportsLinFromCompressedContainer()
    {
        var result = await Import(BlfCompressed(LinMessage(1, 0x20, [1], 0xAA), FlexRayV6(1, 3, 0, [1, 2])));
        Assert.Single(result.BusFrames.Lin);
        Assert.Single(result.BusFrames.FlexRay);
        Assert.Equal("1 LIN frames, 1 FlexRay frames imported", result.BusFrames.Summary());
    }

    [Fact]
    public async Task ImportsLinEvents()
    {
        var result = await Import(Blf(
            LinMessage(1, 0x21, [1, 2, 3], 0x11, type: 12),
            LinSendError(1, 0x22), LinReceiveError(1, 0x23), LinWakeup(1), LinSleep(1)));
        var kinds = result.BusFrames.Lin.Select(f => f.Event).ToArray();
        Assert.Equal([LinEventKind.CrcError, LinEventKind.SendError, LinEventKind.ReceiveError, LinEventKind.Wakeup, LinEventKind.Sleep], kinds);
        Assert.Contains("0xAB", result.BusFrames.Lin[2].Detail);
        Assert.Equal(0, result.Health.RejectedRecords);
    }

    [Fact]
    public async Task ImportsFlexRayObjects()
    {
        var result = await Import(Blf(
            FlexRayV6(1, 10, 5, [1, 2, 3, 4], state: 0x02 | 0x10),
            FlexRayRcv(1, 2, 20, 6, [9, 9], frameFlags: 0x04 | 0x08 | 0x100000),
            FlexRayRcv(1, 3, 21, 7, [7], frameFlags: 0x01 | 0x40, ex: true),
            FlexRayLegacy(false, 1, 30, [1, 2, 3]),
            FlexRayLegacy(true, 1, 31, [4, 5], cycle: 12)));
        var fr = result.BusFrames.FlexRay;
        Assert.Equal(5, fr.Count);
        Assert.Equal(10, fr[0].Slot); Assert.Equal(5, fr[0].Cycle);
        Assert.True((fr[0].Flags & FlexRayFlags.Sync) != 0 && (fr[0].Flags & FlexRayFlags.Startup) != 0);
        Assert.Equal(FlexRayChannel.B, fr[1].FlexRayChannel);
        Assert.True(fr[1].IsDynamic);
        Assert.Equal(0x111, fr[1].HeaderCrc);
        Assert.Equal(FlexRayChannel.AB, fr[2].FlexRayChannel);
        Assert.True(fr[2].IsNull && fr[2].IsError);
        Assert.Equal(0xABCDEFu, fr[2].FrameCrc);
        Assert.Equal(-1, fr[3].Cycle);
        Assert.Equal(12, fr[4].Cycle);
        Assert.Equal(new byte[] { 4, 5 }, fr[4].Payload);
    }

    [Fact]
    public async Task MalformedBusObjectsAreRejectedAndCounted()
    {
        var shortLin = Wrap(11, new byte[6]);
        var badDlc = LinMessage(1, 1, [1], 0);
        badDlc[16 + 16 + 3] = 9; // dlc 9 is impossible for LIN
        var result = await Import(Blf(shortLin, badDlc, Wrap(41, new byte[10]), Wrap(66, new byte[40])));
        Assert.Empty(result.BusFrames.Lin);
        Assert.Empty(result.BusFrames.FlexRay);
        Assert.Equal(4, result.Health.RejectedRecords);
    }

    [Fact]
    public async Task UnknownAndCanXlObjectTypesStayCountedAsUnsupported()
    {
        var result = await Import(Blf(Wrap(18, new byte[16]), Wrap(0x7777, new byte[40])));
        Assert.Equal(2, result.Health.UnsupportedObjectRecords);
        Assert.Equal(0, result.BusFrames.Total);
        Assert.Equal("", result.BusFrames.Summary());
    }

    [Fact]
    public async Task TruncatedFilesNeverCrashWithUnexpectedExceptions()
    {
        var file = Blf(LinMessage(1, 0x10, [1, 2], 5), LinMessage2(1, 0x11, [3], 2), FlexRayV6(1, 1, 1, [1]), FlexRayRcv(1, 1, 2, 2, [1], 0, true));
        for (var length = 0; length < file.Length; length += 3)
        {
            try { await Import(file[..length]); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { }
        }
    }

    [Fact]
    public async Task BitFlippedFilesNeverCrashWithUnexpectedExceptions()
    {
        var original = Blf(LinMessage(1, 0x10, [1, 2], 5), LinMessage2(1, 0x11, [3], 2), FlexRayV6(1, 1, 1, [1]),
            FlexRayRcv(1, 1, 2, 2, [1], 0, true), LinSendError(1, 3), LinReceiveError(1, 4));
        var random = new Random(1234);
        for (var round = 0; round < 300; round++)
        {
            var copy = (byte[])original.Clone();
            for (var k = 0; k < 4; k++) copy[144 + random.Next(copy.Length - 144)] = (byte)random.Next(256);
            try { await Import(copy); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or NotSupportedException or OverflowException) { }
        }
    }

    [Fact]
    public async Task AscLinFlexRayAndXlLinesAreCountedNotRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), "bus-" + Guid.NewGuid().ToString("N") + ".asc");
        await File.WriteAllTextAsync(path,
            "date Mon Jan 1 00:00:00 2024\nbase hex  timestamps absolute\n" +
            "   0.100000 1  123  Rx   d 2 11 22\n" +
            "   0.200000 Lin 1 something\n   0.300000 FlexRay 1 something\n   0.400000 1 CANXL something\n" +
            "   0.500000 garbage line\n");
        try
        {
            var result = await TraceImporter.ReadAllAsync(path);
            Assert.Single(result.CanFrames);
            Assert.Equal(3, result.Health.UnsupportedObjectRecords);
            Assert.Equal(1, result.Health.RejectedRecords); // only the garbage line
            Assert.Equal(0, result.BusFrames.Total);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CanXlFrameValidatesLimits()
    {
        var frame = new CanXlFrame { PriorityId = 0x7FF, Vcid = 3, Sdt = 5, Sec = true, AcceptanceField = 0x11223344, Payload = new byte[2048] };
        Assert.Equal(2048, frame.Payload.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanXlFrame { PriorityId = 0x800 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanXlFrame { Payload = new byte[2049] });
        var table = BusReportTables.CanXlList([frame]);
        Assert.Equal("0x7FF", table.Rows[0][3]);
        Assert.Equal("0x05", table.Rows[0][5]);
    }
}
