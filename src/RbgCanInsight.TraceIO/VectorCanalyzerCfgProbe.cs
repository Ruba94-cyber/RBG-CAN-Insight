namespace RbgCanInsight.TraceIO;

/// <summary>
/// Conservative recognition for a possible Vector CANalyzer configuration.
/// This is deliberately not a parser: Vector's public material confirms the
/// .cfg extension is used for configurations, but does not specify their file
/// serialization sufficiently to decode them safely.
/// </summary>
public static class VectorCanalyzerCfgProbe
{
    public static bool IsUnsupportedCandidate(string path) =>
        string.Equals(Path.GetExtension(path), ".cfg", StringComparison.OrdinalIgnoreCase);

    public static string DescribeUnsupported(string path)
    {
        if (!IsUnsupportedCandidate(path))
            throw new ArgumentException("The path does not have a .cfg extension.", nameof(path));

        return $"'{Path.GetFileName(path)}' has a .cfg extension and may be a CANalyzer configuration. " +
               "This file is not imported: its contents are not interpreted because a verifiable public " +
               "format specification is unavailable.";
    }
}
