using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>
/// Imports every real-world file found under artifacts/online-samples/real (git-ignored, downloaded
/// from public sources, see docs/REAL_FILE_VALIDATION_2026-11-08.md). Skipped silently when absent.
/// python-can fixtures are excluded: several of them are synthetic and contain zero valid frames by design.
/// </summary>
public sealed class RealFileSmokeTests
{
    private static string? RealRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "artifacts", "online-samples", "real");
            if (Directory.Exists(candidate)) return candidate;
        }

        return null;
    }

    [Fact]
    public async Task EveryRealTraceFileImportsAtLeastOneFrame()
    {
        var root = RealRoot();
        if (root is null) return;
        var extensions = new[] { ".blf", ".mf4", ".asc", ".log", ".pcapng" };
        var failures = new List<string>();
        var count = 0;
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                     .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                     .Where(f => !Path.GetFileName(f).StartsWith("pythoncan_", StringComparison.OrdinalIgnoreCase))
                     // LIN-only captures hold no CAN frames by design; they are covered by RealLinSampleTests.
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "lin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            count++;
            try
            {
                var frames = 0;
                await foreach (var _ in TraceImporter.ReadAsync(file)) frames++;
                if (frames == 0) failures.Add($"{Path.GetFileName(file)}: 0 frames");
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.GetType().Name} {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {count} real files failed:\n" + string.Join("\n", failures));
    }
}
