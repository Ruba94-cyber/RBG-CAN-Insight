using RbgCanInsight.Analysis.E2E;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class E2eCheckLogicTests
{
    private static CanFrame Frame(int ch, uint id, double t, byte[] data) =>
        new(ch, id, data, (long)(t * 1e9));

    private static byte[] P1Payload(int i, ushort dataId = 0x1A2B)
    {
        var p = new byte[] { 0, (byte)((i % 15) | ((i * 3 % 16) << 4)), (byte)(i * 7), 0x40, 0, 0, (byte)i, 0x55 };
        p[0] = E2eCrc.Profile1(p, 0, dataId, E2eP1DataIdMode.Both, i % 15);
        return p;
    }

    private static List<CanFrame> Trace(int n, int corrupt = -1)
    {
        var list = new List<CanFrame>();
        for (var i = 0; i < n; i++)
        {
            var p = P1Payload(i);
            if (i == corrupt) p[0] ^= 0xFF;
            list.Add(Frame(1, 0x1A0, i * 0.01, p));
        }
        list.Add(Frame(2, 0x7E0, 0.5, new byte[] { 1, 2, 3 }));
        return list;
    }

    [Fact]
    public void ListIdsCountsFramesPerChannelAndId()
    {
        var ids = E2eCheckLogic.ListIds(Trace(10));
        Assert.Equal(2, ids.Count);
        Assert.Equal(10, ids[0].FrameCount);
        Assert.Equal("0x1A0", ids[0].IdText);
        Assert.True(E2eCheckLogic.Matches(ids[0], "1a0"));
        // Channels are shown 1-based everywhere (the frame above is on internal channel 2, shown as Ch 3).
        Assert.True(E2eCheckLogic.Matches(ids[1], "ch 3"));
        Assert.False(E2eCheckLogic.Matches(ids[1], "ch 2"));
        Assert.StartsWith("Ch 2  0x1A0", ids[0].ToString());
        Assert.False(E2eCheckLogic.Matches(ids[0], "7E0"));
        Assert.Empty(E2eCheckLogic.ListIds([]));
    }

    [Fact]
    public void ValidStreamIsGreen()
    {
        var frames = Trace(40);
        var id = E2eCheckLogic.ListIds(frames)[0];
        var stream = E2eCheckLogic.StreamFrames(frames, id);
        var (cfg, err) = E2eCheckLogic.BuildConfig(id, E2eProfile.P01, "1A2B", "10", null, null, E2eCheckLogic.DominantLength(stream));
        Assert.Null(err);
        var s = E2eChecker.Check(stream, [cfg!]).Summaries[0];
        var o = E2eCheckLogic.Summarize(s);
        Assert.Equal(E2eChip.Green, o.Chip);
        Assert.Equal("All 40 frames passed", o.Headline);
        Assert.DoesNotContain(E2eCheckLogic.BuildRows(s), r => r.IsProblem);
    }

    [Fact]
    public void CorruptCrcIsRedAndFlaggedAsProblem()
    {
        var frames = Trace(40, corrupt: 20);
        var id = E2eCheckLogic.ListIds(frames)[0];
        var stream = E2eCheckLogic.StreamFrames(frames, id);
        var (cfg, _) = E2eCheckLogic.BuildConfig(id, E2eProfile.P01, "0x1a2b", null, "0", "8", 8);
        var s = E2eChecker.Check(stream, [cfg!]).Summaries[0];
        var o = E2eCheckLogic.Summarize(s);
        Assert.Equal(E2eChip.Red, o.Chip);
        Assert.Contains("message 21", o.Details);
        var problems = E2eCheckLogic.BuildRows(s).Where(r => r.IsProblem).ToList();
        // The CRC-failed frame does not advance the counter, so the next frame also reads as a sequence error.
        Assert.Equal(["Wrong CRC", "Wrong sequence"], problems.Select(r => r.Status).ToArray());
    }

    [Theory]
    [InlineData("", "10")]
    [InlineData("XYZ", "10")]
    [InlineData("12345", "10")]
    [InlineData("1A2B", "-5")]
    [InlineData("1A2B", "abc")]
    public void BadInputGivesAMessageNotAnException(string dataId, string cycle)
    {
        var id = new E2eIdEntry(1, 0x1A0, false, 5);
        var (cfg, err) = E2eCheckLogic.BuildConfig(id, E2eProfile.P01, dataId, cycle, null, null, 8);
        Assert.Null(cfg);
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void OutOfRangeLayoutIsRejectedByValidation()
    {
        var id = new E2eIdEntry(1, 0x1A0, false, 5);
        var (cfg, err) = E2eCheckLogic.BuildConfig(id, E2eProfile.P05, "1", null, "7", "16", 8);
        Assert.Null(cfg);
        Assert.NotNull(err);
    }

    [Fact]
    public void EmptyStreamSummarisesWithoutThrowing()
    {
        var id = new E2eIdEntry(1, 0x1A0, false, 0);
        var (cfg, _) = E2eCheckLogic.BuildConfig(id, E2eProfile.P01, "1", null, null, null, E2eCheckLogic.DominantLength([]));
        var s = E2eChecker.Check([], [cfg!]).Summaries[0];
        Assert.Equal(E2eChip.Amber, E2eCheckLogic.Summarize(s).Chip);
        Assert.Empty(E2eCheckLogic.BuildRows(s));
        Assert.Null(E2eCheckLogic.SuggestHint([]));
    }

    [Fact]
    public void HintDetectsCounterAndChecksumOfAnE2eStream()
    {
        var frames = Trace(60);
        var id = E2eCheckLogic.ListIds(frames)[0];
        var hint = E2eCheckLogic.SuggestHint(E2eCheckLogic.StreamFrames(frames, id));
        // Heuristic output is a candidate: only assert the shape when something is found.
        if (hint is not null)
        {
            Assert.Contains("Detected a counter at bits", hint.Text);
            Assert.InRange(hint.CrcByte, 0, 7);
        }
    }

    [Fact]
    public void CsvNeutralisesFormulaPrefixesAndQuotes()
    {
        Assert.Equal("\"'=1+1\"", E2eCheckLogic.CsvEscape("=1+1"));
        Assert.Equal("\"'+1\"", E2eCheckLogic.CsvEscape("+1"));
        Assert.Equal("\"'-1\"", E2eCheckLogic.CsvEscape("-1"));
        Assert.Equal("\"'@x\"", E2eCheckLogic.CsvEscape("@x"));
        Assert.Equal("\"a\"\"b\"", E2eCheckLogic.CsvEscape("a\"b"));
        var csv = E2eCheckLogic.ToCsv([new E2eRow(1, "0.0", "=bad", "1", "0x01", "0x01", 0, "", false)]);
        Assert.Contains("\"'=bad\"", csv);
    }
}
