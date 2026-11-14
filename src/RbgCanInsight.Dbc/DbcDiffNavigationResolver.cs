namespace RbgCanInsight.Dbc;

/// <summary>One exact loaded DBC frame/signal inspection target. Source
/// provenance is a path plus the parsed snapshot, not just a matching filename.</summary>
public sealed record DbcDiffNavigationTarget(
    uint Identifier, bool IsExtended, string MessageName, string SignalName);

public sealed record DbcDiffNavigationResult(
    DbcDiffNavigationTarget? Target, string Explanation)
{
    public bool CanNavigate => Target is not null;
}

/// <summary>
/// Pure guard used before navigating a DBC comparison to a loaded database.
/// Protects against accidental reuse of an identically named DBC from another
/// folder, changed source definitions, standard/extended collisions, and
/// duplicated signal names. No source files or active decoders are modified.
/// This compares the parsed model; unknown vendor syntax remains unverified.
/// </summary>
public static class DbcDiffNavigationResolver
{
    public static DbcDiffNavigationResult Resolve(
        DbcDefinitionDiffRow row, bool currentVersion,
        DbcDatabase comparedVersion, string comparedFile,
        DbcDatabase loaded, string loadedFile)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(comparedVersion);
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentException.ThrowIfNullOrWhiteSpace(comparedFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(loadedFile);

        if (row.Change == DbcDefinitionChange.Ambiguous ||
            row.Scope is not ("Signal" or "Message"))
            return No("An ambiguous or unsupported CAN definition cannot be opened automatically.");
        if ((currentVersion && row.Change == DbcDefinitionChange.Removed) ||
            (!currentVersion && row.Change == DbcDefinitionChange.Added))
            return No("The selected definition is absent in the requested DBC version.");

        if (!string.Equals(Path.GetFullPath(comparedFile), Path.GetFullPath(loadedFile),
                StringComparison.OrdinalIgnoreCase))
            return No("The compared version was not loaded from this exact source file.");

        var differences = DbcDefinitionComparer.Compare(comparedVersion, loaded);
        if (differences.Rows.Any(diff => diff.Change != DbcDefinitionChange.Unchanged) ||
            !comparedVersion.ParseWarnings.SequenceEqual(loaded.ParseWarnings) ||
            !comparedVersion.RawAttributeDeclarations.SequenceEqual(loaded.RawAttributeDeclarations))
            return No("The loaded parsed definitions differ from this comparison snapshot.");

        if (!loaded.TryGetMessage(row.Identifier, row.IsExtended, out var message))
            return No("The exact CAN identifier and 11/29-bit frame format is absent.");

        var expectedName = currentVersion ? row.CurrentMessage : row.BaselineMessage;
        if (!string.Equals(message.Name, expectedName, StringComparison.Ordinal))
            return No("The CAN message name does not match this comparison version.");

        if (row.Scope == "Signal" && message.Signals.Count(signal =>
                string.Equals(signal.Name, row.Signal, StringComparison.Ordinal)) != 1)
            return No("The selected signal is absent or duplicated in the target frame.");

        return new DbcDiffNavigationResult(
            new DbcDiffNavigationTarget(message.Identifier, message.IsExtended,
                message.Name, row.Scope == "Signal" ? row.Signal : string.Empty),
            string.Empty);
    }

    private static DbcDiffNavigationResult No(string reason) => new(null, reason);
}
