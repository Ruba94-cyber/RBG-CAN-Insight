using System.Xml.Linq;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Static keyboard-accessibility guard: every Button in the app XAML must stay reachable by Tab,
/// and the shared button template must draw a visible keyboard focus ring.</summary>
public sealed class KeyboardAccessibilityXamlTests
{
    [Fact]
    public void NoButtonIsRemovedFromTabOrder()
    {
        var appDir = Path.Combine(FindRepositoryRoot(), "src", "RbgCanInsight.App");
        foreach (var path in Directory.EnumerateFiles(appDir, "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains("Themes")))
        {
            var doc = XDocument.Load(path);
            foreach (var button in doc.Descendants().Where(e => e.Name.LocalName is "Button" or "ToggleButton"))
            {
                Assert.NotEqual("False", (string?)button.Attribute("Focusable"));
                Assert.NotEqual("False", (string?)button.Attribute("IsTabStop"));
            }
        }
    }

    [Fact]
    public void ButtonTemplateShowsKeyboardFocusRing()
    {
        var styles = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "RbgCanInsight.App", "Themes", "Styles.xaml"));
        Assert.Contains("x:Name=\"FocusRing\"", styles);
        Assert.Contains("Property=\"IsKeyboardFocused\"", styles);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RbgCanInsight.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
