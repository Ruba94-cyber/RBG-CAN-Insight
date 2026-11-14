using System.Text.Json;

namespace RbgCanInsight.Analysis;

/// <summary>Named, local-only trigger rule for the exact database/channel/CAN
/// identity. The preset does not store the vehicle trace or an OEM payload.</summary>
public sealed record SignalTriggerPreset(string Name, SignalTriggerRule Rule);

/// <summary>
/// Bounded, versioned JSON persistence for offline signal trigger rules.
/// Writes are atomic; corrupt or future-version configuration is never
/// silently overwritten. Supplied path must belong to the app's config area.
/// </summary>
public sealed class SignalTriggerPresetStore
{
    public const int MaxPresets = 256;
    private const long MaxFileBytes = 1_048_576;
    private sealed record PresetDocument(int SchemaVersion, List<SignalTriggerPreset> Presets);
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public SignalTriggerPresetStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public IReadOnlyList<SignalTriggerPreset> ReadAll()
    {
        if (!File.Exists(_path)) return [];
        if (new FileInfo(_path).Length > MaxFileBytes)
            throw new InvalidDataException("Saved trigger rules exceed the 1 MiB configuration safety limit.");
        var data = JsonSerializer.Deserialize<PresetDocument>(File.ReadAllText(_path), JsonOptions)
            ?? throw new InvalidDataException("Saved signal trigger rules are empty or malformed.");
        if (data.SchemaVersion != 1)
            throw new NotSupportedException(
                $"Signal trigger preset format version {data.SchemaVersion} is not supported.");
        if (data.Presets is null || data.Presets.Count > MaxPresets)
            throw new InvalidDataException("Saved signal trigger rules exceed the supported entry limit.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in data.Presets)
        {
            if (preset is null)
                throw new InvalidDataException("Saved signal trigger contains an invalid null entry.");
            Validate(preset.Name, preset.Rule);
            if (!names.Add(preset.Name))
                throw new InvalidDataException("Saved signal trigger rules contain duplicate names.");
        }
        return data.Presets.OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Replace an entry with the same name or create a new entry.
    /// Returns the fully validated, persisted preset list.</summary>
    public IReadOnlyList<SignalTriggerPreset> Save(string name, SignalTriggerRule rule)
    {
        name = name?.Trim() ?? "";
        Validate(name, rule);
        var items = ReadAll().ToList(); // must fail if prior config is corrupt
        var existing = items.FindIndex(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            items[existing] = new SignalTriggerPreset(name, rule);
        else
        {
            if (items.Count >= MaxPresets)
                throw new InvalidOperationException("The saved offline trigger rule limit has been reached.");
            items.Add(new SignalTriggerPreset(name, rule));
        }
        Write(items);
        return ReadAll();
    }

    public bool Remove(string name)
    {
        var items = ReadAll().ToList();
        var index = items.FindIndex(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        items.RemoveAt(index);
        Write(items);
        return true;
    }

    private static void Validate(string name, SignalTriggerRule rule)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 ||
            name.Any(char.IsControl))
            throw new ArgumentException("Preset name must contain 1–80 printable characters.");
        if (rule is null || string.IsNullOrWhiteSpace(rule.SignalKey) ||
            rule.SignalKey.Length > 2048 || rule.SignalKey.Any(char.IsControl))
            throw new ArgumentException("A saved trigger requires a valid, exact physical signal key.");
        if (!Enum.IsDefined(rule.Mode) ||
            !double.IsFinite(rule.Threshold) ||
            !double.IsFinite(rule.ChangeTolerance) || rule.ChangeTolerance < 0 ||
            rule.MinimumEventSeparationNanoseconds < 0)
            throw new ArgumentException("Saved trigger numeric fields or mode are invalid.");
        if (rule.NamedState is { Length: > 512 } ||
            rule.NamedState?.Any(char.IsControl) == true)
            throw new ArgumentException("Saved named state is invalid.");
        if ((rule.Mode is SignalTriggerMode.EntersNamedState or
             SignalTriggerMode.LeavesNamedState) &&
            string.IsNullOrWhiteSpace(rule.NamedState))
            throw new ArgumentException("Saved named-state trigger needs a state label.");
    }

    private void Write(IReadOnlyList<SignalTriggerPreset> presets)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var text = JsonSerializer.Serialize(
            new PresetDocument(1, presets.OrderBy(item => item.Name,
                StringComparer.OrdinalIgnoreCase).ToList()), JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxFileBytes)
            throw new InvalidDataException("Saved trigger rules exceed the maximum config size.");

        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
