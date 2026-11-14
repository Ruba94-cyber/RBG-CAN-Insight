using System.Buffers.Binary;
using RbgCanInsight.Analysis.E2E;
using RbgCanInsight.Cli;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class CliReportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rbgrep").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string P(string name) => Path.Combine(_dir, name);

    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliCommands.RunAsync(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    private async Task<string> Asc(string name, IReadOnlyList<CanFrame> frames)
    {
        var path = P(name);
        await TraceExporter.WriteAscAsync(path, frames);
        return path;
    }

    private static uint J(byte pf, byte ps, byte sa) => (6u << 26) | ((uint)pf << 16) | ((uint)ps << 8) | sa;

    private static CanFrame Ext(uint id, byte[] data, long ms) =>
        new(0, id, data, ms * 1_000_000, CanFrameFlags.Extended);

    private static CanFrame Std(uint id, byte[] data, long ms) => new(0, id, data, ms * 1_000_000);

    [Fact]
    public async Task J1939BamDm1IsReassembledAndDecoded()
    {
        byte[] p = [0x14, 0xFF, 0x64, 0x00, 0x03, 0x05, 0xCD, 0xAB, 0x24, 0x82];
        var frames = new List<CanFrame>
        {
            Ext(J(0xEC, 0xFF, 0x10), [32, 10, 0, 2, 0xFF, 0xCA, 0xFE, 0x00], 0),
            Ext(J(0xEB, 0xFF, 0x10), [1, .. p[..7]], 10),
            Ext(J(0xEB, 0xFF, 0x10), [2, .. p[7..], 0xFF, 0xFF, 0xFF, 0xFF], 20)
        };
        var trace = await Asc("j.asc", frames);
        var r = await Run("report", "j1939", trace, P("j.csv"));
        Assert.Equal(0, r.Code);
        Assert.Single(r.Out.Trim().Split('\n'));
        var csv = File.ReadAllText(P("j.csv"));
        Assert.Contains("0x0FECA", csv);
        Assert.Contains("SPN 100 FMI 3", csv);
        Assert.Contains("SPN 109517 FMI 4", csv);
    }

    [Fact]
    public async Task ObdEngineSpeedIsDecoded()
    {
        static byte[] Sf(params byte[] payload)
        {
            var d = new byte[8];
            Array.Fill(d, (byte)0x55);
            d[0] = (byte)payload.Length;
            payload.CopyTo(d, 1);
            return d;
        }
        var trace = await Asc("o.asc", [Std(0x7DF, Sf(0x01, 0x0C), 1), Std(0x7E8, Sf(0x41, 0x0C, 0x1A, 0xF8), 3)]);
        var r = await Run("report", "obd", trace, P("o.csv"));
        Assert.Equal(0, r.Code);
        var csv = File.ReadAllText(P("o.csv"));
        Assert.Contains("1726", csv);
        Assert.Contains("rpm", csv);
    }

    [Fact]
    public async Task CandidatesFindCounterAndXorChecksum()
    {
        var rnd = new Random(7);
        var frames = new List<CanFrame>();
        for (var i = 0; i < 80; i++)
        {
            var d = new byte[8];
            d[1] = (byte)(i % 16);
            for (var k = 2; k < 8; k++) d[k] = (byte)rnd.Next(256);
            d[0] = (byte)(d[1] ^ d[2] ^ d[3] ^ d[4] ^ d[5] ^ d[6] ^ d[7]);
            frames.Add(Std(0x1A0, d, i * 10));
        }
        var trace = await Asc("c.asc", frames);
        var r = await Run("report", "candidates", trace, P("c.csv"));
        Assert.Equal(0, r.Code);
        var csv = File.ReadAllText(P("c.csv"));
        Assert.Contains("Counter", csv);
        Assert.Contains("Checksum", csv);
    }

    [Fact]
    public async Task BusLoadWritesRowsAndValidatesKbps()
    {
        var frames = Enumerable.Range(0, 50).Select(i => Std(0x100, new byte[8], i * 10)).ToList();
        var trace = await Asc("b.asc", frames);
        var r = await Run("report", "busload", trace, P("b.csv"), "--kbps", "500");
        Assert.Equal(0, r.Code);
        Assert.True(File.ReadAllLines(P("b.csv")).Length >= 2);
        Assert.Equal(2, (await Run("report", "busload", trace, P("b2.csv"))).Code);
        Assert.Equal(2, (await Run("report", "busload", trace, P("b2.csv"), "--kbps", "0")).Code);
    }

    private static List<CanFrame> E2eStream(int count, int corruptAt = -1)
    {
        var frames = new List<CanFrame>();
        for (var i = 0; i < count; i++)
        {
            var d = new byte[8];
            d[1] = (byte)(i % 15);
            d[2] = (byte)(i * 3);
            d[0] = E2eCrc.Profile1(d, 0, 0x1A2B, E2eP1DataIdMode.Both, i % 15);
            if (i == corruptAt) d[0] ^= 0x5A;
            frames.Add(Std(0x1A0, d, i * 10));
        }
        return frames;
    }

    [Fact]
    public async Task E2eProfile1ValidStreamPassesWithExit0()
    {
        var trace = await Asc("e.asc", E2eStream(40));
        var r = await Run("report", "e2e", trace, P("e.csv"), "--id", "1A0", "--profile", "p1", "--data-id", "1A2B", "--cycle-ms", "10");
        Assert.Equal(0, r.Code);
        Assert.Contains("PASS", r.Out);
        Assert.Single(r.Out.Trim().Split('\n'));
        Assert.Equal(41, File.ReadAllLines(P("e.csv")).Length);
    }

    [Fact]
    public async Task E2eProfile1CorruptedStreamFailsWithExit1()
    {
        var trace = await Asc("e.asc", E2eStream(40, corruptAt: 12));
        var r = await Run("report", "e2e", trace, P("e.csv"), "--id", "1A0", "--channel", "0", "--profile", "p1", "--data-id", "1A2B");
        Assert.Equal(1, r.Code);
        Assert.Contains("FAIL", r.Out);
        Assert.Contains("Wrong CRC", File.ReadAllText(P("e.csv")));
    }

    [Fact]
    public async Task E2eUnknownIdAndBadArgumentsAreReported()
    {
        var trace = await Asc("e.asc", E2eStream(5));
        Assert.Equal(1, (await Run("report", "e2e", trace, P("e.csv"), "--id", "7FF", "--profile", "p1", "--data-id", "1")).Code);
        Assert.Equal(2, (await Run("report", "e2e", trace, P("e.csv"), "--id", "1A0", "--profile", "p9", "--data-id", "1")).Code);
        Assert.Equal(2, (await Run("report", "e2e", trace, P("e.csv"), "--id", "1A0", "--profile", "p1", "--data-id", "zz")).Code);
        Assert.Equal(2, (await Run("report", "bogus")).Code);
        Assert.Equal(2, (await Run("report")).Code);
    }

    [Fact]
    public async Task SuggestDbcRanksTheMatchingFileFirst()
    {
        File.WriteAllText(P("good.dbc"),
            "VERSION \"\"\n\nNS_ :\n\nBS_:\n\nBU_: ECU\n\nBO_ 416 Msg: 8 ECU\n SG_ Speed : 0|8@1+ (1,0) [0|255] \"\" Vector__XXX\n\n");
        File.WriteAllText(P("other.dbc"),
            "VERSION \"\"\n\nNS_ :\n\nBS_:\n\nBU_: ECU\n\nBO_ 512 Else: 8 ECU\n SG_ Temp : 0|8@1+ (1,0) [0|255] \"\" Vector__XXX\n\n");
        var frames = Enumerable.Range(0, 20).Select(i => Std(0x1A0, [(byte)i, 0, 0, 0, 0, 0, 0, 0], i * 10)).ToList();
        var trace = await Asc("s.asc", frames);
        var r = await Run("report", "suggest-dbc", trace, P("s.csv"), P("other.dbc"), P("good.dbc"));
        Assert.Equal(0, r.Code);
        var lines = File.ReadAllLines(P("s.csv"));
        Assert.Equal(3, lines.Length);
        Assert.Contains("good.dbc", lines[1]);
        Assert.Contains("other.dbc", lines[2]);
        Assert.Contains("good.dbc", r.Out);
    }

    [Fact]
    public async Task SignalStatsWritesSummaryAndRejectsMissingSignal()
    {
        File.WriteAllText(P("t.dbc"),
            "VERSION \"\"\n\nNS_ :\n\nBS_:\n\nBU_: ECU\n\nBO_ 416 Msg: 8 ECU\n SG_ Speed : 0|8@1+ (1,0) [0|255] \"\" Vector__XXX\n\n");
        var frames = Enumerable.Range(0, 30).Select(i => Std(0x1A0, [(byte)(i % 10), 0, 0, 0, 0, 0, 0, 0], i * 100)).ToList();
        var trace = await Asc("st.asc", frames);
        var r = await Run("report", "signal-stats", trace, P("t.dbc"), P("st.csv"), "--signal", "Msg.Speed");
        Assert.Equal(0, r.Code);
        var csv = File.ReadAllText(P("st.csv"));
        Assert.Contains("Samples", csv);
        Assert.Contains("Msg.Speed", csv);
        Assert.Equal(1, (await Run("report", "signal-stats", trace, P("t.dbc"), P("st2.csv"), "--signal", "Msg.Nope")).Code);
        Assert.Equal(2, (await Run("report", "signal-stats", trace, P("t.dbc"), P("st2.csv"), "--signal", "Nodot")).Code);
    }

    [Fact]
    public async Task CsvCellsStartingWithFormulaCharactersAreNeutralised()
    {
        // Direct check of the shared helper used for every report cell.
        Assert.StartsWith("'=", CliCommands.Csv("=1+1"));
        Assert.StartsWith("'@", CliCommands.Csv("@x"));
        Assert.StartsWith("'+", CliCommands.Csv("+1"));
        Assert.StartsWith("'-", CliCommands.Csv("-1"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ConvertToMdf4RoundTripsAndReportsSkippedFrames()
    {
        var frames = new List<CanFrame>
        {
            Std(0x123, [1, 2, 3, 4, 5, 6, 7, 8], 1),
            new(1, 0x18FEF100, [9, 8, 7], 2_000_000, CanFrameFlags.Extended),
            Std(0x321, [], 3)
        };
        var trace = await Asc("m.asc", frames);
        var r = await Run("convert", trace, P("m.mf4"));
        Assert.Equal(0, r.Code);
        var back = await CliCommands.LoadAsync(P("m.mf4"), CancellationToken.None);
        Assert.Equal(3, back.Count);
        Assert.Equal(0x123u, back[0].Identifier);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, back[0].GetPayload());
        Assert.True(back[1].IsExtended);
        Assert.Equal(1, back[1].Channel);
        Assert.Equal(2_000_000, back[1].TimestampNanoseconds);
        Assert.Equal("", r.Err);

        // A remote frame cannot be stored in MDF4: it is skipped and counted on stderr.
        var withRemote = await Asc("m2.asc", [.. frames, new CanFrame(0, 0x555, [], 4_000_000, CanFrameFlags.Remote)]);
        var r2 = await Run("convert", withRemote, P("m2.mf4"));
        Assert.Equal(0, r2.Code);
        Assert.Contains("1 frame(s) skipped", r2.Err);
    }

    // ---- pcapng input ----

    private static void U32(List<byte> o, uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, v); o.AddRange(s.ToArray()); }
    private static void U16(List<byte> o, ushort v) { Span<byte> s = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(s, v); o.AddRange(s.ToArray()); }

    private static byte[] Block(uint type, Action<List<byte>> body)
    {
        var c = new List<byte>();
        body(c);
        while (c.Count % 4 != 0) c.Add(0);
        var b = new List<byte>();
        U32(b, type); U32(b, (uint)c.Count + 12); b.AddRange(c); U32(b, (uint)c.Count + 12);
        return b.ToArray();
    }

    [Fact]
    public async Task PcapngSocketCanInputIsConverted()
    {
        var file = new List<byte>();
        file.AddRange(Block(0x0A0D0D0A, b => { U32(b, 0x1A2B3C4D); U16(b, 1); U16(b, 0); b.AddRange(Enumerable.Repeat((byte)0xFF, 8)); }));
        file.AddRange(Block(1, b => { U16(b, 227); U16(b, 0); U32(b, 0); }));
        foreach (var (ticks, canId) in new[] { (1_500_000UL, 0x123u), (2_000_000UL, 0x8000_0000u | 0x18FEF100u) })
        {
            var pkt = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(pkt, canId);
            pkt[4] = 3; pkt[8] = 1; pkt[9] = 2; pkt[10] = 3;
            file.AddRange(Block(6, b => { U32(b, 0); U32(b, (uint)(ticks >> 32)); U32(b, (uint)ticks); U32(b, 16); U32(b, 16); b.AddRange(pkt); }));
        }
        var path = P("cap.pcapng");
        File.WriteAllBytes(path, file.ToArray());
        var r = await Run("convert", path, P("cap.asc"));
        Assert.Equal(0, r.Code);
        Assert.Contains("Converted 2 frames", r.Out);
        var back = await CliCommands.LoadAsync(P("cap.asc"), CancellationToken.None);
        Assert.Equal(2, back.Count);
        Assert.Equal(0x123u, back[0].Identifier);
        Assert.Equal(1_500_000_000L, back[0].TimestampNanoseconds);
        Assert.True(back[1].IsExtended);
    }
}
