namespace RbgCanInsight.Dbc;

/// <summary>Checks applied when a database is added next to ones that are already loaded. Results are plain
/// strings so they travel through the existing <see cref="DbcDatabase.ParseWarnings"/> list and show up as
/// the warning badge in the DBC Manager.</summary>
public static class DbcLoadChecks
{
    private const int MaxReported = 5;

    /// <summary>True when the file produced no message definitions at all, so it is not usable as a database.</summary>
    public static bool HasNoMessages(DbcDatabase database) => database.MessageDefinitionCount == 0;

    /// <summary>Warnings for message IDs that <paramref name="candidate"/> defines with a different name than a
    /// database that is already loaded, so signals of both would appear twice in decoded views. Databases
    /// pinned to different channels may legitimately reuse an ID: when <paramref name="canShareChannel"/> is given,
    /// only databases it accepts (those that can apply to the same channel as the candidate) are compared.</summary>
    public static IReadOnlyList<string> FindDuplicateMessageIds(
        DbcDatabase candidate, IEnumerable<DbcDatabase> loaded,
        Func<DbcDatabase, bool>? canShareChannel = null)
    {
        var warnings = new List<string>();
        var total = 0;
        var existing = loaded.Where(item => !string.Equals(item.Name, candidate.Name, StringComparison.Ordinal))
            .Where(item => canShareChannel is null || canShareChannel(item)).ToArray();
        foreach (var message in candidate.AllMessageDefinitions.OrderBy(item => item.Identifier))
        {
            foreach (var other in existing)
            {
                if (!other.TryGetMessage(message.Identifier, message.IsExtended, out var clash))
                    continue;
                total++;
                if (total <= MaxReported)
                {
                    var id = $"0x{message.Identifier:X}";
                    warnings.Add(string.Equals(clash.Name, message.Name, StringComparison.Ordinal)
                        ? $"Message ID {id} ('{message.Name}') is also defined in '{other.Name}'. Decoded signals may appear twice unless the databases are assigned to different channels."
                        : $"Message ID {id} is '{message.Name}' here but '{clash.Name}' in '{other.Name}'. Decoded signals may appear twice unless the databases are assigned to different channels.");
                }
            }
        }

        if (total > MaxReported)
            warnings.Add($"{total - MaxReported} more message ID(s) are also defined in other loaded databases.");
        return warnings;
    }
}
