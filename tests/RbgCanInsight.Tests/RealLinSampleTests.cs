using RbgCanInsight.Analysis.Buses;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Cross-checks against Vector-written LIN files from artifacts/online-samples/real/lin (git-ignored; the
/// tests return silently when the folder is absent). The converter pair holds the same frame as BLF and ASC.</summary>
public sealed class RealLinSampleTests
{
    private static string? LinRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "artifacts", "online-samples", "real", "lin");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    [Fact]
    public async Task ConverterBlfAndAscDescribeTheSameLinFrame()
    {
        var root = LinRoot();
        if (root is null) return;
        var blf = await TraceImporter.ReadAllAsync(Path.Combine(root, "vector_converter_LinMessage2.blf"));
        var asc = await TraceImporter.ReadAllAsync(Path.Combine(root, "vector_converter_LinMessage2.asc"));
        var b = Assert.Single(blf.BusFrames.Lin);
        var a = Assert.Single(asc.BusFrames.Lin);
        Assert.Equal(0x2D, b.FrameId);
        Assert.Equal(new byte[] { 0x00, 0xF0, 0xF0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, b.Data);
        Assert.Equal((byte)0x70, b.Checksum);
        Assert.Equal(BusDirection.Tx, b.Direction);
        Assert.Equal(73_973_000, b.TimestampNanoseconds);
        Assert.Equal(b.FrameId, a.FrameId);
        Assert.Equal(b.Data, a.Data);
        Assert.Equal(b.Checksum, a.Checksum);
        Assert.Equal(b.TimestampNanoseconds, a.TimestampNanoseconds);
        Assert.Equal(b.Direction, a.Direction);
        // the file says "CSM = enhanced"
        Assert.Equal(LinChecksumStatus.EnhancedOnly, LinFrameAnalyzer.Check(blf.BusFrames.Lin)[0].Checksum);
    }

    [Fact]
    public async Task ConverterAscSendErrorAndSleepAreRecognised()
    {
        var root = LinRoot();
        if (root is null) return;
        var err = await TraceImporter.ReadAllAsync(Path.Combine(root, "vector_converter_LinSendError2.asc"));
        var f = Assert.Single(err.BusFrames.Lin);
        Assert.Equal(LinEventKind.SendError, f.Event);
        Assert.Equal(0x33, f.FrameId);
        var sleep = await TraceImporter.ReadAllAsync(Path.Combine(root, "vector_converter_LinSleepModeEvent.blf"));
        Assert.Equal(LinEventKind.Sleep, Assert.Single(sleep.BusFrames.Lin).Event);
        var rcv = await TraceImporter.ReadAllAsync(Path.Combine(root, "vector_converter_LinReceiveError2.asc"));
        Assert.Equal(0, rcv.BusFrames.Total);
        Assert.Equal(1, rcv.Health.UnsupportedObjectRecords); // counted, not decoded
    }

    [Fact]
    public async Task EveryRealLinBlfImportsWithoutFailure()
    {
        var root = LinRoot();
        if (root is null) return;
        foreach (var file in Directory.GetFiles(root, "*.blf"))
        {
            var r = await TraceImporter.ReadAllAsync(file);
            Assert.True(r.Health.Completed, Path.GetFileName(file));
        }
    }

    [Fact]
    public void EveryRealLdfParsesOrFailsWithFormatException()
    {
        var root = LinRoot();
        if (root is null) return;
        var parsed = 0;
        foreach (var file in Directory.GetFiles(root, "*.ldf"))
        {
            try
            {
                var ldf = LdfParser.Parse(File.ReadAllText(file));
                if (ldf.Frames.Count > 0) parsed++;
            }
            catch (FormatException) { }
        }
        Assert.True(parsed >= 10, $"only {parsed} real LDF files parsed with frames");
    }
}
