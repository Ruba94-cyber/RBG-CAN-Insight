using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Exact regression numbers from the odxtools <c>somersault.pdx</c> sample (optional, git-ignored).</summary>
public sealed class OdxRealSampleTests
{
    private static string? FindSomersault()
    {
        var overridden = Environment.GetEnvironmentVariable("RBG_ODX_REAL_DIR");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            var p = Path.Combine(overridden, "somersault.pdx");
            if (File.Exists(p)) return p;
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "artifacts", "online-samples", "odx", "somersault.pdx");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    [Fact]
    public void SomersaultVariantsGetTheirEffectiveServicesThroughInheritance()
    {
        var path = FindSomersault();
        if (path is null) return;
        var catalogs = DidCatalog.LoadAll(path).ToDictionary(c => c.Ecu);

        Assert.Equal(["somersault_assiduous", "somersault_lazy"], catalogs.Keys.Order().ToArray());
        Assert.Equal(
            ["session_start", "session_stop", "tester_present", "do_forward_flips", "report_status"],
            catalogs["somersault_lazy"].ServiceDefinitions.Select(s => s.Name).ToArray());
        Assert.Equal(8, catalogs["somersault_assiduous"].ServiceDefinitions.Count);
        // The variant adds services on top of what the lazy variant has, never fewer.
        Assert.Subset(
            catalogs["somersault_assiduous"].ServiceDefinitions.Select(s => s.Name).ToHashSet(),
            catalogs["somersault_lazy"].ServiceDefinitions.Select(s => s.Name).ToHashSet());
        Assert.Equal(3, catalogs["somersault_lazy"].VariantDefinitions.Count);
        Assert.Equal(2, catalogs["somersault_lazy"].ConditionDefinitions.Count);
    }
}
