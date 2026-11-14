using System.IO;
using System.Text.Json;

namespace RbgCanInsight.App.Common;

/// <summary>One workspace preset the analyst has saved or loaded before, remembered by name so it
/// can be reopened from a short list instead of hunting for the file again.</summary>
public sealed record PresetLibraryEntry(string Name, string Path, DateTimeOffset SavedAt);

/// <summary>
/// Small cross-session library of named workspace presets. This does not replace the
/// .rbgpreset file itself (still a plain JSON file the user can move/share) — it is a
/// convenience index so "Load preset" can show a short, named list of recent presets instead of
/// always starting from a blank file-open dialog.
/// </summary>
public static class PresetLibraryStore
{
    private const int MaxEntries = 20;

    private static string LibraryPath => Path.Combine(AppStorage.ConfigDirectory, "preset-library.json");

    public static IReadOnlyList<PresetLibraryEntry> Load()
    {
        try
        {
            if (!File.Exists(LibraryPath))
            {
                return [];
            }

            var entries = JsonSerializer.Deserialize<PresetLibraryEntry[]>(File.ReadAllText(LibraryPath)) ?? [];
            return entries
                .Where(entry => File.Exists(entry.Path))
                .OrderByDescending(entry => entry.SavedAt)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Adds or updates the entry for this path (matched case-insensitively) and moves it
    /// to the front of the list, trimming to <see cref="MaxEntries"/>.</summary>
    public static void Remember(string name, string path)
    {
        var entries = Load()
            .Where(entry => !string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
            .ToList();
        entries.Insert(0, new PresetLibraryEntry(name, path, DateTimeOffset.Now));
        Save(entries.Take(MaxEntries).ToArray());
    }

    public static void Rename(string path, string newName)
    {
        var entries = Load().ToArray();
        for (var index = 0; index < entries.Length; index++)
        {
            if (string.Equals(entries[index].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                entries[index] = entries[index] with { Name = newName };
            }
        }

        Save(entries);
    }

    public static void Remove(string path)
    {
        var entries = Load().Where(entry => !string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray();
        Save(entries);
    }

    private static void Save(IReadOnlyList<PresetLibraryEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LibraryPath)!);
            File.WriteAllText(LibraryPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort: the library is a convenience index, not the source of truth.
        }
    }
}
