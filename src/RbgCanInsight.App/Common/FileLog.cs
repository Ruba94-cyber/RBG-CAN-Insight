using System.IO;

namespace RbgCanInsight.App.Common;

/// <summary>
/// Minimal file logger for diagnostics: one rolling text file per day under `logs/`.
/// Errors were previously visible only in message boxes, which made field issues untraceable.
/// </summary>
public static class FileLog
{
    private static readonly object Gate = new();

    public static void Error(string context, Exception exception) =>
        Write("ERROR", $"{context}: {exception}");

    public static void Info(string message) =>
        Write("INFO", message);

    private static void Write(string level, string message)
    {
        try
        {
            var path = Path.Combine(AppStorage.LogsDirectory, $"rbg-can-insight-{DateTime.Now:yyyyMMdd}.log");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
