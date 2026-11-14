using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.ViewModels;
using RbgCanInsight.Correlation;

namespace RbgCanInsight.App.Common;

/// <summary>
/// Displays the sample-and-hold physical value at the offline plot cursor. The
/// binding also listens to plot time changes during scrub/playback; it is evaluated
/// only for realized (virtualized) signal rows, not for every DBC signal on every tick.
/// </summary>
public sealed class SignalAtPlotCursorConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not PlotSeriesViewModel series)
            return "—";

        var timestamp = values[1] is long time ? time : -1;
        if (timestamp < 0 && series.Samples.Count > 0)
            timestamp = series.Samples[^1].TimestampNanoseconds;

        var sample = series.Timeline.SampleAt(timestamp);
        if (sample is not { } observed)
            return "—";

        var (physical, _, state) = series.ValueAt(timestamp);
        // Source age has its own column. Do not duplicate a long provenance
        // suffix inside this value cell and make the physical value unreadable.
        return state is "—" or "" ? physical : $"{physical} · {state}";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Source time and provenance of the held last recorded sample.
/// The visible cell shows age; the tooltip can request the exact frame and
/// timestamp via ConverterParameter="provenance".</summary>
public sealed class SignalSourceAgeAtPlotCursorConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not PlotSeriesViewModel series)
            return "—";

        var timestamp = values[1] is long time ? time : -1;
        if (timestamp < 0 && series.Samples.Count > 0)
            timestamp = series.Samples[^1].TimestampNanoseconds;

        var (age, provenance) = series.EvidenceAt(timestamp);
        return parameter is "provenance" ? provenance : age;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the bound integer equals the converter parameter.</summary>
public sealed class IntEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int actual && int.TryParse(parameter as string, out var expected) && actual == expected
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Small expand/collapse indicator glyph for a row whose inline detail is toggled by
/// clicking anywhere on the row, not a dedicated button.</summary>
public sealed class ExpandGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "▾" : "▸";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Wraps a single <see cref="PlotSeriesViewModel"/> in a one-item array so it can feed
/// a <c>SignalPlotControl.ItemsSource</c>, which expects a collection.</summary>
public sealed class SingleSeriesToArrayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PlotSeriesViewModel series ? new[] { series } : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the bound value is null — switches the busy indicator between
/// indeterminate (no percent reported yet) and determinate once a phase starts reporting progress.</summary>
public sealed class IsNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Non-boolean values follow null-ness so the converter doubles as a null-to-visibility.
        var flag = value is bool boolean ? boolean : value is not null;
        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Collapses a Grid row to zero height when the bound value is "empty" (null, false or a
/// non-positive count); otherwise resolves to the pixel height given as the converter
/// parameter. Unlike toggling a child's Visibility alone, this actually returns the row's
/// reserved space to the rest of the layout when the panel has nothing to show.
/// </summary>
/// <remarks>
/// <see cref="RememberedHeight"/> lets a specific XAML usage (via its own named resource
/// instance, not the shared default) remember the last height the user actually dragged the
/// panel to, instead of always reopening at the fixed <c>ConverterParameter</c> default. Only
/// give a usage site its own instance if reopening at a remembered size is wanted there —
/// sites that share the default instance keep the plain fixed-height behavior.
/// </remarks>
public sealed class CollapsibleRowHeightConverter : IValueConverter
{
    public double? RememberedHeight { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isOpen = value switch
        {
            null => false,
            bool boolean => boolean,
            int count => count > 0,
            _ => true
        };

        var defaultHeight = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
            ? height
            : 200;
        var openHeight = RememberedHeight ?? defaultHeight;
        return isOpen ? new GridLength(openHeight) : new GridLength(0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Activity → accent brush for the change matrix.</summary>
public sealed class ActivityToBrushConverter : IValueConverter
{
    private static readonly Brush Variable = Frozen(66, 196, 139);
    private static readonly Brush Stable = Frozen(147, 161, 181);
    private static readonly Brush Anomalous = Frozen(255, 104, 104);
    private static readonly Brush Missing = Frozen(245, 200, 66);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            SignalActivity.Variable => Variable,
            SignalActivity.Anomalous => Anomalous,
            SignalActivity.Missing => Missing,
            _ => Stable
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Confidence [0..1] → width for the confidence bar (parameter = max width).</summary>
public sealed class ConfidenceToWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var confidence = value is double d ? Math.Clamp(d, 0, 1) : 0;
        var max = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 60;
        return confidence * max;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Candidate combo item → display label (handles the "no mapping" sentinel).</summary>
public sealed class CandidateLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is CorrelationCandidate candidate
            ? $"{candidate.Message.Transmitter}.{candidate.Message.Name}.{candidate.Signal.Name}  ({candidate.Confidence:P0})"
            : "— no mapping —";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Mapping status text → brush for the status pill.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly Brush Confirmed = Frozen(66, 196, 139);
    private static readonly Brush Suggested = Frozen(46, 168, 255);
    private static readonly Brush ToVerify = Frozen(245, 200, 66);
    private static readonly Brush Rejected = Frozen(255, 104, 104);
    private static readonly Brush Unmatched = Frozen(147, 161, 181);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            DiagData.MappingStatus.Confirmed => Confirmed,
            DiagData.MappingStatus.Suggested => Suggested,
            DiagData.MappingStatus.ToVerify => ToVerify,
            DiagData.MappingStatus.Rejected => Rejected,
            _ => Unmatched
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
