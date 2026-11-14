using System.Globalization;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis;

/// <summary>A saved query paired with the row colour it paints in the Trace.</summary>
public sealed record RowColourRule(string Query, string Colour);

/// <summary>
/// Row colour rules for the Trace: each rule is an ordinary Trace query plus a colour, and the first rule
/// whose query matches a frame decides that row's colour (like Wireshark colouring rules). Evaluation is
/// lazy and per row, so it adds nothing to loading or filtering a large trace.
/// </summary>
public static class RowColourRules
{
    /// <summary>Translucent tints that stay legible on both the light and the dark theme (#AARRGGBB).</summary>
    public static IReadOnlyList<(string Name, string Hex)> Palette { get; } =
    [
        ("Red", "#55E53935"),
        ("Orange", "#55FB8C00"),
        ("Yellow", "#55FDD835"),
        ("Green", "#5543A047"),
        ("Teal", "#5500ACC1"),
        ("Blue", "#551E88E5"),
        ("Purple", "#558E24AA"),
        ("Grey", "#55757575")
    ];

    /// <summary>The name shown for "no colour" in pickers.</summary>
    public const string NoColourName = "None";

    public static string? HexForName(string? name) =>
        Palette.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Hex;

    public static string NameForHex(string? hex) =>
        Palette.FirstOrDefault(item => item.Hex.Equals(hex, StringComparison.OrdinalIgnoreCase)).Name ?? NoColourName;

    /// <summary>True for #RRGGBB or #AARRGGBB.</summary>
    public static bool IsValidColour(string? colour) =>
        colour is { Length: 7 or 9 } && colour[0] == '#' &&
        colour.AsSpan(1).ToString().All(Uri.IsHexDigit) &&
        uint.TryParse(colour.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);

    /// <summary>Compiles rules into one function that returns the colour of the first matching rule, or null.
    /// Rules with an empty query or an invalid colour are ignored.</summary>
    public static Func<CanFrame, string?> Compile(
        IEnumerable<RowColourRule> rules, long originNanoseconds, Func<string, Func<CanFrame, bool>> basicTerm)
    {
        var compiled = rules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Query) && IsValidColour(rule.Colour))
            .Select(rule => (Matches: FrameQuery.Compile(rule.Query, originNanoseconds, basicTerm), rule.Colour))
            .ToArray();
        if (compiled.Length == 0) return _ => null;
        return frame =>
        {
            foreach (var (matches, colour) in compiled)
                if (matches(frame)) return colour;
            return null;
        };
    }
}
