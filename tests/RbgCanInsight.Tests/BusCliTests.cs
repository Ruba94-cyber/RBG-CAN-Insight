using RbgCanInsight.Cli;
using Xunit;
using static RbgCanInsight.Tests.BusFrameTestData;

namespace RbgCanInsight.Tests;

public sealed class BusCliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rbgbus").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliCommands.RunAsync(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task LinAndFlexRayReportsWriteCsv()
    {
        var blf = Path.Combine(_dir, "t.blf");
        await File.WriteAllBytesAsync(blf, Blf(
            LinMessage(1, 0x10, [1, 2], 0x00), LinMessage(1, 0x10, [1, 2], 0x00, ts: 20_000_000),
            FlexRayV6(1, 1, 0, [1]), FlexRayV6(1, 1, 1, [1]), FlexRayV6(1, 2, 2, [1]), FlexRayV6(1, 1, 3, [1])));
        var ldf = Path.Combine(_dir, "t.ldf");
        await File.WriteAllTextAsync(ldf, "Frames { Door: 0x10, M, 2 { }  }");

        var lin = await Run("report", "lin", blf, Path.Combine(_dir, "lin.csv"), "--ldf", ldf);
        Assert.Equal(0, lin.Code);
        Assert.Contains("2 LIN record(s), 2 with problems", lin.Out);
        var csv = await File.ReadAllTextAsync(Path.Combine(_dir, "lin.csv"));
        Assert.Contains("Door", csv);
        Assert.Contains("mismatch", csv);

        var fr = await Run("report", "flexray", blf, Path.Combine(_dir, "fr.csv"));
        Assert.Equal(0, fr.Code);
        Assert.Contains("4 FlexRay record(s)", fr.Out);
        Assert.Contains("cycle 2", await File.ReadAllTextAsync(Path.Combine(_dir, "fr.csv")));
    }

    [Fact]
    public async Task ReportWithoutBusDataSaysSoAndBadArgumentsAreUsageErrors()
    {
        var asc = Path.Combine(_dir, "c.asc");
        await File.WriteAllTextAsync(asc, "   0.1 1  123  Rx   d 1 11\n");
        var r = await Run("report", "lin", asc, Path.Combine(_dir, "o.csv"));
        Assert.Equal(0, r.Code);
        Assert.Contains("No such records were found", r.Out);
        Assert.Equal(2, (await Run("report", "flexray", asc, Path.Combine(_dir, "o2.csv"), "--ldf", "x.ldf")).Code);
        Assert.Equal(2, (await Run("report", "lin", asc)).Code);
    }
}
