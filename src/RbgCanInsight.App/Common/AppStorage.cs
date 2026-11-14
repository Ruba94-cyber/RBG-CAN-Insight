using System.IO;

namespace RbgCanInsight.App.Common;

/// <summary>
/// Resolves the writable data folders of the app. Portable-first: `config/` and `logs/` next to
/// the executable when that location is writable, otherwise the per-user local app data folder
/// (e.g. when the app is installed under Program Files).
/// </summary>
public static class AppStorage
{
    private static readonly Lazy<string> Root = new(ResolveRoot);

    public static string ConfigDirectory => EnsureDirectory(Path.Combine(Root.Value, "config"));
    public static string LogsDirectory => EnsureDirectory(Path.Combine(Root.Value, "logs"));

    private static string ResolveRoot()
    {
        // Only a test process that explicitly sets this environment variable
        // uses a disposable profile; normal portable and installed app paths
        // remain unchanged. FlaUI runs against a real process without touching
        // the developer's settings, history or saved window positions.
        var isolatedTestRoot = Environment.GetEnvironmentVariable("RBG_CAN_INSIGHT_GUI_TEST_ROOT");
        if (!string.IsNullOrWhiteSpace(isolatedTestRoot))
            return Path.GetFullPath(isolatedTestRoot);

        var portable = AppContext.BaseDirectory;
        if (IsWritable(portable))
        {
            return portable;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RbgCanInsight");
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string EnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // Best effort: callers already treat persistence as optional.
        }

        return path;
    }
}
