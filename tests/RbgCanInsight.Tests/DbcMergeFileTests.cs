using RbgCanInsight.Dbc;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DbcMergeFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rbg-merge-" + Guid.NewGuid().ToString("N"));

    public DbcMergeFileTests() => Directory.CreateDirectory(_folder);
    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    private string Write(string name, string text, bool bom = false)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, new System.Text.UTF8Encoding(bom).GetPreamble()
            .Concat(new System.Text.UTF8Encoding(false).GetBytes(text)).ToArray());
        return path;
    }

    private const string Base = "VERSION \"\"\n\nBU_: ECM\n\nBO_ 256 A: 8 ECM\n SG_ S1 : 0|8@1+ (1,0) [0|255] \"\" Vector__XXX\n\n";
    private const string Incoming = "VERSION \"\"\n\nBU_: ECM\n\nBO_ 256 A: 8 ECM\n SG_ S1 : 0|8@1+ (1,0) [0|255] \"\" Vector__XXX\n SG_ S2 : 8|8@1+ (1,0) [0|255] \"\" Vector__XXX\n\n";

    [Fact]
    public void WritesNewFileKeepsBomAndNeverTouchesEitherSource()
    {
        var basePath = Write("base.dbc", Base, bom: true);
        var incomingPath = Write("incoming.dbc", Incoming);
        var baseBytes = File.ReadAllBytes(basePath);
        var incomingBytes = File.ReadAllBytes(incomingPath);
        var destination = Path.Combine(_folder, "merged.dbc");

        var result = DbcMerge.WriteMergedFile(basePath, incomingPath, destination);

        Assert.True(result.IsValid);
        var written = File.ReadAllBytes(destination);
        Assert.Equal([0xEF, 0xBB, 0xBF], written[..3]);                      // BOM of the base is preserved
        Assert.Contains("SG_ S2", System.Text.Encoding.UTF8.GetString(written));
        Assert.Equal(baseBytes, File.ReadAllBytes(basePath));
        Assert.Equal(incomingBytes, File.ReadAllBytes(incomingPath));
        Assert.Empty(Directory.GetFiles(_folder, "*.partial-*"));
    }

    [Fact]
    public void RefusesToOverwriteSourcesOrExistingFiles()
    {
        var basePath = Write("base.dbc", Base);
        var incomingPath = Write("incoming.dbc", Incoming);
        var existing = Write("existing.dbc", "x");
        Assert.Throws<IOException>(() => DbcMerge.WriteMergedFile(basePath, incomingPath, basePath));
        Assert.Throws<IOException>(() => DbcMerge.WriteMergedFile(basePath, incomingPath, incomingPath));
        Assert.Throws<IOException>(() => DbcMerge.WriteMergedFile(basePath, incomingPath, existing));
        Assert.Equal("x", File.ReadAllText(existing));
        Assert.Throws<NotSupportedException>(() =>
            DbcMerge.WriteMergedFile(basePath, incomingPath, Path.Combine(_folder, "merged.txt")));
    }

    [Fact]
    public void NonUtf8SourceIsRejectedInsteadOfSilentlyAlteringCharacters()
    {
        var basePath = Path.Combine(_folder, "latin1.dbc");
        File.WriteAllBytes(basePath, System.Text.Encoding.Latin1.GetBytes(Base.Replace("ECM", "ECM") + "CM_ \"café\";\n"));
        var incomingPath = Write("incoming.dbc", Incoming);
        Assert.Throws<NotSupportedException>(() => DbcMerge.PlanFiles(basePath, incomingPath));
    }
}
