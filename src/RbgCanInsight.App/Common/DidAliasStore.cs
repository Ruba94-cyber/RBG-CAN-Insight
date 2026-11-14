using System.IO;
using System.Text.Json;
using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;

namespace RbgCanInsight.App.Common;

/// <summary>Learned mapping tied to ECU+DID and to the exact source DBC signal.
/// Optional fields preserve the legacy did-aliases.json file shape.</summary>
public sealed record DidAlias(
    string DidName, string Message, string Signal,
    int? Did = null, string Ecu = "", string Database = "",
    uint? MessageIdentifier = null, bool? IsExtended = null);

public static class DidAliasStore
{
    private static string AliasPath => Path.Combine(AppStorage.ConfigDirectory, "did-aliases.json");
    private static readonly Lazy<Dictionary<string, DidAlias>> Cache = new(Load);

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
    private static string Key(DidAlias alias) =>
        alias.Did is { } id
            ? $"{Normalize(alias.Ecu)}|{id:X4}|{Normalize(alias.DidName)}"
            : Normalize(alias.DidName);

    public static DidAlias? Find(DidParameter did)
    {
        var key = Key(new DidAlias(did.Name, "", "", did.Did, did.Ecu));
        if (Cache.Value.TryGetValue(key, out var exact)) return exact;
        // Legacy entries have no ECU or DID identity. Return as tentative,
        // never as verified evidence.
        return Cache.Value.GetValueOrDefault(Normalize(did.Name));
    }

    public static void Save(DidParameter did, string database, DbcMessage message, DbcSignal signal)
    {
        var alias = new DidAlias(
            did.Name, message.Name, signal.Name, did.Did, did.Ecu,
            database, message.Identifier, message.IsExtended);
        Cache.Value[Key(alias)] = alias;
        Persist();
    }

    public static void Remove(DidParameter did)
    {
        var changed = Cache.Value.Remove(Key(new DidAlias(did.Name, "", "", did.Did, did.Ecu)));
        // The user may be removing a pre-migration, name-only alias.
        changed |= Cache.Value.Remove(Normalize(did.Name));
        if (changed) Persist();
    }

    private static Dictionary<string, DidAlias> Load()
    {
        try
        {
            if (!File.Exists(AliasPath)) return new Dictionary<string, DidAlias>();
            var list = JsonSerializer.Deserialize<DidAlias[]>(File.ReadAllText(AliasPath)) ?? [];
            var map = new Dictionary<string, DidAlias>();
            foreach (var alias in list)
            {
                if (string.IsNullOrWhiteSpace(alias.DidName)) continue;
                map[Key(alias)] = alias;
            }
            return map;
        }
        catch
        {
            return new Dictionary<string, DidAlias>();
        }
    }

    private static void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AliasPath)!);
            File.WriteAllText(AliasPath,
                JsonSerializer.Serialize(Cache.Value.Values.ToArray(),
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Profile may be read-only; do not interrupt offline analysis.
        }
    }
}
