using System.Text;

namespace RbgCanInsight.Dbc;

public sealed record DbcNodeMessageRef(string Message, uint Identifier, bool IsExtended, int SignalCount);

/// <summary>One BU_ node with the messages it transmits (BO_ transmitter or BO_TX_BU_) and receives (SG_ receivers).</summary>
public sealed record DbcNodeSummary(
    string Name, string Comment, bool Declared,
    IReadOnlyList<DbcNodeMessageRef> Transmits, IReadOnlyList<DbcNodeMessageRef> Receives);

public sealed record DbcMatrixRow(
    DbcMessage Message, IReadOnlyList<string> Transmitters, IReadOnlyDictionary<string, int> ReceivedSignals);

/// <summary>
/// Per-node Tx/Rx view and network-matrix export derived from the parsed database plus <see cref="DbcNetworkDefinition"/>.
/// Definition-only: it states what the DBC declares, not which ECU really sent a frame.
/// </summary>
public sealed class DbcNodeView
{
    public const string UnknownNode = "Vector__XXX";

    public IReadOnlyList<DbcNodeSummary> Nodes { get; }
    public IReadOnlyList<DbcMatrixRow> Rows { get; }
    public IReadOnlyList<string> NodeNames { get; }

    /// <summary>Receiver node names of a message (all signals), without the Vector__XXX placeholder.</summary>
    public static IReadOnlyList<string> DeclaredReceivers(DbcMessage message) =>
        message.Signals.SelectMany(s => SplitReceivers(s.Receiver)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();

    public static IEnumerable<string> SplitReceivers(string? receiver) =>
        (receiver ?? "").Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(n => !n.Equals(UnknownNode, StringComparison.OrdinalIgnoreCase));

    public DbcNodeView(DbcDatabase database, DbcNetworkDefinition network)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(network);
        var extra = network.ExtraTransmitters.GroupBy(e => e.RawIdentifier)
            .ToDictionary(g => g.Key, g => g.SelectMany(x => x.Nodes).ToList());

        var rows = new List<DbcMatrixRow>();
        foreach (var m in database.AllMessageDefinitions.OrderBy(m => m.IsExtended).ThenBy(m => m.Identifier))
        {
            var raw = m.IsExtended ? m.Identifier | 0x8000_0000u : m.Identifier;
            var tx = new List<string>();
            if (!string.IsNullOrWhiteSpace(m.Transmitter) && !m.Transmitter.Equals(UnknownNode, StringComparison.OrdinalIgnoreCase)) tx.Add(m.Transmitter);
            if (extra.TryGetValue(raw, out var more)) tx.AddRange(more.Where(n => !tx.Contains(n, StringComparer.Ordinal)));
            var rx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in m.Signals)
                foreach (var n in SplitReceivers(s.Receiver)) rx[n] = rx.GetValueOrDefault(n) + 1;
            rows.Add(new DbcMatrixRow(m, tx, rx));
        }
        Rows = rows;

        var declared = network.Nodes.ToDictionary(n => n.Name, n => n, StringComparer.OrdinalIgnoreCase);
        var names = declared.Keys.ToList();
        foreach (var n in rows.SelectMany(r => r.Transmitters.Concat(r.ReceivedSignals.Keys)))
            if (!declared.ContainsKey(n) && !names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
        NodeNames = names;

        Nodes = names.Select(n =>
        {
            DbcNodeMessageRef R(DbcMatrixRow r, int signals) => new(r.Message.Name, r.Message.Identifier, r.Message.IsExtended, signals);
            return new DbcNodeSummary(n, declared.TryGetValue(n, out var d) ? d.Comment : "", declared.ContainsKey(n),
                rows.Where(r => r.Transmitters.Contains(n, StringComparer.OrdinalIgnoreCase)).Select(r => R(r, r.Message.Signals.Count)).ToList(),
                rows.Where(r => r.ReceivedSignals.ContainsKey(n)).Select(r => R(r, r.ReceivedSignals[n])).ToList());
        }).ToList();
    }

    /// <summary>Network matrix: one row per message, one column per node; cells are Tx, Rx(n signals) or Tx+Rx(n).</summary>
    public string ToMatrixCsv()
    {
        var sb = new StringBuilder("﻿");
        sb.Append("Identifier,Format,Message,DLC,Transmitters");
        foreach (var n in NodeNames) sb.Append(',').Append(Csv(n));
        sb.Append("\r\n");
        foreach (var r in Rows)
        {
            sb.Append(r.Message.IsExtended ? $"0x{r.Message.Identifier:X8}" : $"0x{r.Message.Identifier:X3}").Append(',')
              .Append(r.Message.IsExtended ? "29-bit" : "11-bit").Append(',').Append(Csv(r.Message.Name)).Append(',')
              .Append(r.Message.Length).Append(',').Append(Csv(string.Join("; ", r.Transmitters)));
            foreach (var n in NodeNames)
            {
                var tx = r.Transmitters.Contains(n, StringComparer.OrdinalIgnoreCase);
                var rx = r.ReceivedSignals.TryGetValue(n, out var c) ? c : 0;
                sb.Append(',').Append(tx && rx > 0 ? $"Tx+Rx({rx})" : tx ? "Tx" : rx > 0 ? $"Rx({rx})" : "");
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Csv(string v) => v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
}
