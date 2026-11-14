using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>
/// Opens a real 1.1-million-frame BMW E65 BLF recording (downloaded from a public repository into the
/// git-ignored artifacts/online-samples/real folder; the test passes immediately when it is absent) and checks
/// that the window stays usable: it loads, jumps to the end, and filters, each within a generous wall-clock budget.
/// Timings are written to the test output so regressions are visible.
/// </summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiLargeRealTraceTests
{
    private static string? FindFile()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "artifacts", "online-samples", "real", "blf", "E65_Log2.blf");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    [Fact]
    public void MillionFrameRealBlfLoadsJumpsAndFiltersWithinBudget()
    {
        var file = FindFile();
        if (file is null) return;

        using var ui = new GuiSession();
        var clock = Stopwatch.StartNew();
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(file);
        ui.Until(() => ui.Status.Contains("Trace loaded:", StringComparison.OrdinalIgnoreCase) ? "ok" : null,
            "million-frame load", timeoutSeconds: 120);
        var load = clock.Elapsed;
        Assert.True(load < TimeSpan.FromSeconds(90), $"load took {load.TotalSeconds:F1} s");

        ui.ShowTraceReplayControls();
        clock.Restart();
        ui.Click("trace.tail");
        ui.Until(() => ui.Visible("trace.grid").FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length > 0
            ? "rows" : null, "rows after Tail", timeoutSeconds: 30);
        var tail = clock.Elapsed;
        Assert.True(tail < TimeSpan.FromSeconds(20), $"jump to end took {tail.TotalSeconds:F1} s");

        clock.Restart();
        ui.Fill("trace.search", "id:7DF");
        ui.Until(() => ui.Find("trace.search") is not null ? "ok" : null, "search accepted", timeoutSeconds: 30);
        Thread.Sleep(1500);
        var filter = clock.Elapsed;
        Assert.True(filter < TimeSpan.FromSeconds(30), $"filter took {filter.TotalSeconds:F1} s");

        Console.WriteLine($"[perf] load {load.TotalSeconds:F1}s, tail {tail.TotalSeconds:F1}s, filter {filter.TotalSeconds:F1}s");
    }
}
