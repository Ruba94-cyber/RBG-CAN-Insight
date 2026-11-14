using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class TraceDatabaseIsolationTests
{
    private static DbcDatabase Dbc(string name, double factor) =>
        new DbcParser().Parse(name, new StringReader(
            $"""
            BO_ 256 Sensor: 8 ECM
             SG_ Value : 0|8@1+ ({factor.ToString(System.Globalization.CultureInfo.InvariantCulture)},0) [0|500] "" ECM
            """));

    [Fact]
    public void IdenticallyNamedDifferentDatabasesCannotMixPhysicalScalingIntoOneTimeline()
    {
        var first = Dbc("same-name", 1);
        var second = Dbc("same-name", 2);
        var frames = new[]
        {
            new CanFrame(0, 256, [10], 0),
            new CanFrame(0, 256, [20], 1_000_000)
        };

        Assert.Empty(SignalTimelineBuilder.Build(frames, [first, second]));
        // Source frames remain intact, even when database identity is ambiguous.
        Assert.Equal(2, frames.Length);
        Assert.Equal((byte)10, frames[0].GetByte(0));
    }

    [Fact]
    public void DatabaseConflictIsDetectedEvenWhenOneDefinitionProducesOnlyNaN()
    {
        var floating = new DbcSignal("ReportedValue", 0, 32,
            DbcByteOrder.Intel, false, 1, 0, 0, 100, "", "ECM",
            ValueType: DbcSignalValueType.Float32);
        var scalar = new DbcSignal("ReportedValue", 0, 8,
            DbcByteOrder.Intel, false, 1, 0, 0, 255, "", "ECM");
        var floatingMessage = new DbcMessage(256, "Sensor", 8,
            "ECM", false, [floating]);
        var scalarMessage = new DbcMessage(256, "Sensor", 8,
            "ECM", false, [scalar]);
        var floatDatabase = new DbcDatabase("identical-name",
            new Dictionary<uint, DbcMessage> { [256] = floatingMessage });
        var intDatabase = new DbcDatabase("identical-name",
            new Dictionary<uint, DbcMessage> { [256] = scalarMessage });
        var nan = BitConverter.GetBytes(float.NaN);
        var frames = new[]
        {
            new CanFrame(0, 256, nan, 0),
            new CanFrame(0, 256, nan, 1_000_000)
        };

        // The NaN-only definition is a real competing source even though it
        // cannot contribute numeric samples. No raw source frame is changed.
        Assert.Empty(SignalTimelineBuilder.Build(
            frames, [floatDatabase, intDatabase]));
        Assert.Equal(2, frames.Length);
        Assert.Equal(BitConverter.ToUInt32(nan),
            BitConverter.ToUInt32(frames[0].GetPayload()));
    }

    [Fact]
    public void TwoOverlappingProfilesOfTheSameDatabaseDoNotDoubleCountSourceFrames()
    {
        var database = Dbc("one-source", 1);
        var decoders = new[]
        {
            new DbcDecoder(database),
            new DbcDecoder(database)
        };
        var frames = new[]
        {
            new CanFrame(0, 256, [10], 0),
            new CanFrame(0, 256, [20], 1_000_000)
        };

        var series = Assert.Single(SignalTimelineBuilder.Build(frames, decoders));
        Assert.Equal(2, series.Samples.Count);
        Assert.Equal(new[] { 0, 1 },
            series.Samples.Select(sample => sample.FrameIndex));
    }

    [Fact]
    public void SameNamedDatabasesOnDifferentExplicitChannelsRemainSeparateEvidence()
    {
        var first = Dbc("same-name", 1);
        var second = Dbc("same-name", 2);
        var decoders = new[]
        {
            new DbcDecoder(first, new DbcDecoderOptions(Channel: 0)),
            new DbcDecoder(second, new DbcDecoderOptions(Channel: 1))
        };
        var frames = new[]
        {
            new CanFrame(0, 256, [10], 0),
            new CanFrame(1, 256, [10], 0)
        };

        var timelines = SignalTimelineBuilder.Build(frames, decoders);
        Assert.Equal(2, timelines.Count);
        Assert.Equal(10d, Assert.Single(timelines.Single(t => t.Channel == 0).Samples).Value);
        Assert.Equal(20d, Assert.Single(timelines.Single(t => t.Channel == 1).Samples).Value);
    }

    [Fact]
    public void DenseSixtyFourSignalMessageKeepsPerSignalIdentityAcrossManyFrames()
    {
        var signals = Enumerable.Range(0, 64).Select(bit =>
            new DbcSignal("Bit_" + bit, bit, 1,
                DbcByteOrder.Intel, false, 1, 0, 0, 1, "", "ECM"))
            .ToArray();
        var message = new DbcMessage(256, "DenseBitfield", 8,
            "ECM", false, signals);
        var database = new DbcDatabase("dense",
            new Dictionary<uint, DbcMessage> { [256] = message });
        var frames = Enumerable.Range(0, 128).Select(index =>
            new CanFrame(0, 256,
                Enumerable.Repeat((byte)0xFF, 8).ToArray(),
                index * 1_000_000L)).ToArray();

        var timelines = SignalTimelineBuilder.Build(frames, [database]);
        Assert.Equal(64, timelines.Count);
        Assert.Equal(64, timelines.Select(item => item.Signal.Name).Distinct().Count());
        Assert.All(timelines, timeline =>
        {
            Assert.Equal(128, timeline.Samples.Count);
            Assert.All(timeline.Samples, sample => Assert.Equal(1d, sample.Value));
            Assert.Equal(0, timeline.Samples[0].FrameIndex);
            Assert.Equal(127, timeline.Samples[^1].FrameIndex);
        });
    }

    [Fact]
    public void DuplicateSignalNamesAreNotArbitrarilyDecodedAndIndependentFieldsSurvive()
    {
        var duplicateA = new DbcSignal("Collision", 0, 8, DbcByteOrder.Intel,
            false, 1, 0, 0, 255, "", "ECM");
        var duplicateB = duplicateA with { StartBit = 8, Factor = 10 };
        var independent = duplicateA with { Name = "Unambiguous", StartBit = 16 };
        var message = new DbcMessage(256, "BrokenDbc", 3, "ECM", false,
            [duplicateA, duplicateB, independent]);
        var database = new DbcDatabase("broken",
            new Dictionary<uint, DbcMessage> { [256u] = message });
        var frames = new[] { new CanFrame(0, 256, [10, 20, 30], 0) };

        var values = new DbcDecoder(database).Decode(frames[0]);
        Assert.Equal("Unambiguous", Assert.Single(values).Signal);
        var timeline = Assert.Single(SignalTimelineBuilder.Build(frames, [database]));
        Assert.Equal("Unambiguous", timeline.Signal.Name);
        Assert.Equal(30, Assert.Single(timeline.Samples).Value);
    }
}
