using System.Text.Json;

namespace RbgCanInsight.Analysis;

/// <summary>What a pane of the Investigation workspace shows.</summary>
public enum InvestigationPane
{
    Empty,
    Trace,
    DecodedSignals,
    UdsTransactions,
    Findings,
    // Append only: old numeric/serialized layout preferences retain their meaning.
    ObservedSignals,
    SignalPlot
}

public enum InvestigationArrangement
{
    Single,
    SideBySide,
    Stacked,
    Quad
}

/// <summary>A named arrangement of synchronized panes. Always exactly four pane slots are stored;
/// only the first <see cref="InvestigationLayouts.VisiblePaneCount"/> are shown.</summary>
public sealed record InvestigationLayout(
    string Name,
    InvestigationArrangement Arrangement,
    IReadOnlyList<InvestigationPane> Panes);

public static class InvestigationLayouts
{
    public const int SlotCount = 4;

    public static int VisiblePaneCount(InvestigationArrangement arrangement) => arrangement switch
    {
        InvestigationArrangement.Single => 1,
        InvestigationArrangement.SideBySide or InvestigationArrangement.Stacked => 2,
        _ => 4
    };

    /// <summary>Pads or trims the pane list to the four slots, replacing unknown values with Empty.</summary>
    public static InvestigationLayout Normalize(InvestigationLayout layout)
    {
        var panes = new InvestigationPane[SlotCount];
        for (var i = 0; i < SlotCount; i++)
        {
            var value = i < layout.Panes.Count ? layout.Panes[i] : InvestigationPane.Empty;
            panes[i] = Enum.IsDefined(value) ? value : InvestigationPane.Empty;
        }

        var arrangement = Enum.IsDefined(layout.Arrangement) ? layout.Arrangement : InvestigationArrangement.SideBySide;
        return new InvestigationLayout(layout.Name.Trim(), arrangement, panes);
    }

    /// <summary>Layouts that ship with the app. They cannot be overwritten or deleted.</summary>
    public static IReadOnlyList<InvestigationLayout> BuiltIn { get; } =
    [
        new("Trace + decoded signals", InvestigationArrangement.SideBySide,
            [InvestigationPane.Trace, InvestigationPane.DecodedSignals, InvestigationPane.Empty, InvestigationPane.Empty]),
        new("Trace + UDS transactions", InvestigationArrangement.Stacked,
            [InvestigationPane.Trace, InvestigationPane.UdsTransactions, InvestigationPane.Empty, InvestigationPane.Empty]),
        new("Full investigation", InvestigationArrangement.Quad,
            [InvestigationPane.Trace, InvestigationPane.DecodedSignals, InvestigationPane.UdsTransactions,
             InvestigationPane.Findings]),
        // Append new built-ins to preserve the indices of earlier shipped layouts.
        new("Signals + trend", InvestigationArrangement.SideBySide,
            [InvestigationPane.ObservedSignals, InvestigationPane.SignalPlot, InvestigationPane.Empty, InvestigationPane.Empty])
    ];

    public static bool IsBuiltIn(string name) =>
        BuiltIn.Any(layout => layout.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>User-saved layouts as a small JSON file. An unreadable file yields no layouts.</summary>
public sealed class InvestigationLayoutStore(string path)
{
    private sealed record StoredLayout(string Name, string Arrangement, string[] Panes);

    public IReadOnlyList<InvestigationLayout> Load()
    {
        try
        {
            if (!File.Exists(path)) return [];
            var stored = JsonSerializer.Deserialize<List<StoredLayout>>(File.ReadAllText(path)) ?? [];
            var result = new List<InvestigationLayout>();
            foreach (var item in stored)
            {
                if (string.IsNullOrWhiteSpace(item?.Name) || InvestigationLayouts.IsBuiltIn(item.Name)) continue;
                result.Add(InvestigationLayouts.Normalize(new InvestigationLayout(
                    item.Name,
                    Enum.TryParse<InvestigationArrangement>(item.Arrangement, true, out var arrangement)
                        ? arrangement : InvestigationArrangement.SideBySide,
                    (item.Panes ?? []).Select(pane =>
                        Enum.TryParse<InvestigationPane>(pane, true, out var kind) ? kind : InvestigationPane.Empty)
                        .ToArray())));
            }

            return result;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Saves or replaces a layout by name. Built-in names are reserved.</summary>
    public IReadOnlyList<InvestigationLayout> Save(InvestigationLayout layout)
    {
        var normalized = InvestigationLayouts.Normalize(layout);
        if (normalized.Name.Length == 0) throw new ArgumentException("A layout needs a name.");
        if (InvestigationLayouts.IsBuiltIn(normalized.Name))
            throw new InvalidOperationException($"\"{normalized.Name}\" is a built-in layout; choose another name.");
        var items = Load().Where(item => !item.Name.Equals(normalized.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        items.Add(normalized);
        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Write(items);
        return items;
    }

    public IReadOnlyList<InvestigationLayout> Delete(string name)
    {
        var items = Load().Where(item => !item.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        Write(items);
        return items;
    }

    private void Write(List<InvestigationLayout> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stored = items.Select(item => new StoredLayout(
            item.Name, item.Arrangement.ToString(), item.Panes.Select(pane => pane.ToString()).ToArray()));
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}
