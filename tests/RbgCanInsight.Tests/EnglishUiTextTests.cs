using Xunit;

namespace RbgCanInsight.Tests;

public sealed class EnglishUiTextTests
{
    private static readonly string[] ItalianUiFragments =
    [
        "Cerca ID",
        "messaggi",
        "segnali decodificati",
        "in tutte le viste",
        "per navigare",
        "Invio per aprire",
        "per chiudere"
    ];

    [Fact]
    public void AppUiDoesNotContainKnownItalianText()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appDirectory = Path.Combine(repositoryRoot, "src", "RbgCanInsight.App");
        var uiFiles = Directory.EnumerateFiles(appDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

        foreach (var path in uiFiles)
        {
            var content = File.ReadAllText(path);
            foreach (var fragment in ItalianUiFragments)
            {
                Assert.DoesNotContain(fragment, content, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RbgCanInsight.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
