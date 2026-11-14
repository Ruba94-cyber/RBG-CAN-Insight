using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Reads the raw palette XAML (not a WPF ResourceDictionary — the test project has no WPF
/// dependency) and checks every theme against WCAG 2.1's 4.5:1 minimum contrast for normal text,
/// on every surface that theme actually draws it on. A prior audit found the Light theme's
/// Warning/Error/Teal/Accent colors read as low as ~2.5:1 against a raised panel because only the
/// Dark theme was ever eyeballed during development; this guards against that regressing silently
/// for any theme, current or future.</summary>
public sealed class ThemeContrastTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] SurfaceKeys = ["BackgroundColor", "SurfaceColor", "SurfaceRaisedColor"];

    // Colors actually used as text/icon/pill foreground somewhere in the app. Plot series colors
    // are deliberately excluded: they are documented as colorblind-optimized fills that are always
    // paired with a persistent legend label, not standalone text.
    private static readonly string[] TextLikeKeys =
        ["TextPrimaryColor", "TextSecondaryColor", "WarningColor", "ErrorColor", "TealColor", "AccentColor"];

    [Theory]
    [InlineData("Palette.Dark.xaml")]
    [InlineData("Palette.Light.xaml")]
    [InlineData("Palette.HighContrast.xaml")]
    public void EveryTextLikeColorMeetsMinimumContrastOnEverySurfaceItIsDrawnOn(string fileName)
    {
        var colors = LoadColors(fileName);

        foreach (var surfaceKey in SurfaceKeys)
        {
            var surface = colors[surfaceKey];
            foreach (var textKey in TextLikeKeys)
            {
                var contrast = ContrastRatio(colors[textKey], surface);
                Assert.True(contrast >= 4.5,
                    $"{fileName}: {textKey} on {surfaceKey} has contrast {contrast:F2}:1, " +
                    "below the 4.5:1 WCAG AA minimum for normal text.");
            }
        }
    }

    [Fact]
    public void AllThreeThemesDefineTheSameColorKeys()
    {
        var dark = LoadColors("Palette.Dark.xaml").Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var light = LoadColors("Palette.Light.xaml").Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var highContrast = LoadColors("Palette.HighContrast.xaml").Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

        Assert.Equal(dark, light);
        Assert.Equal(dark, highContrast);
    }

    private static Dictionary<string, (byte R, byte G, byte B)> LoadColors(string fileName)
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "RbgCanInsight.App", "Themes", fileName);
        var document = XDocument.Load(path);
        var colors = new Dictionary<string, (byte, byte, byte)>();
        foreach (var element in document.Root!.Elements(Presentation + "Color"))
        {
            var key = element.Attribute(X + "Key")?.Value;
            var value = element.Value.Trim();
            if (key is null || !value.StartsWith('#'))
            {
                continue;
            }

            colors[key] = ParseHex(value);
        }

        return colors;
    }

    private static (byte R, byte G, byte B) ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        var r = byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var g = byte.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var b = byte.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (r, g, b);
    }

    private static double ContrastRatio((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
    {
        var luminanceA = RelativeLuminance(a);
        var luminanceB = RelativeLuminance(b);
        var lighter = Math.Max(luminanceA, luminanceB);
        var darker = Math.Min(luminanceA, luminanceB);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance((byte R, byte G, byte B) color) =>
        0.2126 * Linearize(color.R) + 0.7152 * Linearize(color.G) + 0.0722 * Linearize(color.B);

    private static double Linearize(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
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
