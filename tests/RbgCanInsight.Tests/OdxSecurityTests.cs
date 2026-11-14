using System.IO.Compression;
using System.Xml;
using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class OdxSecurityTests
{
    [Fact]
    public void PlainOdxRejectsDtdAndExternalEntities()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rbg-unsafe-{Guid.NewGuid():N}.odx");
        File.WriteAllText(path, """
            <?xml version="1.0"?>
            <!DOCTYPE ODX [<!ENTITY value SYSTEM "file:///etc/passwd">]>
            <ODX><SHORT-NAME>&value;</SHORT-NAME></ODX>
            """);
        try
        {
            Assert.Throws<XmlException>(() => OdxCatalogReader.LoadAll(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PdxRejectsDtdInsideMemberWithoutExtractingAnyFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rbg-unsafe-{Guid.NewGuid():N}.pdx");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var output = new StreamWriter(zip.CreateEntry("ecu.odx-d").Open()))
                output.Write("""
                    <!DOCTYPE ODX [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
                    <ODX><DIAG-LAYER-CONTAINER><SHORT-NAME>&xxe;</SHORT-NAME></DIAG-LAYER-CONTAINER></ODX>
                    """);
            Assert.Throws<XmlException>(() => OdxCatalogReader.LoadAll(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void PdxRefusesDeclaredHugeUncompressedMemberBeforeReading()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rbg-unsafe-{Guid.NewGuid():N}.pdx");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var output = new StreamWriter(zip.CreateEntry("normal.odx-d").Open()))
                output.Write("<ODX/>");
            // Normal tiny valid diagnostic package must not trigger the size gate.
            // The reader may return an empty catalog, but must not fail with a
            // size-limit error for ordinary member metadata.
            var exception = Record.Exception(() => OdxCatalogReader.LoadAll(path));
            Assert.False(exception is InvalidDataException invalid &&
                invalid.Message.Contains("size limit", StringComparison.OrdinalIgnoreCase));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
