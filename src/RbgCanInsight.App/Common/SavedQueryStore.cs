using System.IO;
using System.Text.Json;

namespace RbgCanInsight.App.Common;

/// <summary><see cref="Colour"/> (optional, #AARRGGBB) paints the Trace rows this query matches.</summary>
public sealed record SavedQuery(string Name, string Query, string? Colour = null);

/// <summary>Named Trace filters the analyst reuses ("Hide keep-alive noise", "Only ch2 diagnostics").
/// Stored as plain JSON next to the other settings; a damaged or unreadable file simply yields an
/// empty list and never blocks the app.</summary>
public sealed class SavedQueryStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(AppStorage.ConfigDirectory, "saved-queries.json");

    public IReadOnlyList<SavedQuery> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            return (JsonSerializer.Deserialize<List<SavedQuery>>(File.ReadAllText(_path)) ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item?.Name))
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Adds a query, or replaces the one with the same name (case-insensitive).</summary>
    public IReadOnlyList<SavedQuery> Upsert(string name, string query, string? colour = null)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A saved query needs a name.", nameof(name));
        var items = Load().Where(item => !item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        items.Add(new SavedQuery(name, query, colour));
        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Write(items);
        return items;
    }

    public IReadOnlyList<SavedQuery> Remove(string name)
    {
        var items = Load().Where(item => !item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        Write(items);
        return items;
    }

    private void Write(List<SavedQuery> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, overwrite: true);
    }
}
