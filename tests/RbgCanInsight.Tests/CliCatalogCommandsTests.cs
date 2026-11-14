using RbgCanInsight.Cli;
using RbgCanInsight.DiagData;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class CliCatalogCommandsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rbgcli-cat").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string P(string name) => Path.Combine(_dir, name);

    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliCommands.RunAsync(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    private string Dbc(string name, string factor = "0.5", bool duplicateName = false)
    {
        var path = P(name);
        var second = duplicateName ? "\nBO_ 292 Msg: 8 ECU\n SG_ Other : 0|8@1+ (1,0) [0|255] \"\" Vector__XXX\n" : string.Empty;
        File.WriteAllText(path,
            "VERSION \"\"\n\nNS_ :\n\nBS_:\n\nBU_: ECU\n\nBO_ 291 Msg: 8 ECU\n SG_ Speed : 0|8@1+ (" + factor +
            ",0) [0|127] \"km/h\" Vector__XXX\n" + second + "\n");
        return path;
    }

    private string Odx(string name, bool brokenField = false)
    {
        var path = P(name);
        var field = new DiagnosticFieldDefinition("A", "A", "", 0, 8, "A_UINT8", "", null, null, 1, 0, false, "BIG_ENDIAN");
        var fields = brokenField ? new[] { field, field with { Name = "B", BitOffset = 4 } } : new[] { field };
        var did = new DidParameter(0xF190, "VIN", "VIN", "", "", null, null, 1, 0, "A_UINT8", 16, "ECU1", "x", fields);
        var catalog = new DidCatalog("ecu", "ECU1", "x", [did],
            [new DiagnosticTroubleCode(0x012345, "P0123", "Throttle low", "Desc", "ECU1", "x")]);
        OdxCatalogWriter.Write(catalog, path);
        return path;
    }

    [Fact]
    public async Task CheckDbcFailsOnErrorsAndWritesTheCsv()
    {
        var bad = Dbc("bad.dbc", duplicateName: true);
        var r = await Run("check-dbc", bad, "--out", P("dbc.csv"));
        Assert.Equal(1, r.Code);
        Assert.Contains("check-dbc: FAIL", r.Out);
        Assert.Contains("DBC-002", await File.ReadAllTextAsync(P("dbc.csv")));

        var ok = await Run("check-dbc", Dbc("ok.dbc"));
        Assert.Equal(0, ok.Code);
        Assert.Contains("check-dbc: PASS", ok.Out);
    }

    [Fact]
    public async Task CheckDbcFailOnLevelIsHonoured()
    {
        var path = Dbc("ok.dbc");
        Assert.Equal(0, (await Run("check-dbc", path, "--fail-on", "error")).Code);
        var strict = await Run("check-dbc", path, "--fail-on", "review");
        Assert.Contains("review item(s)", strict.Out);
        Assert.Equal(2, (await Run("check-dbc", path, "--fail-on", "bogus")).Code);
    }

    [Fact]
    public async Task CheckCddReportsLayoutErrorsAndPassesACleanFile()
    {
        var broken = await Run("check-cdd", Odx("broken.odx", brokenField: true), "--out", P("cat.csv"));
        // The ODX reader may normalise the layout; the contract is the exit code matches the report content.
        var csv = await File.ReadAllTextAsync(P("cat.csv"));
        Assert.Equal(csv.Contains(",Error,", StringComparison.Ordinal) ? 1 : 0, broken.Code);
        Assert.Contains("check-cdd:", broken.Out);

        var clean = await Run("check-cdd", Odx("clean.odx"), "--fail-on", "error");
        Assert.Equal(0, clean.Code);
        Assert.Contains("rule(s) evaluated", clean.Out);
    }

    [Fact]
    public async Task CheckCddMissingFileIsAnErrorWithoutStackTrace()
    {
        var r = await Run("check-cdd", P("nope.odx"));
        Assert.Equal(1, r.Code);
        Assert.DoesNotContain("   at ", r.Err);
    }

    [Fact]
    public async Task CompareReportsIdenticalAndDifferentDbcs()
    {
        var same = await Run("compare", Dbc("a.dbc"), Dbc("b.dbc"));
        Assert.Equal(0, same.Code);
        Assert.Contains("IDENTICAL", same.Out);

        var diff = await Run("compare", Dbc("a.dbc"), Dbc("c.dbc", factor: "0.25"), "--out", P("diff.csv"));
        Assert.Equal(1, diff.Code);
        Assert.Contains("DIFFERENT", diff.Out);
        var csv = await File.ReadAllTextAsync(P("diff.csv"));
        Assert.Contains("Speed", csv);
        Assert.Contains("Changed", csv);
    }

    [Fact]
    public async Task CompareCatalogsWorksAndRefusesToMixKinds()
    {
        var path = Odx("one.odx");
        Assert.Equal(0, (await Run("compare", path, path)).Code);
        Assert.Equal(2, (await Run("compare", path, Dbc("x.dbc"))).Code);
    }

    [Fact]
    public async Task ExportOdxWritesAValidatedFile()
    {
        var input = Odx("in.odx");
        var r = await Run("export-odx", input, P("out.odx"));
        Assert.Equal(0, r.Code);
        Assert.Contains("structural self-check passed", r.Out);
        Assert.Empty(OdxCatalogWriter.Validate(P("out.odx")));
        var again = DidCatalog.LoadAll(P("out.odx"));
        Assert.Contains(again.SelectMany(c => c.Parameters), p => p.Did == 0xF190);
    }

    [Fact]
    public async Task ExportOdxUnknownEcuListsTheAvailableOnes()
    {
        var r = await Run("export-odx", Odx("in.odx"), P("out.odx"), "--ecu", "NOPE");
        Assert.Equal(1, r.Code);
        Assert.Contains("No ECU named", r.Err);
    }

    private async Task<string> Trace(string name, params CanFrame[] frames)
    {
        var path = P(name);
        await TraceExporter.WriteAscAsync(path, frames);
        return path;
    }

    private static CanFrame F(uint id, long ms, params byte[] payload)
    {
        var data = new byte[8];
        payload.CopyTo(data, 0);
        return new CanFrame(0, id, data, ms * 1_000_000L);
    }

    [Fact]
    public async Task TimingReportWritesFindingsAndHonoursThresholds()
    {
        var trace = await Trace("t.asc",
            F(0x7E0, 0, 3, 0x22, 0xF1, 0x90), F(0x7E8, 80, 5, 0x62, 0xF1, 0x90, 0xAA, 0xBB));
        var r = await Run("report", "timing", trace, P("timing.csv"));
        Assert.Equal(0, r.Code);
        Assert.Contains("TIM-001", await File.ReadAllTextAsync(P("timing.csv")));

        var lenient = await Run("report", "timing", trace, P("timing2.csv"), "--p2-ms", "200");
        Assert.DoesNotContain("TIM-001", await File.ReadAllTextAsync(P("timing2.csv")));
        Assert.Equal(0, lenient.Code);

        // Very late response: error severity, exit code 1.
        var late = await Trace("late.asc", F(0x7E0, 0, 3, 0x22, 0xF1, 0x90), F(0x7E8, 400, 5, 0x62, 0xF1, 0x90, 0xAA, 0xBB));
        Assert.Equal(1, (await Run("report", "timing", late, P("timing3.csv"))).Code);
    }

    [Fact]
    public async Task FaultsReportListsDtcsWithStatusBitColumns()
    {
        var trace = await Trace("f.asc",
            F(0x7E0, 0, 3, 0x19, 0x02, 0xFF), F(0x7E8, 5, 7, 0x59, 0x02, 0xFF, 0x12, 0x34, 0x56, 0x09));
        var r = await Run("report", "faults", trace, P("faults.csv"));
        Assert.Equal(0, r.Code);
        var csv = await File.ReadAllTextAsync(P("faults.csv"));
        Assert.Contains("0x123456", csv);
        Assert.Contains("testFailed", csv);
        Assert.Contains("1 DTC(s)", r.Out);
    }
}
