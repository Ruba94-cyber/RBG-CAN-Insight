using System.Security.Cryptography;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Imports the real third-party MF4 logger files when present (artifacts/ is not versioned,
/// so the test is a no-op on a clean checkout; set RBG_MF4_SAMPLES to point at the folder).
/// Compares a fingerprint of every imported file against RBG_MF4_FINGERPRINT_BASE when given.</summary>
public sealed class Mdf4RealSamples
{
    internal static string? FindDir()
    {
        var env = Environment.GetEnvironmentVariable("RBG_MF4_SAMPLES");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "artifacts", "online-samples", "real");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    internal static async Task<string> Fingerprint(string file)
    {
        var count = 0L;
        var previous = long.MinValue;
        var ordered = true;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await foreach (var f in Mdf4Importer.ReadAsync(file))
        {
            count++;
            ordered &= f.TimestampNanoseconds >= previous;
            previous = f.TimestampNanoseconds;
            hash.AppendData(BitConverter.GetBytes(f.TimestampNanoseconds));
            hash.AppendData(BitConverter.GetBytes(f.Identifier));
            hash.AppendData(BitConverter.GetBytes(f.Channel));
            hash.AppendData(f.GetPayload());
        }
        return $"{count}|{ordered}|{Convert.ToHexString(hash.GetHashAndReset())[..16]}";
    }

    [Fact]
    public async Task RealSamplesImportAndMatchRecordedFingerprints()
    {
        var dir = FindDir();
        if (dir is null) return;
        var lines = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".mf4", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string result;
            try { result = await Fingerprint(file); }
            catch (NotSupportedException) { result = "UNSUPPORTED"; } // signal-only MF4 is out of scope
            catch (Exception ex) { result = "ERR " + ex.GetType().Name; }
            lines.Add(Path.GetFileName(file) + "=" + result);
        }
        var outPath = Environment.GetEnvironmentVariable("RBG_MF4_FINGERPRINT_OUT");
        if (!string.IsNullOrEmpty(outPath)) File.WriteAllLines(outPath, lines);
        Assert.DoesNotContain(lines, l => l.Contains("=ERR"));
        var basePath = Environment.GetEnvironmentVariable("RBG_MF4_FINGERPRINT_BASE");
        if (!string.IsNullOrEmpty(basePath) && File.Exists(basePath))
            Assert.Equal(File.ReadAllLines(basePath), lines);
    }
}
