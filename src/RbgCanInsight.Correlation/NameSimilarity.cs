using System.Text.RegularExpressions;

namespace RbgCanInsight.Correlation;

/// <summary>
/// Text similarity tuned for automotive identifier names: names are split into tokens
/// (underscores, camel case, digits), common automotive abbreviations are expanded to a
/// canonical token, and the score blends token overlap with a normalized edit distance.
/// </summary>
public static partial class NameSimilarity
{
    // Canonicalisation map for the abbreviations that appear constantly in DBC/ODX names.
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spd"] = "speed",
        ["vel"] = "speed",
        ["velocity"] = "speed",
        ["tmp"] = "temperature",
        ["temp"] = "temperature",
        ["t"] = "temperature",
        ["press"] = "pressure",
        ["prs"] = "pressure",
        ["pres"] = "pressure",
        ["p"] = "pressure",
        ["eng"] = "engine",
        ["mot"] = "engine",
        ["motor"] = "engine",
        ["veh"] = "vehicle",
        ["bat"] = "battery",
        ["batt"] = "battery",
        ["u"] = "voltage",
        ["volt"] = "voltage",
        ["volts"] = "voltage",
        ["i"] = "current",
        ["curr"] = "current",
        ["amp"] = "current",
        ["lvl"] = "level",
        ["lev"] = "level",
        ["pos"] = "position",
        ["posn"] = "position",
        ["pct"] = "percent",
        ["perc"] = "percent",
        ["n"] = "speed",
        ["rpm"] = "speed",
        ["revs"] = "speed",
        ["trq"] = "torque",
        ["tq"] = "torque",
        ["accel"] = "acceleration",
        ["acc"] = "acceleration",
        ["stat"] = "status",
        ["sts"] = "status",
        ["st"] = "status",
        ["req"] = "request",
        ["cmd"] = "command",
        ["ctrl"] = "control",
        ["fl"] = "frontleft",
        ["fr"] = "frontright",
        ["rl"] = "rearleft",
        ["rr"] = "rearright",
        ["whl"] = "wheel",
        ["fuel"] = "fuel",
        ["cool"] = "coolant",
        ["clnt"] = "coolant",
        ["amb"] = "ambient",
        ["ext"] = "ambient",
        ["odo"] = "odometer",
        ["mileage"] = "odometer",
        ["dist"] = "distance",
        ["hr"] = "hours",
        ["hrs"] = "hours",
        ["swa"] = "steeringangle",
        ["str"] = "steering",
        ["brk"] = "brake",
        ["gr"] = "gear",
        ["ig"] = "ignition",
        ["ign"] = "ignition"
    };

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "of", "and", "value", "signal", "data", "read", "actual", "info"
    };

    public static IReadOnlyList<string> Tokenize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        var expanded = CamelBoundary().Replace(name, "$1 $2");
        var rawTokens = SplitPattern().Split(expanded)
            .Where(token => token.Length > 0)
            .Select(token => token.ToLowerInvariant())
            .Where(token => !Stopwords.Contains(token));

        var tokens = new List<string>();
        foreach (var token in rawTokens)
        {
            tokens.Add(Synonyms.GetValueOrDefault(token, token));
        }

        return tokens;
    }

    /// <summary>Similarity in [0,1]: blend of canonical-token overlap and edit-distance ratio.</summary>
    public static double Compute(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0;
        }

        var leftTokens = Tokenize(left);
        var rightTokens = Tokenize(right);
        var tokenScore = TokenOverlap(leftTokens, rightTokens);

        var normalizedLeft = string.Concat(leftTokens);
        var normalizedRight = string.Concat(rightTokens);
        var editScore = normalizedLeft.Length == 0 || normalizedRight.Length == 0
            ? 0
            : 1.0 - (double)Levenshtein(normalizedLeft, normalizedRight) /
              Math.Max(normalizedLeft.Length, normalizedRight.Length);

        return Math.Max(tokenScore, 0.5 * tokenScore + 0.5 * editScore);
    }

    private static double TokenOverlap(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        var leftSet = new HashSet<string>(left);
        var rightSet = new HashSet<string>(right);
        double intersection = leftSet.Count(rightSet.Contains);

        // Partial credit for tokens contained in one another ("temp" in "temperature2").
        foreach (var token in leftSet)
        {
            if (!rightSet.Contains(token) &&
                rightSet.Any(other => other.Contains(token) || token.Contains(other)))
            {
                intersection += 0.5;
            }
        }

        return intersection / Math.Max(leftSet.Count, rightSet.Count);
    }

    private static int Levenshtein(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    [GeneratedRegex("([a-z\\d])([A-Z])")]
    private static partial Regex CamelBoundary();

    [GeneratedRegex("[^A-Za-z\\d]+|(?<=[A-Za-z])(?=\\d)|(?<=\\d)(?=[A-Za-z])")]
    private static partial Regex SplitPattern();
}
