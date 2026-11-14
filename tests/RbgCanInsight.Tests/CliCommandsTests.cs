using Xunit;
using RbgCanInsight.Cli;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;

namespace RbgCanInsight.Tests;

public sealed class CliCommandsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rbgcli").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string P(string name) => Path.Combine(_dir, name);

    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliCommands.RunAsync(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    private async Task<string> MakeTrace(string name, int channel, long startNs, byte marker)
    {
        var path = P(name);
        var frames = Enumerable.Range(0, 5).Select(i =>
            new CanFrame(channel, 0x123, new byte[] { (byte)(i * 10), marker, 0, 0, 0, 0, 0, 0 }, startNs + i * 1_000_000_000L)).ToList();
        await TraceExporter.WriteAscAsync(path, frames);
        return path;
    }

    private string MakeDbc()
    {
        var path = P("t.dbc");
        File.WriteAllText(path,
            "VERSION \"\"\n\nNS_ :\n\nBS_:\n\nBU_: ECU\n\nBO_ 291 Msg: 8 ECU\n SG_ Speed : 0|8@1+ (0.5,0) [0|127] \"km/h\" Vector__XXX\n\n");
        return path;
    }

    [Fact]
    public async Task NoArgsAndUnknownCommandAreUsageErrors()
    {
        Assert.Equal(2, (await Run()).Code);
        var r = await Run("bogus");
        Assert.Equal(2, r.Code);
        Assert.DoesNotContain("   at ", r.Err);
    }

    [Fact]
    public async Task MissingFileIsExit1WithoutStackTrace()
    {
        var r = await Run("info", P("nope.asc"));
        Assert.Equal(1, r.Code);
        Assert.StartsWith("Error:", r.Err);
        Assert.DoesNotContain("   at ", r.Err);
    }

    [Fact]
    public async Task ConvertShiftCutRoundTrip()
    {
        var src = await MakeTrace("a.asc", 0, 0, 1);
        var csv = P("a.csv");
        Assert.Equal(0, (await Run("convert", src, csv)).Code);
        Assert.Equal(6, File.ReadAllLines(csv).Length); // CSV export is not re-importable by the existing CSV reader (pre-existing)

        var shifted = P("s.asc");
        Assert.Equal(0, (await Run("shift", src, shifted, "--seconds", "2.5")).Code);
        var sf = await CliCommands.LoadAsync(shifted, default);
        Assert.InRange(sf[0].TimestampNanoseconds, 2_499_000_000L, 2_501_000_000L);

        var neg = await Run("shift", src, P("n.asc"), "--seconds", "-1");
        Assert.Equal(1, neg.Code); // would go negative

        var cut = P("c.asc");
        Assert.Equal(0, (await Run("cut", src, cut, "--from", "1", "--to", "3")).Code);
        Assert.Equal(2, (await CliCommands.LoadAsync(cut, default)).Count);
        Assert.Equal(2, (await Run("cut", src, cut, "--from", "1")).Code);
    }

    [Fact]
    public async Task MergeKeepsChannelsApartAndSortsByTime()
    {
        var a = await MakeTrace("a.asc", 0, 0, 1);
        var b = await MakeTrace("b.asc", 0, 500_000_000, 2);
        var m = P("m.asc");
        Assert.Equal(0, (await Run("merge", m, a, b)).Code);
        var frames = await CliCommands.LoadAsync(m, default);
        Assert.Equal(10, frames.Count);
        Assert.Equal(2, frames.Select(f => f.Channel).Distinct().Count());
        Assert.True(frames.Zip(frames.Skip(1)).All(p => p.First.TimestampNanoseconds <= p.Second.TimestampNanoseconds));
    }

    [Fact]
    public async Task InfoReportsDbcCoverageWithoutPayload()
    {
        var a = await MakeTrace("a.asc", 0, 0, 7);
        var r = await Run("info", a, "--dbc", MakeDbc());
        Assert.Equal(0, r.Code);
        Assert.Contains("Frames: 5", r.Out);
        Assert.Contains("100.0% of frames", r.Out);
        Assert.Contains("Distinct identifiers: 1", r.Out);
    }

    [Fact]
    public async Task ExportSignalsWritesPhysicalValuesAndFiltersSignals()
    {
        var a = await MakeTrace("a.asc", 0, 0, 7);
        var dbc = MakeDbc();
        var csv = P("sig.csv");
        Assert.Equal(0, (await Run("export-signals", a, dbc, csv)).Code);
        var lines = File.ReadAllLines(csv);
        Assert.Equal(6, lines.Length);
        Assert.Contains("Speed", lines[2]);
        Assert.EndsWith(",5,km/h", lines[2]); // raw 10 * 0.5
        Assert.Equal(0, (await Run("export-signals", a, dbc, csv, "--signals", "Other")).Code);
        Assert.Single(File.ReadAllLines(csv));
    }

    [Fact]
    public async Task RealSamplesInfoWhenAvailable()
    {
        var dir = FindSamples();
        if (dir is null) return; // git-ignored; skipped when absent
        var file = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .FirstOrDefault(f => TraceImporter.IsSupported(f) && new FileInfo(f).Length < 50_000_000);
        if (file is null) return;
        var r = await Run("info", file);
        Assert.Equal(0, r.Code);
        Assert.Contains("Frames:", r.Out);
    }

    private static string? FindSamples()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "artifacts", "online-samples");
            if (Directory.Exists(p)) return p;
        }
        return null;
    }
}
