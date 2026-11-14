using System.IO.Compression;
using System.Xml.Linq;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Read-only version comparisons and actual trace export are
/// exercised through the real UI and the Windows common file dialogs.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiExportComparisonWorkflows
{
    [Fact]
    public void DbcVersionComparison_ExportsReadOnlyWorkbookWithoutChangingEitherDbc()
    {
        using var ui = new GuiSession();
        var modified = Path.Combine(ui.Root, "changed.dbc");
        var original = File.ReadAllText(ui.DbcPath);
        File.WriteAllText(modified, original.Replace("(0.5,0)", "(0.75,0)",
            StringComparison.Ordinal));
        Assert.NotEqual(original, File.ReadAllText(modified));

        ui.Navigate("nav.dbc");
        ui.Click("dbc.compare");
        ui.ChooseFile(ui.DbcPath);
        ui.ChooseFile(modified);
        ui.AwaitStatus("Read-only DBC comparison ready");

        var destination = Path.Combine(ui.Root, "dbc-difference.xlsx");
        ui.Click("dbc.exportDiff");
        ui.ChooseFile(destination);
        ui.AwaitExport(destination);
        Assert.True(CountSheets(destination) >= 2);
        Assert.Equal(original, File.ReadAllText(ui.DbcPath));
        Assert.Contains("(0.75,0)", File.ReadAllText(modified));
    }

    [Fact]
    public void OdxVersionComparison_ExportsDifferencesWithoutModifyingEitherCatalog()
    {
        using var ui = new GuiSession();
        var original = File.ReadAllText(ui.OdxPath);
        var modifiedPath = Path.Combine(ui.Root, "changed.odx");
        File.WriteAllText(modifiedPath,
            original.Replace("Read_EngineCoolantTemperature",
                "Read_EngineCoolantTemperatureV2", StringComparison.Ordinal));
        Assert.NotEqual(original, File.ReadAllText(modifiedPath));
        ui.Navigate("nav.uds");
        ui.Click("uds.openCatalog");
        ui.Click("diagnostics.compare");
        ui.ChooseFile(ui.OdxPath);
        ui.ChooseFile(modifiedPath);
        ui.AwaitStatus("Read-only CDD/ODX comparison ready");

        var destination = Path.Combine(ui.Root, "diagnostic-difference.xlsx");
        ui.Click("diagnostics.exportDiff");
        ui.ChooseFile(destination);
        ui.AwaitExport(destination);
        Assert.True(CountSheets(destination) >= 2);
        Assert.Equal(original, File.ReadAllText(ui.OdxPath));
    }

    [Fact]
    public void TraceExport_UsesActualFilteredTraceFileDialogAndPreservesOriginal()
    {
        using var ui = new GuiSession();
        var original = File.ReadAllText(ui.TracePath);
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        // ASC is the first native Save dialog type; choosing the same extension
        // avoids conflating filtering correctness with native file-type selection.
        var exported = Path.Combine(ui.Root, "visible-trace.asc");
        ui.Click("trace.exportVisible");
        ui.ChooseFile(exported);
        ui.Until(() => File.Exists(exported) && new FileInfo(exported).Length > 70
            ? "saved" : null, "exported ASC frames", 30);
        var written = File.ReadAllText(exported);
        Assert.Contains("100", written);
        Assert.Contains("200", written);
        Assert.Equal(original, File.ReadAllText(ui.TracePath));
    }

    private static int CountSheets(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry("xl/workbook.xml")!.Open();
        return XDocument.Load(stream).Descendants()
            .Count(element => element.Name.LocalName == "sheet");
    }
}
