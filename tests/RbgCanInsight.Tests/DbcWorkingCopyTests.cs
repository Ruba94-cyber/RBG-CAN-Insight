using RbgCanInsight.Dbc;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DbcWorkingCopyTests
{
    private static (DbcDatabase Db, DbcSignal Signal) Sample()
    {
        var speed = new DbcSignal("Speed", 8, 16, DbcByteOrder.Intel,
            false, 0.01, 0, 0, 200, "km/h", "Tester",
            ValueDescriptions: new Dictionary<long, string> { [0] = "Stopped" });
        var standard = new DbcMessage(0x123, "Main", 8, "ECU", false, [speed]);
        var extended = new DbcMessage(0x123, "Ext", 8, "ECU", true,
            [new DbcSignal("State", 0, 8, DbcByteOrder.Intel, false, 1, 0,
                0, 255, "", "Tester")]);
        var db = new DbcDatabase("Sample",
            new Dictionary<uint, DbcMessage> { [0x123] = standard },
            MessageIdentities: new Dictionary<DbcMessageIdentity, DbcMessage>
            {
                [new(0x123, false)] = standard,
                [new(0x123, true)] = extended
            });
        return (db, speed);
    }

    [Fact]
    public void EditingScalarInWorkingCopyLeavesOriginalAndExtendedIdUntouched()
    {
        var (original, speed) = Sample();
        var copy = new DbcWorkingCopy(original);
        copy.StageSignal(new(0x123, false), "Speed",
            speed with { Factor = 0.02, StartBit = 16, Unit = "m/s" });
        Assert.True(copy.IsModified);
        var preview = copy.Preview();
        Assert.Equal(2, preview.MessageDefinitionCount);
        Assert.Equal(0.02, preview.MessageIdentities![new(0x123, false)].Signals[0].Factor);
        Assert.Equal(16, preview.MessageIdentities[new(0x123, false)].Signals[0].StartBit);
        Assert.Equal(0.01, original.MessageIdentities![new(0x123, false)].Signals[0].Factor);
        Assert.Equal("State", preview.MessageIdentities[new(0x123, true)].Signals[0].Name);
        Assert.Single(copy.Changes);
    }

    [Fact]
    public void OutOfBoundsAndInvalidScalingDoNotChangeWorkingCopy()
    {
        var (db, speed) = Sample();
        var copy = new DbcWorkingCopy(db);
        Assert.Throws<ArgumentException>(() => copy.StageSignal(
            new(0x123, false), "Speed", speed with { StartBit = 60 }));
        Assert.Throws<ArgumentException>(() => copy.StageSignal(
            new(0x123, false), "Speed", speed with { Factor = double.NaN }));
        Assert.Throws<ArgumentException>(() => copy.StageSignal(
            new(0x123, false), "Speed", speed with { Minimum = 500, Maximum = 200 }));
        Assert.False(copy.IsModified);
    }

    [Fact]
    public void RenamesAndUnreviewedMuxEditsAreBlocked()
    {
        var (db, speed) = Sample();
        var copy = new DbcWorkingCopy(db);
        Assert.Throws<InvalidOperationException>(() => copy.StageSignal(
            new(0x123, false), "Speed", speed with { Name = "SpeedChanged" }));
        Assert.Throws<InvalidOperationException>(() => copy.StageSignal(
            new(0x123, false), "Speed", speed with { IsMultiplexor = true }));
        Assert.Empty(copy.Changes);
    }

    [Fact]
    public void RevertRestoresOriginalAndDiscardAllResetsWorkingCopy()
    {
        var (db, speed) = Sample();
        var copy = new DbcWorkingCopy(db);
        copy.StageSignal(new(0x123, false), "Speed", speed with { Factor = 0.04 });
        Assert.True(copy.IsModified);
        copy.RevertSignal(new(0x123, false), "Speed");
        Assert.False(copy.IsModified);
        copy.StageSignal(new(0x123, false), "Speed", speed with { Offset = 4 });
        copy.RevertAll();
        Assert.False(copy.IsModified);
        Assert.Equal(0, copy.Preview().MessageIdentities![new(0x123, false)].Signals[0].Offset);
    }

    [Fact]
    public void ValueTableAndScalarChangesRevertTogetherWithoutModifyingOriginal()
    {
        var (db, speed) = Sample();
        var copy = new DbcWorkingCopy(db);
        var id = new DbcMessageIdentity(0x123, false);
        copy.StageSignal(id, "Speed", speed with { Factor = 0.02 });
        copy.StageNamedValues(id, "Speed",
            new Dictionary<long, string> { [0] = "Idle", [1] = "Moving" });
        Assert.Single(copy.Changes);
        Assert.Equal(0.02, copy.Preview().MessageIdentities![id].Signals[0].Factor);
        Assert.Equal("Idle", copy.Preview().MessageIdentities![id].Signals[0].ValueDescriptions![0]);
        Assert.Equal("Stopped", db.MessageIdentities![id].Signals[0].ValueDescriptions![0]);
        copy.RevertSignal(id, "Speed");
        Assert.False(copy.IsModified);
        Assert.Equal(0.01, copy.Preview().MessageIdentities![id].Signals[0].Factor);
    }

    [Fact]
    public void EquivalentValueTableDoesNotCreateDirtyDraft()
    {
        var (db, _) = Sample();
        var copy = new DbcWorkingCopy(db);
        copy.StageNamedValues(new(0x123, false), "Speed",
            new Dictionary<long, string> { [0] = "Stopped" });
        Assert.False(copy.IsModified);
        Assert.Empty(copy.Changes);
    }

}
