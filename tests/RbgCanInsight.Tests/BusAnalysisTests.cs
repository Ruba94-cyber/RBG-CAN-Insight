using RbgCanInsight.Analysis.Buses;
using RbgCanInsight.Domain;
using Xunit;
using static RbgCanInsight.Tests.BusFrameTestData;

namespace RbgCanInsight.Tests;

/// <summary>LIN / FlexRay / LDF decoders on hand-built frames. Expected values are from the LIN 2.x parity and
/// checksum rules computed by hand, not from a reference tool.</summary>
public sealed class BusAnalysisTests
{
    [Theory]
    [InlineData(0x00, 0x80)]
    [InlineData(0x01, 0xC1)]
    [InlineData(0x02, 0x42)]
    [InlineData(0x3C, 0x3C)]
    [InlineData(0x3D, 0x7D)]
    public void ProtectedIdParity(int id, int pid)
    {
        Assert.Equal((byte)pid, LinProtocol.ProtectedId((byte)id));
        Assert.True(LinProtocol.IsValidProtectedId((byte)pid));
        Assert.False(LinProtocol.IsValidProtectedId((byte)(pid ^ 0x40)));
    }

    [Fact]
    public void ClassicChecksumFoldsCarry()
    {
        // 0x4A+0x55=0x9F; +0x93=0x132 -> 0x33; +0xE5=0x118 -> 0x19; inverted 0xE6
        Assert.Equal(0xE6, LinProtocol.Checksum([0x4A, 0x55, 0x93, 0xE5], null));
    }

    [Fact]
    public void ChecksumModelsAndDiagnosticRuleAreReported()
    {
        byte[] d = [0x10, 0x20];
        var classic = LinProtocol.Checksum(d, null);
        var enhanced = LinProtocol.Checksum(d, LinProtocol.ProtectedId(0x05));
        var frames = new[]
        {
            Lin(0, 0x05, d, classic), Lin(1, 0x05, d, enhanced), Lin(2, 0x05, d, 0x00),
            Lin(3, 0x3C, d, LinProtocol.Checksum(d, LinProtocol.ProtectedId(0x3C))),
            Lin(4, 0x05, d, null, protectedId: false, kind: LinEventKind.Wakeup),
            Lin(5, 0xC1, d, classic, protectedId: true), // PID of 0x01 is 0xC1 -> ok
            Lin(6, 0x81, d, classic, protectedId: true), // wrong parity
        };
        var checks = LinFrameAnalyzer.Check(frames);
        Assert.Equal(LinChecksumStatus.ClassicOnly, checks[0].Checksum);
        Assert.Equal(LinChecksumStatus.EnhancedOnly, checks[1].Checksum);
        Assert.Equal(LinChecksumStatus.Mismatch, checks[2].Checksum);
        Assert.Contains("must use the classic", checks[3].Verdict);
        Assert.Equal(LinPidStatus.NotChecked, checks[0].Pid);
        Assert.Equal(LinPidStatus.Valid, checks[5].Pid);
        Assert.Equal(LinPidStatus.Invalid, checks[6].Pid);
        Assert.Contains("parity wrong", checks[6].Verdict);
    }

    [Fact]
    public void TruncatedDataIsNotChecksummed()
    {
        var f = Lin(0, 5, [1], 0x11) with { DeclaredLength = 4 };
        Assert.Equal(LinChecksumStatus.Truncated, LinFrameAnalyzer.Check([f])[0].Checksum);
    }

    [Fact]
    public void TimingReportsPeriodAndJitter()
    {
        var frames = new[]
        {
            Lin(0, 5, [1], null, ts: 0 + 1), Lin(1, 6, [1], null, ts: 5_000_000),
            Lin(2, 5, [1], null, ts: 10_000_001), Lin(3, 5, [1], null, ts: 32_000_001),
        };
        var t = Assert.Single(LinFrameAnalyzer.Timing(frames), x => x.FrameId == 5);
        Assert.Equal(3, t.Count);
        Assert.Equal(10.0, t.MinMs, 3);
        Assert.Equal(22.0, t.MaxMs, 3);
        Assert.Equal(16.0, t.MeanMs, 3);
        Assert.Equal(0, t.FirstIndex);
        Assert.Equal(3, t.LastIndex);
    }

    private static LinFrame Diag(int i, byte id, params byte[] d) => Lin(i, id, d, 0);

    [Fact]
    public void DecodesNodeConfigurationAndSingleFrames()
    {
        var frames = new[]
        {
            Diag(0, 0x3C, 0x7F, 0x06, 0xB0, 0x34, 0x12, 0x78, 0x56, 0x20),       // assign NAD
            Diag(1, 0x3C, 0x20, 0x06, 0xB2, 0x00, 0x34, 0x12, 0xFF, 0x7F),       // read by id 0
            Diag(2, 0x3D, 0x20, 0x06, 0xF2, 0x34, 0x12, 0x78, 0x56, 0x03),       // positive response
            Diag(3, 0x3D, 0x20, 0x03, 0x7F, 0xB2, 0x12, 0xFF, 0xFF, 0xFF),       // negative response
            Diag(4, 0x3C, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF),       // go to sleep
        };
        var m = LinDiagnosticsDecoder.Decode(frames);
        Assert.Equal(5, m.Count);
        Assert.Contains("Assign NAD", m[0].Decoded);
        Assert.Contains("supplier 0x1234, function 0x5678, new NAD 0x20", m[0].Decoded);
        Assert.Contains("Read by identifier", m[1].Decoded);
        Assert.Contains("identifier 0, supplier 0x1234", m[1].Decoded);
        Assert.Contains("positive response", m[2].Decoded);
        Assert.Contains("variant 3", m[2].Decoded);
        Assert.Contains("NRC 0x12 (subFunctionNotSupported)", m[3].Decoded);
        Assert.Equal(LinDiagStatus.GoToSleep, m[4].Status);
        Assert.All(m, x => Assert.Single(x.FrameIndices));
    }

    [Fact]
    public void ReassemblesMultiFrameUdsOnLin()
    {
        // ReadDataByIdentifier response 62 F1 90 + 8 data bytes = 11 bytes: FF carries 5, CF1 6
        var frames = new[]
        {
            Diag(0, 0x3D, 0x10, 0x10, 0x0B, 0x62, 0xF1, 0x90, 0x01, 0x02),
            Diag(1, 0x3D, 0x10, 0x21, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08),
        };
        var m = Assert.Single(LinDiagnosticsDecoder.Decode(frames));
        Assert.Equal(LinDiagStatus.Complete, m.Status);
        Assert.Equal(11, m.Payload.Length);
        Assert.Equal(new[] { 0, 1 }, m.FrameIndices);
        Assert.Contains("ReadDataByIdentifier (positive response)", m.Decoded);
    }

    [Fact]
    public void ReportsSequenceErrorsOrphansAndIncompleteTransfers()
    {
        var frames = new[]
        {
            Diag(0, 0x3D, 0x10, 0x10, 0x0B, 0x62, 0xF1, 0x90, 0x01, 0x02),
            Diag(1, 0x3D, 0x10, 0x22, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08), // seq 2 instead of 1
            Diag(2, 0x3D, 0x10, 0x21, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08), // orphan
            Diag(3, 0x3C, 0x10, 0x10, 0x20, 0x2E, 0xF1, 0x90, 0x01, 0x02), // never finished
            Lin(4, 0x3C, [1, 2], 0),                                       // bad diagnostic frame
        };
        var m = LinDiagnosticsDecoder.Decode(frames);
        Assert.Contains(m, x => x.Status == LinDiagStatus.SequenceError && x.Decoded.Contains("expected sequence 1, got 2"));
        Assert.Contains(m, x => x.Status == LinDiagStatus.OrphanConsecutive);
        Assert.Contains(m, x => x.Status == LinDiagStatus.Incomplete && x.Direction == LinDiagDirection.Request);
        Assert.Contains(m, x => x.Status == LinDiagStatus.BadFrame);
    }

    private const string Ldf = """
        LIN_description_file;
        LIN_protocol_version = "2.1";
        LIN_speed = 19.2 kbps;
        /* comment */
        Nodes { Master: ECU, 5 ms, 0.1 ms; Slaves: Door, Seat; }
        Signals {
          Speed: 8, 0, Door, ECU;   // km/h
          Mode: 2, 0, Door, ECU;
          Temp: 8, 0, Seat, ECU;
        }
        Frames {
          DoorStatus: 0x10, Door, 2 { Speed, 0; Mode, 8; }
          SeatStatus: 17, Seat, 1 { Temp, 0; }
        }
        Event_triggered_frames { Ev: Tbl, 0x20, DoorStatus; }
        Signal_encoding_types {
          SpeedEnc { physical_value, 0, 250, 0.5, 0, "km/h"; }
          ModeEnc { logical_value, 0, "off"; logical_value, 1, "on"; }
        }
        Signal_representation { SpeedEnc: Speed; ModeEnc: Mode; }
        """;

    [Fact]
    public void ParsesLdfAndDecodesSignals()
    {
        var ldf = LdfParser.Parse(Ldf);
        Assert.Equal("2.1", ldf.ProtocolVersion);
        Assert.Equal(19.2, ldf.SpeedKbps);
        Assert.Equal("ECU", ldf.MasterNode);
        Assert.Equal(2, ldf.Frames.Count);
        Assert.Equal(0x10, ldf.Frames[0].Id);
        Assert.Equal(17, ldf.Frames[1].Id);
        Assert.Equal(8, ldf.Frames[0].Signals[1].OffsetBits);

        byte[] a = [100, 0x01], b = [50, 0x00];
        var frames = new[]
        {
            Lin(0, 0x10, a, LinProtocol.Checksum(a, LinProtocol.ProtectedId(0x10))),
            Lin(1, 0x10, b, LinProtocol.Checksum(b, LinProtocol.ProtectedId(0x10))),
            Lin(2, 0x11, [33], LinProtocol.Checksum([33], LinProtocol.ProtectedId(0x11))),
            Lin(3, 0x30, [1], LinProtocol.Checksum([1], null)),             // not in LDF
            Lin(4, 0x10, [1], 0x00),                                        // wrong checksum
            Lin(5, 0x10, [1, 2, 3], LinProtocol.Checksum([1, 2, 3], null)), // checks classic; length mismatch
        };
        var r = LdfSignalDecoder.Decode(ldf, frames);
        Assert.Equal(3, r.FramesDecoded);
        Assert.Equal(1, r.FramesNotInLdf);
        Assert.Equal(1, r.FramesSkippedForErrors);
        Assert.Equal(1, r.FramesLengthMismatch);
        var speed = r.Signals.Single(s => s.Signal == "Speed");
        Assert.Equal(2, speed.Count);
        Assert.Equal(25.0, speed.Min);
        Assert.Equal(50.0, speed.Max);
        Assert.Equal("km/h", speed.Unit);
        Assert.Equal("off (raw 0)", r.Signals.Single(s => s.Signal == "Mode").LastValue);
        Assert.Equal(33, r.Signals.Single(s => s.Signal == "Temp").LastRaw);
        Assert.Equal(0, speed.FirstIndex);
        Assert.Equal(1, speed.LastIndex);
    }

    [Fact]
    public void LdfNameColumnAndSummaryRowAppearInTables()
    {
        var ldf = LdfParser.Parse(Ldf);
        byte[] a = [1, 1];
        var frames = new[] { Lin(0, 0x10, a, LinProtocol.Checksum(a, LinProtocol.ProtectedId(0x10))) };
        var table = BusReportTables.LinFrames(frames, ldf);
        Assert.Equal("DoorStatus", table.Rows[0][12]);
        Assert.Equal("(summary)", BusReportTables.LinSignals(ldf, frames).Rows[^1][0]);
    }

    [Fact]
    public void MalformedAndTruncatedLdfOnlyThrowFormatException()
    {
        foreach (var bad in new[] { "Signals { a: 8, 0, ", "Frames { f: 1, M, 2 { s, 0 } }", "/* open", "x \"open", "Nodes {", "\u0001\u0002", "Signals { a: x, 0, M; }" })
            Assert.Throws<FormatException>(() => LdfParser.Parse(bad));
        for (var length = 0; length < Ldf.Length; length += 5)
        {
            try { LdfParser.Parse(Ldf[..length]); }
            catch (FormatException) { }
        }
        var random = new Random(7);
        for (var round = 0; round < 300; round++)
        {
            var chars = Ldf.ToCharArray();
            for (var k = 0; k < 5; k++) chars[random.Next(chars.Length)] = (char)random.Next(32, 127);
            try { LdfSignalDecoder.Decode(LdfParser.Parse(new string(chars)), [Lin(0, 0x10, [1, 2], 0)]); }
            catch (FormatException) { }
        }
    }

    private static FlexRayFrame Fr(int slot, int cycle, long ts, bool error = false, bool? dynamic = false, int bytes = 4,
        FlexRayChannel ch = FlexRayChannel.A) => new()
    {
        Slot = slot, Cycle = cycle, TimestampNanoseconds = ts, DeclaredPayloadBytes = bytes, IsDynamic = dynamic, FlexRayChannel = ch,
        Flags = error ? FlexRayFlags.Error : FlexRayFlags.None
    };

    [Fact]
    public void FlexRayMatrixInfersRepetitionAndFindsMissingSlot()
    {
        var frames = new List<FlexRayFrame>();
        long ts = 0;
        for (var cycle = 0; cycle < 8; cycle++)
        {
            ts += 5_000_000;
            frames.Add(Fr(1, cycle, ts));                       // every cycle
            if (cycle != 3) frames.Add(Fr(2, cycle, ts + 1));   // slot 2 absent in cycle 3
            if (cycle % 2 == 0) frames.Add(Fr(3, cycle, ts + 2)); // repetition 2, base 0
            if (cycle == 5) frames.Add(Fr(4, cycle, ts + 3, error: true));
            if (cycle == 6) frames.Add(Fr(70, cycle, ts + 3, dynamic: true));
        }
        var rows = FlexRayAnalyzer.Analyze(frames).Rows;
        var s1 = rows.Single(r => r.Slot == 1);
        Assert.Equal(8, s1.Frames); Assert.Empty(s1.MissingInstances); Assert.Equal(1, s1.InferredRepetition);
        var s2 = rows.Single(r => r.Slot == 2);
        Assert.Single(s2.MissingInstances);
        Assert.Contains("cycle 3", s2.MissingInstances[0]);
        var s3 = rows.Single(r => r.Slot == 3);
        Assert.Equal(2, s3.InferredRepetition); Assert.Empty(s3.MissingInstances);
        var s4 = rows.Single(r => r.Slot == 4);
        Assert.Equal(1, s4.ErrorFrames); Assert.Equal(s4.FirstIndex, s4.FirstErrorIndex);
        Assert.Equal("dynamic", rows.Single(r => r.Slot == 70).Segment);
        Assert.Equal("0-7", FlexRayAnalyzer.CompressRanges(s1.SeenCycles));
        var table = BusReportTables.FlexRayMatrix(frames);
        Assert.Equal(5, table.Rows.Count);
    }

    [Fact]
    public void FlexRayFramesWithoutCycleAreNeverReportedMissing()
    {
        var frames = new[] { Fr(1, -1, 1), Fr(1, -1, 2), Fr(2, 0, 3) };
        var rows = FlexRayAnalyzer.Analyze(frames).Rows;
        Assert.Equal(2, rows.Single(r => r.Slot == 1).FramesWithoutCycle);
        Assert.All(rows, r => Assert.Empty(r.MissingInstances));
    }

    [Fact]
    public void ReportsHandleEmptyInput()
    {
        Assert.Empty(BusReportTables.LinFrames([]).Rows);
        Assert.Empty(BusReportTables.LinDiagnostics([]).Rows);
        Assert.Empty(BusReportTables.FlexRayMatrix([]).Rows);
        Assert.Empty(BusReportTables.CanXlList([]).Rows);
        Assert.Empty(BusReportTables.LinTiming([]).Rows);
    }
}
