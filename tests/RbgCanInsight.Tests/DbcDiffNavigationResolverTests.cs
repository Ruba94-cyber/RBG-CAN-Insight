using RbgCanInsight.Dbc;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DbcDiffNavigationResolverTests
{
    private static DbcDatabase Parse(string text) =>
        new DbcParser().Parse("network", new StringReader(text));

    private static readonly string Original = """
        BO_ 256 Engine: 8 ECU
         SG_ Speed : 0|16@1+ (0.01,0) [0|250] "km/h" Dash
         SG_ State : 16|8@1+ (1,0) [0|10] "" Dash
        """;

    private static readonly string Updated = """
        BO_ 256 Engine: 8 ECU
         SG_ Speed : 0|16@1+ (0.02,0) [0|250] "km/h" Dash
         SG_ State : 16|8@1+ (1,0) [0|10] "" Dash
        """;

    private static DbcDefinitionDiffRow SpeedDiff(DbcDatabase baseline, DbcDatabase current) =>
        Assert.Single(DbcDefinitionComparer.Compare(baseline, current).Rows
            .Where(row => row.Scope == "Signal" && row.Signal == "Speed"));

    [Fact]
    public void OldVersionLoadsItsExactSignalWithoutConfusingRevisedScaling()
    {
        var old = Parse(Original);
        var current = Parse(Updated);
        var diff = SpeedDiff(old, current);
        var baseline = DbcDiffNavigationResolver.Resolve(diff, currentVersion: false,
            old, "/sources/original.dbc", old, "/sources/original.dbc");
        Assert.True(baseline.CanNavigate);
        Assert.Equal(256u, baseline.Target!.Identifier);
        Assert.Equal("Speed", baseline.Target.SignalName);
        Assert.False(baseline.Target.IsExtended);
        var changed = DbcDiffNavigationResolver.Resolve(diff, currentVersion: true,
            current, "/sources/current.dbc", current, "/sources/current.dbc");
        Assert.True(changed.CanNavigate);
    }

    [Fact]
    public void IdenticalNamesInAnotherFolderNeverCountAsSameSource()
    {
        var before = Parse(Original);
        var current = Parse(Updated);
        var diff = SpeedDiff(before, current);
        var wrong = DbcDiffNavigationResolver.Resolve(diff, false, before,
            "/approved/engine.dbc", before, "/other/engine.dbc");
        Assert.False(wrong.CanNavigate);
        Assert.Contains("exact source", wrong.Explanation);
    }

    [Fact]
    public void SamePathWithModifiedLoadedSignalIsRefused()
    {
        var baseline = Parse(Original);
        var modified = Parse(Updated);
        var diff = SpeedDiff(baseline, modified);
        var result = DbcDiffNavigationResolver.Resolve(diff, false, baseline,
            "/source/engine.dbc", modified, "/source/engine.dbc");
        Assert.False(result.CanNavigate);
        Assert.Contains("parsed definitions differ", result.Explanation);
    }

    [Fact]
    public void AddedSignalCannotBeInspectedInBaselineVersion()
    {
        var old = Parse(Original);
        var current = Parse(Original.Replace(
            " SG_ State", " SG_ NewFlag : 24|8@1+ (1,0) [0|1] \"\" Dash\n SG_ State",
            StringComparison.Ordinal));
        var row = Assert.Single(DbcDefinitionComparer.Compare(old, current).Rows
            .Where(item => item.Signal == "NewFlag"));
        Assert.Equal(DbcDefinitionChange.Added, row.Change);
        var result = DbcDiffNavigationResolver.Resolve(row, false, old,
            "/source/engine.dbc", old, "/source/engine.dbc");
        Assert.False(result.CanNavigate);
        Assert.Contains("absent", result.Explanation);
    }

    [Fact]
    public void NumericIdCannotNavigateAcrossStandardExtendedFormat()
    {
        var standard = Parse("""
            BO_ 256 Standard: 8 ECU
             SG_ Speed : 0|8@1+ (1,0) [0|200] "" ECU
            """);
        var extended = Parse("""
            BO_ 2147483904 Extended: 8 ECU
             SG_ Speed : 0|8@1+ (1,0) [0|200] "" ECU
            """);
        var row = Assert.Single(DbcDefinitionComparer.Compare(standard, extended).Rows
            .Where(item => item.Scope == "Signal" && item.Change == DbcDefinitionChange.Removed));
        var rejected = DbcDiffNavigationResolver.Resolve(row, false, standard,
            "/source/network.dbc", extended, "/source/network.dbc");
        Assert.False(rejected.CanNavigate);
    }
}
