using System.Xml;

namespace RbgCanInsight.TraceIO;

/// <summary>A load failure rendered once and reused for the dialog and the status bar so every file type
/// (trace, CAN database, diagnostic description) reports problems the same way.</summary>
/// <param name="Title">Dialog caption.</param>
/// <param name="Message">Plain-English dialog text: what was wrong, which file, and what happened to existing data.</param>
/// <param name="StatusLine">One-line version for the status bar.</param>
public sealed record LoadFailure(string Title, string Message, string StatusLine);

public static class LoadFailureReport
{
    /// <summary>The importer ran but produced no frames (empty file, wrong format, only unsupported records).</summary>
    public static LoadFailure NoUsableFrames(string fileName, string healthSummary, bool previousTraceKept)
    {
        var kept = previousTraceKept ? "The trace that was already open was kept." : "No trace is loaded.";
        var detail = string.IsNullOrWhiteSpace(healthSummary) ? string.Empty : Environment.NewLine + Environment.NewLine + healthSummary.Trim();
        return new LoadFailure(
            "Trace not loaded",
            $"No usable CAN frames were found in {fileName}. The file may be empty, damaged or not a supported trace format. {kept}{detail}",
            $"{fileName}: no usable CAN frames found. {kept}");
    }

    /// <summary>The importer threw before the capture was committed.</summary>
    public static LoadFailure Trace(string fileName, Exception exception, long framesRead, bool previousTraceKept)
    {
        var kept = previousTraceKept ? "The trace that was already open was kept." : "No trace is loaded.";
        var partial = framesRead > 0
            ? $" {framesRead:N0} frame(s) were read before the failure but were not loaded as a complete capture."
            : string.Empty;
        var reason = Describe(exception, "trace");
        return new LoadFailure(
            "Trace not loaded",
            $"{fileName} could not be loaded: {reason}{partial} {kept}",
            $"{fileName} could not be loaded: {reason} {kept}");
    }

    /// <summary>A CDD/ODX/PDX/DTC table could not be read. Leads with a human sentence; the technical
    /// parser message follows below it.</summary>
    public static LoadFailure DiagnosticFile(string fileName, Exception exception)
    {
        var reason = Describe(exception, "diagnostic description");
        var technical = exception is XmlException or InvalidDataException
            ? Environment.NewLine + Environment.NewLine + "Technical detail: " + exception.Message
            : string.Empty;
        return new LoadFailure(
            "Diagnostic file not loaded",
            $"{fileName} could not be loaded: {reason} Existing diagnostic data was not changed.{technical}",
            $"{fileName} could not be loaded: {reason}");
    }

    /// <summary>A DBC/ARXML file could not be read.</summary>
    public static LoadFailure Database(string fileName, Exception exception)
    {
        var reason = Describe(exception, "CAN database");
        return new LoadFailure(
            "CAN database not loaded",
            $"{fileName} could not be loaded: {reason} Loaded databases were not changed.",
            $"{fileName} could not be loaded: {reason}");
    }

    private static string Describe(Exception exception, string kind) => exception switch
    {
        XmlException => $"it is not a valid XML {kind} file (the XML is incomplete or malformed).",
        FileNotFoundException or DirectoryNotFoundException => "the file was not found. It may have been moved or deleted.",
        UnauthorizedAccessException => "access to the file was denied.",
        IOException io => "the file could not be read (" + Sentence(io.Message).TrimEnd('.') + ").",
        _ => Sentence(exception.Message)
    };

    private static string Sentence(string message)
    {
        var text = message.Trim().ReplaceLineEndings(" ");
        if (text.Length == 0) return "unknown error.";
        return text.EndsWith('.') ? text : text + ".";
    }
}
