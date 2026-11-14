using RbgCanInsight.Dbc;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Exact, isolated history of validated DBC working-copy operations.
/// The source database must remain immutable across every undo/redo branch.</summary>
public sealed class DbcUndoRedoTests
{
    private static readonly DbcMessageIdentity Standard = new(0x123, false);
    private static readonly DbcMessageIdentity Extended = new(0x123, true);

    private static (DbcDatabase Database, DbcWorkingCopy Copy) Create()
    {
        var main = new DbcSignal("Speed", 0, 16, DbcByteOrder.Intel, false,
            0.01, 0, 0, 250, "km/h", "BCM",
            ValueDescriptions: new Dictionary<long, string> { [0] = "Stopped" });
        var extra = new DbcSignal("State", 0, 8, DbcByteOrder.Intel, false,
            1, 0, 0, 255, "", "ECM");
        var standard = new DbcMessage(0x123, "Standard", 8, "ECM", false, [main]);
        var extended = new DbcMessage(0x123, "Extended", 8, "ECM", true, [extra]);
        var original = new DbcDatabase("Synthetic",
            new Dictionary<uint, DbcMessage> { [0x123] = standard },
            MessageIdentities: new Dictionary<DbcMessageIdentity, DbcMessage>
            {
                [Standard] = standard,
                [Extended] = extended
            });
        return (original, new DbcWorkingCopy(original));
    }

    private static DbcSignal Signal(DbcWorkingCopy copy, DbcMessageIdentity id) =>
        Assert.Single(copy.Preview().MessageIdentities![id].Signals);

    [Fact]
    public void ScalarStageUndoAndRedoRestoreExactStagedStateAndSourceStaysOriginal()
    {
        var (database, copy) = Create();
        var original = Signal(copy, Standard);
        Assert.False(copy.CanUndo);
        Assert.False(copy.CanRedo);
        Assert.False(copy.Undo());
        Assert.False(copy.Redo());

        copy.StageSignal(Standard, "Speed", original with { Offset = 10, Unit = "mph" });
        Assert.True(copy.IsModified);
        Assert.True(copy.CanUndo);
        Assert.False(copy.CanRedo);
        Assert.Equal(10, Signal(copy, Standard).Offset);
        Assert.True(copy.Undo());
        Assert.False(copy.IsModified);
        Assert.True(copy.CanRedo);
        Assert.Equal(0, Signal(copy, Standard).Offset);
        Assert.True(copy.Redo());
        Assert.Equal(10, Signal(copy, Standard).Offset);
        Assert.Equal("mph", Signal(copy, Standard).Unit);
        Assert.True(copy.IsModified);
        Assert.Equal(0, database.MessageIdentities![Standard].Signals[0].Offset);
    }

    [Fact]
    public void ValueTableAndScalarChangesUndoInCorrectOrder()
    {
        var (_, copy) = Create();
        var initial = Signal(copy, Standard);
        copy.StageSignal(Standard, "Speed", initial with { Factor = 0.02 });
        copy.StageNamedValues(Standard, "Speed", new Dictionary<long, string>
        {
            [0] = "Off",
            [1] = "On"
        });
        Assert.Single(copy.Changes);
        Assert.Equal("On", Signal(copy, Standard).ValueDescriptions![1]);
        Assert.True(copy.Undo());
        Assert.Equal(0.02, Signal(copy, Standard).Factor);
        Assert.Equal("Stopped", Signal(copy, Standard).ValueDescriptions![0]);
        Assert.Single(copy.Changes);
        Assert.True(copy.Undo());
        Assert.False(copy.IsModified);
        Assert.Equal(0.01, Signal(copy, Standard).Factor);
        Assert.True(copy.Redo());
        Assert.True(copy.Redo());
        Assert.Equal("On", Signal(copy, Standard).ValueDescriptions![1]);
        Assert.Equal(0.02, Assert.Single(copy.Changes).After.Factor);
    }

    [Fact]
    public void NewStageAfterUndoClearsRedoWithoutResurrectingOldChange()
    {
        var (_, copy) = Create();
        var original = Signal(copy, Standard);
        copy.StageSignal(Standard, "Speed", original with { Offset = 1 });
        copy.StageSignal(Standard, "Speed", Signal(copy, Standard) with { Offset = 2 });
        Assert.True(copy.Undo());
        Assert.True(copy.CanRedo);
        copy.StageSignal(Standard, "Speed", Signal(copy, Standard) with { Offset = 3 });
        Assert.False(copy.CanRedo);
        Assert.False(copy.Redo());
        Assert.Equal(3, Signal(copy, Standard).Offset);
        Assert.True(copy.Undo());
        Assert.Equal(1, Signal(copy, Standard).Offset);
    }

    [Fact]
    public void UndoNeverConfusesStandardWithExtendedCanIdentity()
    {
        var (_, copy) = Create();
        copy.StageSignal(Standard, "Speed", Signal(copy, Standard) with { Offset = 10 });
        copy.StageSignal(Extended, "State", Signal(copy, Extended) with { Offset = -5 });
        Assert.Equal(-5, Signal(copy, Extended).Offset);
        Assert.Equal(10, Signal(copy, Standard).Offset);
        Assert.True(copy.Undo());
        Assert.Equal(0, Signal(copy, Extended).Offset);
        Assert.Equal(10, Signal(copy, Standard).Offset);
        Assert.True(copy.Undo());
        Assert.Equal(0, Signal(copy, Standard).Offset);
        Assert.False(copy.IsModified);
    }

    [Fact]
    public void RevertOfStagedSignalIsUndoableAndRedoable()
    {
        var (_, copy) = Create();
        copy.StageNamedValues(Standard, "Speed",
            new Dictionary<long, string> { [0] = "Idle", [2] = "Running" });
        copy.RevertSignal(Standard, "Speed");
        Assert.False(copy.IsModified);
        Assert.Equal("Stopped", Signal(copy, Standard).ValueDescriptions![0]);
        Assert.True(copy.Undo());
        Assert.True(copy.IsModified);
        Assert.Equal("Running", Signal(copy, Standard).ValueDescriptions![2]);
        Assert.True(copy.Redo());
        Assert.False(copy.IsModified);
    }

    [Fact]
    public void InvalidOrNoOpStagesDoNotPolluteHistoryOrDeleteRedo()
    {
        var (_, copy) = Create();
        var initial = Signal(copy, Standard);
        copy.StageSignal(Standard, "Speed", initial with { Offset = 1 });
        Assert.True(copy.Undo());
        Assert.True(copy.CanRedo);
        copy.StageSignal(Standard, "Speed", initial);
        Assert.True(copy.CanRedo);
        Assert.Throws<ArgumentException>(() =>
            copy.StageSignal(Standard, "Speed", initial with { StartBit = 70 }));
        Assert.True(copy.CanRedo);
        Assert.False(copy.CanUndo);
    }

    [Fact]
    public void ExplicitDiscardAllClearsHistoryAndCannotRedoPreviouslyDiscardedEdits()
    {
        var (_, copy) = Create();
        copy.StageSignal(Standard, "Speed", Signal(copy, Standard) with { Factor = 0.1 });
        copy.StageSignal(Extended, "State", Signal(copy, Extended) with { Offset = 3 });
        copy.Undo();
        Assert.True(copy.CanRedo);
        copy.RevertAll();
        Assert.False(copy.CanRedo);
        Assert.False(copy.CanUndo);
        Assert.False(copy.IsModified);
        Assert.Equal(0.01, Signal(copy, Standard).Factor);
        Assert.Equal(0, Signal(copy, Extended).Offset);
    }

    [Fact]
    public void BoundedHistoryKeepsNewest128EditsAndCanUndoThemWithoutOverwritingSource()
    {
        var (source, copy) = Create();
        for (var i = 1; i <= 135; i++)
            copy.StageSignal(Standard, "Speed",
                Signal(copy, Standard) with { Offset = i });
        for (var i = 0; i < 128; i++)
            Assert.True(copy.Undo());
        Assert.False(copy.Undo());
        Assert.Equal(7, Signal(copy, Standard).Offset);
        Assert.Equal(0, source.MessageIdentities![Standard].Signals[0].Offset);
        for (var i = 0; i < 128; i++)
            Assert.True(copy.Redo());
        Assert.False(copy.Redo());
        Assert.Equal(135, Signal(copy, Standard).Offset);
    }
}
