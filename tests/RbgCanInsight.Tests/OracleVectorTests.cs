using System.Text.Json;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>
/// Release-gate evidence for "BLF parsing validated" and "DBC decoding validated".
/// The fixtures under fixtures/ are produced by tools/dbc-blf-oracle/make_vectors.py, a stdlib-only
/// Python script that shares no code with the application: its own LOGG/LOBJ byte writer and a
/// linear-bit-index DBC decoder. expected.json holds the oracle's frame fields and decoded values.
/// </summary>
public sealed class OracleVectorTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "fixtures");
    private static readonly Lazy<JsonElement> Expected = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "expected.json"))).RootElement.Clone());

    private static async Task<List<CanFrame>> ImportAsync(string name)
    {
        var frames = new List<CanFrame>();
        await foreach (var frame in BlfImporter.ReadAsync(Path.Combine(Dir, name)))
            frames.Add(frame);
        return frames;
    }

    private static void AssertFrame(JsonElement e, CanFrame f)
    {
        var tag = $"frame #{e.GetProperty("index").GetInt32()}";
        Assert.True(e.GetProperty("channel").GetInt32() == f.Channel, tag + " channel");
        Assert.True(e.GetProperty("id").GetUInt32() == f.Identifier, tag + " id");
        Assert.True(e.GetProperty("extended").GetBoolean() == f.IsExtended, tag + " extended");
        Assert.True(e.GetProperty("remote").GetBoolean() == f.Flags.HasFlag(CanFrameFlags.Remote), tag + " remote");
        Assert.True(e.GetProperty("error").GetBoolean() == f.IsError, tag + " error");
        Assert.True(e.GetProperty("tx").GetBoolean() == f.IsTransmit, tag + " tx");
        Assert.True(e.GetProperty("fd").GetBoolean() == f.Flags.HasFlag(CanFrameFlags.FlexibleDataRate), tag + " fd");
        Assert.True(e.GetProperty("brs").GetBoolean() == f.Flags.HasFlag(CanFrameFlags.BitRateSwitch), tag + " brs");
        Assert.True(e.GetProperty("ts_ns").GetInt64() == f.TimestampNanoseconds, tag + " timestamp");
        Assert.Equal(e.GetProperty("data").GetString(), Convert.ToHexString(f.GetPayload()).ToLowerInvariant());
    }

    [Fact]
    public async Task OracleBlfFramesMatchExactlyAcrossContainersTypesAndUnits()
    {
        var frames = await ImportAsync("oracle_main.blf");
        var expected = Expected.Value.GetProperty("frames");
        Assert.Equal(expected.GetArrayLength(), frames.Count);
        for (var i = 0; i < frames.Count; i++)
            AssertFrame(expected[i], frames[i]);
        Assert.True(frames.Count >= 280);
    }

    [Fact]
    public async Task OracleDbcPhysicalAndRawValuesMatchExactlyIncludingAbsentSignals()
    {
        var database = new DbcParser().ParseFile(Path.Combine(Dir, "oracle.dbc"));
        Assert.Empty(database.ParseWarnings);
        var decoder = new DbcDecoder(database);
        var frames = await ImportAsync("oracle_main.blf");

        var byFrame = Expected.Value.GetProperty("signals").EnumerateArray()
            .GroupBy(s => s.GetProperty("frame").GetInt32())
            .ToDictionary(g => g.Key, g => g.ToList());
        var checkedValues = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            var actual = decoder.Decode(frames[i]);
            var want = byFrame.GetValueOrDefault(i) ?? [];
            // Same signal SET: inactive multiplex branches, short payloads, remote and error
            // objects must not produce values (absent, never zero).
            Assert.True(
                want.Select(s => s.GetProperty("signal").GetString()).Order(StringComparer.Ordinal)
                    .SequenceEqual(actual.Select(s => s.Signal).Order(StringComparer.Ordinal)),
                $"frame #{i}: expected [{string.Join(",", want.Select(s => s.GetProperty("signal").GetString()))}] " +
                $"got [{string.Join(",", actual.Select(s => s.Signal))}]");
            foreach (var e in want)
            {
                var name = e.GetProperty("signal").GetString()!;
                var value = actual.Single(v => v.Signal == name);
                var tag = $"frame #{i} {e.GetProperty("message").GetString()}.{name}";
                Assert.True(e.GetProperty("message").GetString() == value.Message, tag + " message");
                Assert.True(e.GetProperty("raw_value").GetDouble() == value.RawValue,
                    $"{tag} raw expected {e.GetProperty("raw").GetString()} got {value.RawValue:R}");
                var expectedPhysical = e.GetProperty("physical").GetDouble();
                Assert.True(
                    Math.Abs(expectedPhysical - value.PhysicalValue) <= 1e-9 * Math.Max(1, Math.Abs(expectedPhysical)),
                    $"{tag} physical expected {expectedPhysical:R} got {value.PhysicalValue:R}");
                Assert.Equal(e.GetProperty("unit").GetString(), value.Unit);
                if (e.GetProperty("text").ValueKind == JsonValueKind.String)
                {
                    Assert.True(database.TryGetMessage(frames[i].Identifier, frames[i].IsExtended, out var message));
                    var signal = message.Signals.Single(s => s.Name == name);
                    Assert.Equal(e.GetProperty("text").GetString(),
                        signal.GetValueDescription(long.Parse(e.GetProperty("raw").GetString()!)));
                }
                checkedValues++;
            }
        }
        Assert.Equal(Expected.Value.GetProperty("signals").GetArrayLength(), checkedValues);
    }

    [Fact]
    public async Task Integer64BitRawCodesStayExactInTheirSignalDefinition()
    {
        // 2^53+1 cannot be held in a double: the physical value is rounded, but the DBC
        // named-value lookup must not confuse neighbours (checked through the long overload).
        var database = new DbcParser().ParseFile(Path.Combine(Dir, "oracle.dbc"));
        var frames = await ImportAsync("oracle_main.blf");
        var decoder = new DbcDecoder(database);
        var wide = frames.Where(f => f.Identifier == 0x104 && !f.IsExtended).ToList();
        Assert.Equal(2, wide.Count);
        Assert.Equal(18446744073709551615d, decoder.Decode(wide[0]).Single().RawValue);
        Assert.Equal(9007199254740992d, decoder.Decode(wide[1]).Single().RawValue); // nearest double of 2^53+1
        Assert.Equal(new byte[] { 1, 0, 0, 0, 0, 0, 0x20, 0 }, wide[1].GetPayload());
    }

    [Theory]
    [InlineData("oracle_neg_zero_length.blf")]
    [InlineData("oracle_neg_header_only_garbage.blf")]
    [InlineData("oracle_neg_bad_magic.blf")]
    [InlineData("oracle_neg_huge_header_size.blf")]
    [InlineData("oracle_neg_huge_object_size.blf")]
    [InlineData("oracle_neg_huge_container_declared.blf")]
    [InlineData("oracle_neg_huge_zlib_declared.blf")]
    [InlineData("oracle_neg_truncated_container.blf")]
    [InlineData("oracle_neg_truncated_object_header.blf")]
    [InlineData("oracle_neg_bad_zlib_checksum.blf")]
    public async Task CorruptFilesAreRejectedWithoutSilentlyAcceptingPartialData(string file)
    {
        // A malformed BLF must raise a clear error; it must never produce a truncated trace that
        // looks complete. (Frames yielded before the failing object may exist; the error must follow.)
        await Assert.ThrowsAnyAsync<Exception>(async () => await ImportAsync(file));
    }

    [Fact]
    public async Task UnsupportedCompressionMethodIsReportedNotIgnored() =>
        await Assert.ThrowsAsync<NotSupportedException>(() => ImportAsync("oracle_neg_unsupported_compression.blf"));

    [Fact]
    public async Task ZeroLengthFileReportsEmptyFile()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ImportAsync("oracle_neg_zero_length.blf"));
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownObjectTypesAreSkippedAndKnownFramesSurvive()
    {
        var frames = await ImportAsync("oracle_neg_unknown_only.blf");
        AssertFrame(Expected.Value.GetProperty("unknown_only_frame"), Assert.Single(frames));
    }

    [Fact]
    public async Task BadObjectSignatureResynchronisesOntoFollowingObjectsOnly()
    {
        var frames = await ImportAsync("oracle_neg_bad_object_signature.blf");
        var survivors = Expected.Value.GetProperty("bad_signature_survivors");
        Assert.Equal(survivors.GetArrayLength(), frames.Count);
        for (var i = 0; i < frames.Count; i++)
            AssertFrame(survivors[i], frames[i]);
    }

    [Fact]
    public void FixtureSetStaysSmall()
    {
        var total = new DirectoryInfo(Dir).EnumerateFiles().Sum(f => f.Length);
        Assert.True(total < 1_000_000, $"fixtures are {total} bytes");
    }
}
