namespace RbgCanInsight.Diagnostics;

public enum DiagnosticStateChangeKind
{
    SessionChanged,
    SecurityUnlocked,
    SecurityLockedByReset,
    SecurityLockedBySession,
    EcuReset
}

/// <summary>A state change proven by a positive response recorded in the trace.</summary>
public sealed record DiagnosticStateChange(
    string Ecu,
    DiagnosticStateChangeKind Kind,
    long TimestampNanoseconds,
    int? Value,
    IReadOnlyList<int> FrameIndices,
    string Description);

/// <summary>Recorded state of one ECU at a point in time. Null means "not observed yet" — the
/// tracker never assumes a default session before the trace shows one.</summary>
public sealed record DiagnosticEcuState(int? Session, int? SecurityLevel, bool Known);

/// <summary>Reconstructs diagnostic session (0x10), reset (0x11) and security-access (0x27) state
/// from positive responses only. Negative or missing responses never change state, and state
/// before the first observation is reported as unknown rather than guessed.</summary>
public sealed class DiagnosticSessionStateTracker
{
    private readonly Dictionary<string, List<(long Time, DiagnosticStateChange Change)>> _byEcu;

    private DiagnosticSessionStateTracker(
        IReadOnlyList<DiagnosticStateChange> changes,
        Dictionary<string, List<(long, DiagnosticStateChange)>> byEcu)
    {
        Changes = changes;
        _byEcu = byEcu;
    }

    public IReadOnlyList<DiagnosticStateChange> Changes { get; }

    public static DiagnosticSessionStateTracker Reconstruct(IReadOnlyList<DiagnosticTransaction> transactions)
    {
        var changes = new List<DiagnosticStateChange>();
        foreach (var transaction in transactions.OrderBy(item => item.EndTimestampNanoseconds))
        {
            if (transaction.Outcome != DiagnosticOutcome.Positive ||
                transaction.Request?.Payload is not { Length: >= 2 } request)
                continue;
            var ecu = transaction.Endpoint.Ecu;
            var time = transaction.EndTimestampNanoseconds;
            var sub = request[1] & 0x7F;
            switch (transaction.Decoded.ServiceId)
            {
                case 0x10:
                    changes.Add(new(ecu, DiagnosticStateChangeKind.SessionChanged, time, sub,
                        transaction.FrameIndices, $"Diagnostic session 0x{sub:X2} confirmed by positive response."));
                    changes.Add(new(ecu, DiagnosticStateChangeKind.SecurityLockedBySession, time, null,
                        transaction.FrameIndices, "Security access re-locks when the session is (re)started."));
                    break;
                // enableRapidPowerShutDown (0x04) / disableRapidPowerShutDown (0x05) do not reset the ECU.
                case 0x11 when sub is not (0x04 or 0x05):
                    changes.Add(new(ecu, DiagnosticStateChangeKind.EcuReset, time, sub,
                        transaction.FrameIndices, $"ECU reset type 0x{sub:X2} confirmed."));
                    changes.Add(new(ecu, DiagnosticStateChangeKind.SecurityLockedByReset, time, null,
                        transaction.FrameIndices, "Security access is locked after an ECU reset."));
                    break;
                case 0x27 when sub > 0 && sub % 2 == 0:
                    // Even sub-function = sendKey for requestSeed level (sub - 1).
                    changes.Add(new(ecu, DiagnosticStateChangeKind.SecurityUnlocked, time, sub - 1,
                        transaction.FrameIndices,
                        $"Security level 0x{sub - 1:X2} unlocked (sendKey 0x{sub:X2} accepted)."));
                    break;
            }
        }

        var byEcu = changes.GroupBy(change => change.Ecu, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(change => change.TimestampNanoseconds)
                    .Select(change => (change.TimestampNanoseconds, change)).ToList(),
                StringComparer.OrdinalIgnoreCase);
        return new DiagnosticSessionStateTracker(changes, byEcu);
    }

    /// <summary>State of the ECU strictly after all changes at or before <paramref name="timestampNanoseconds"/>.</summary>
    public DiagnosticEcuState StateAt(string ecu, long timestampNanoseconds)
    {
        if (!_byEcu.TryGetValue(ecu, out var list)) return new DiagnosticEcuState(null, null, false);
        int? session = null;
        int? security = null;
        var known = false;
        foreach (var (time, change) in list)
        {
            if (time > timestampNanoseconds) break;
            known = true;
            switch (change.Kind)
            {
                case DiagnosticStateChangeKind.SessionChanged: session = change.Value; break;
                case DiagnosticStateChangeKind.SecurityUnlocked: security = change.Value; break;
                case DiagnosticStateChangeKind.SecurityLockedByReset:
                case DiagnosticStateChangeKind.SecurityLockedBySession: security = 0; break;
                case DiagnosticStateChangeKind.EcuReset: session = 0x01; break;
            }
        }
        // Security 0 = observed locked. A session never observed stays unknown.
        return new DiagnosticEcuState(session, security, known);
    }
}
