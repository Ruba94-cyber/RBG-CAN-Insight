using System.Xml;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class LoadFailureReportTests
{
    private static XmlException BrokenXml()
    {
        try
        {
            new XmlDocument().LoadXml("<ODX><Name");
        }
        catch (XmlException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected the XML to be rejected.");
    }

    [Fact]
    public void BrokenXmlLeadsWithAHumanSentenceAndKeepsTheParserTextBelow()
    {
        var failure = LoadFailureReport.DiagnosticFile("broken.odx", BrokenXml());

        var firstLine = failure.Message.Split(Environment.NewLine)[0];
        Assert.StartsWith("broken.odx could not be loaded: it is not a valid XML diagnostic description file", firstLine);
        Assert.DoesNotContain("position", firstLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Technical detail:", failure.Message);
        Assert.Contains("Existing diagnostic data was not changed", failure.Message);
        Assert.DoesNotContain("position", failure.StatusLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyTraceNamesTheFileAndSaysWhetherThePreviousTraceWasKept()
    {
        var kept = LoadFailureReport.NoUsableFrames("garbage.asc", "0 frames", previousTraceKept: true);
        var none = LoadFailureReport.NoUsableFrames("garbage.asc", "", previousTraceKept: false);

        Assert.Contains("garbage.asc", kept.Message);
        Assert.Contains("was kept", kept.Message);
        Assert.Contains("was kept", kept.StatusLine);
        Assert.Contains("No trace is loaded", none.Message);
        Assert.Equal("Trace not loaded", kept.Title);
    }

    [Fact]
    public void TraceFailureReportsPartialReadAndFileName()
    {
        var failure = LoadFailureReport.Trace("random.blf", new InvalidDataException("Not a BLF file"), 12, previousTraceKept: true);

        Assert.Contains("random.blf could not be loaded", failure.Message);
        Assert.Contains("Not a BLF file.", failure.Message);
        Assert.Contains("12 frame(s)", failure.Message);
        Assert.Contains("was kept", failure.Message);
    }

    [Fact]
    public void MissingFileGetsAFriendlyReasonForEveryFileType()
    {
        var error = new FileNotFoundException("Could not find file 'C:\\x\\y.dbc'.");

        Assert.Contains("file was not found", LoadFailureReport.Database("y.dbc", error).Message);
        Assert.Contains("file was not found", LoadFailureReport.DiagnosticFile("y.odx", error).Message);
        Assert.Contains("file was not found", LoadFailureReport.Trace("y.blf", error, 0, false).Message);
    }
}
