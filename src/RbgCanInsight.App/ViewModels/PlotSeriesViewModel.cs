using System.IO;
using System.Globalization;
using System.Windows.Media;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;
using RbgCanInsight.Dbc;

namespace RbgCanInsight.App.ViewModels;

/// <summary>One plottable signal series backed by its decoded timeline.</summary>
public sealed class PlotSeriesViewModel : ObservableObject
{
    private bool _isPlotted;
    private bool _isDetailOpen;
    private Brush _plotColor = Brushes.Transparent;
    private readonly string _observedRangeText;

    public PlotSeriesViewModel(SignalTimeline timeline)
    {
        Timeline = timeline;
        FullScaleValue = ComputeFullScaleValue(timeline.Signal);
        var samples = timeline.Samples;
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        var sum = 0d;
        var finiteCount = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            var value = samples[i].Value;
            if (double.IsFinite(value))
            {
                min = Math.Min(min, value);
                max = Math.Max(max, value);
                sum += value;
                finiteCount++;
            }
            if (i > 0 && value != samples[i - 1].Value)
                ChangeCount++;
        }
        _observedRangeText = finiteCount == 0
            ? "no finite samples"
            : $"{min:G6} … {max:G6}{UnitSuffix} · mean {sum / finiteCount:G6}{UnitSuffix}";
    }

    /// <summary>
    /// Physical value corresponding to an all-ones raw bit pattern, when a
    /// strictly linear integer DBC conversion permits computing it. This
    /// pattern MAY be a documented sentinel, but it can also be valid data;
    /// do not infer "not available" without an explicit DBC meaning.
    /// </summary>
    public double? FullScaleValue { get; }

    private static double? ComputeFullScaleValue(DbcSignal signal) =>
        DbcSignalRawMath.AllOnesPhysical(signal);

    /// <summary>True when the sample value sits exactly at the raw full-scale pattern.</summary>
    public bool IsFullScale(double value) =>
        FullScaleValue is { } fullScale && value == fullScale;

    public SignalTimeline Timeline { get; }
    public string SignalName => Timeline.Signal.Name;
    public string DatabaseName => Timeline.DatabaseName;
    public string ChannelText => Timeline.Channel < 0 ? "CAN ?" : $"CAN {Timeline.Channel + 1}";
    public string RecordingText => Timeline.OriginalSourceIndex < 0
        ? "Current recording"
        : $"Logger {Timeline.OriginalSourceIndex + 1}" +
            (string.IsNullOrWhiteSpace(Timeline.OriginalSourcePath)
                ? "" : $" · {Path.GetFileName(Timeline.OriginalSourcePath)}");
    public string RecordingPath => Timeline.OriginalSourcePath ??
        "Original source path unavailable (single recording).";
    public string MessageName => Timeline.Message.Name;
    public string Transmitter => string.IsNullOrWhiteSpace(Timeline.Message.Transmitter)
        ? "unknown ECU"
        : Timeline.Message.Transmitter;
    public string Unit => Timeline.Signal.Unit;
    /// <summary>Actually recorded CAN wire ID, including the J1939
    /// source address; different source ECUs cannot share one curve.</summary>
    public string IdentifierHex =>
        Timeline.SourceIdentifier.ToString(Timeline.SourceIsExtended ? "X8" : "X3");
    public string DefinitionIdentifierHex =>
        Timeline.Message.Identifier.ToString(Timeline.Message.IsExtended ? "X8" : "X3");
    public bool IsPgnMatchedSource =>
        Timeline.SourceIdentifier != Timeline.Message.Identifier ||
        Timeline.SourceIsExtended != Timeline.Message.IsExtended;
    public string SourceIdentifierDetailText => (IsPgnMatchedSource
        ? $"Recorded CAN 0x{IdentifierHex} · J1939 SA 0x{(Timeline.SourceIdentifier & 0xFFu):X2} · matched DBC declaration 0x{DefinitionIdentifierHex}; source ECU name not independently verified"
        : $"Recorded CAN 0x{IdentifierHex} · {(Timeline.SourceIsExtended ? "29-bit" : "11-bit")} · original source frame evidence") +
        $"\n{RecordingText} · {RecordingPath}";
    public int SampleCount => Timeline.Samples.Count;
    /// <summary>Changes actually observed in source trace frames, not DBC-defined possible states.</summary>
    public int ChangeCount { get; private set; }
    public bool HasObservedChanges => ChangeCount > 0;
    public string ActivityText => SampleCount switch
    {
        0 => "No samples",
        1 => "1 observation",
        _ when ChangeCount == 0 => "No observed changes",
        _ => $"{ChangeCount:N0} changes"
    };
    /// <summary>Recorded values only. Absence of a transition in a
    /// sampled trace is not proof that the ECU output was constant
    /// between recordings or while its multiplex branch was inactive.</summary>
    public string ActivityExplanation => SampleCount switch
    {
        0 => "No decodable source CAN frames for this signal.",
        1 => "One decoded CAN sample: no change can be inferred from a single observation.",
        _ when ChangeCount == 0 =>
            $"{SampleCount:N0} decoded CAN samples with no observed physical-value transition; behavior between frames is unknown.",
        _ =>
            $"{ChangeCount:N0} recorded physical-value transitions across {SampleCount:N0} decoded CAN samples."
    };
    public IReadOnlyList<SignalSample> Samples => Timeline.Samples;
    public string Comment => Timeline.Signal.Comment;
    public bool HasComment => !string.IsNullOrWhiteSpace(Comment);
    public string ToolTipText =>
        $"{DatabaseName} · {RecordingText} · {ChannelText} · {Transmitter} · {MessageName} (recorded 0x{IdentifierHex}) · {SampleCount:N0} samples" +
        (IsPgnMatchedSource ? $"\nDBC definition 0x{DefinitionIdentifierHex} · J1939 PGN match; recorded source kept distinct" : string.Empty) +
        $"\n{ActivityExplanation}" +
        (string.IsNullOrWhiteSpace(Timeline.Signal.Comment) ? string.Empty : $"\n{Timeline.Signal.Comment}");

    /// <summary>DBC-declared physical range, the engineering-limits context an analyst needs
    /// alongside whatever the trace actually observed (see <see cref="ObservedRangeText"/>).</summary>
    public string DeclaredRangeText => Timeline.Signal.Minimum == 0 && Timeline.Signal.Maximum == 0
        ? "not specified"
        : $"{Timeline.Signal.Minimum:G6} … {Timeline.Signal.Maximum:G6}{UnitSuffix}";

    public string ScalingText =>
        $"factor {Timeline.Signal.Factor:G6} · offset {Timeline.Signal.Offset:G6}";

    public string BitLayoutText =>
        $"start bit {Timeline.Signal.StartBit} · {Timeline.Signal.BitLength} bit · " +
        $"{Timeline.Signal.ByteOrder} · {(Timeline.Signal.IsSigned ? "signed" : "unsigned")} · {Timeline.Signal.ValueType}";

    /// <summary>Every raw→text mapping the DBC defines for this signal, or empty when it has none
    /// (a plain numeric/physical signal).</summary>
    public string ValueTableText => Timeline.Signal.ValueDescriptions is { Count: > 0 } table
        ? string.Join(Environment.NewLine, table.OrderBy(entry => entry.Key)
            .Select(entry => $"{entry.Key} = {entry.Value}"))
        : "—";

    public bool HasValueTable => Timeline.Signal.ValueDescriptions is { Count: > 0 };

    /// <summary>Stable digital/enum rendering based on DBC semantics rather
    /// than the number of different values in the current zoom slice. A
    /// sparsely sampled temperature must not turn into a logic signal when
    /// the analyst zooms down to only two points.</summary>
    public bool IsDigitalSignal =>
        HasValueTable ||
        Timeline.Signal.IsMultiplexor ||
        (Timeline.Signal.ValueType == DbcSignalValueType.Integer &&
         Timeline.Signal.BitLength <= 2);

    /// <summary>Min/max/mean actually observed in this trace, as opposed to the DBC's declared
    /// engineering range — the two often differ and both matter to an analyst.</summary>
    public string ObservedRangeText => _observedRangeText;

    private string UnitSuffix => string.IsNullOrWhiteSpace(Unit) ? string.Empty : " " + Unit;

    /// <summary>Physical value, raw value and (if the DBC defines one) decoded state text for the
    /// sample at or before the given timestamp — CAN sample-and-hold semantics, matching how the
    /// rest of the app reads a signal's value at a point in time.</summary>
    public (string PhysicalText, string RawText, string StateText) ValueAt(long timestampNanoseconds)
    {
        var sample = Timeline.SampleAt(timestampNanoseconds);
        if (sample is not { } found)
        {
            return ("—", "—", "—");
        }

        var physicalText = $"{found.Value.ToString("G6", CultureInfo.InvariantCulture)}{UnitSuffix}";
        var signal = Timeline.Signal;
        // Display exact-looking raw codes only when a safe, one-to-one linear
        // integer inversion is possible. Otherwise show unknown rather than
        // claim an unsupported/segmented conversion has a unique raw source.
        var hasRaw = DbcSignalRawMath.TryReconstructRaw(signal, found.Value, out var raw);
        var rawText = hasRaw ? raw.ToString("0", CultureInfo.InvariantCulture) : "—";
        var stateText = hasRaw ? signal.GetValueDescription(raw) ?? "—" : "—";
        return (physicalText, rawText, stateText);
    }

    /// <summary>Explicitly distinguishes the last recorded source frame
    /// from a value held after it. No message cycle or ECU failure is inferred
    /// from age alone; multiplex branches may intentionally be inactive.</summary>
    public (string AgeText, string ProvenanceText) EvidenceAt(long timestampNanoseconds)
    {
        var found = Timeline.SampleAt(timestampNanoseconds);
        if (found is not { } sample)
            return ("Not yet observed", "No source frame recorded at or before this cursor");

        var ageSeconds = Math.Max(0d,
            ((double)timestampNanoseconds - sample.TimestampNanoseconds) / 1_000_000_000d);
        var age = ageSeconds switch
        {
            <= 0 => "At source frame",
            < 0.001 => $"Held {ageSeconds * 1_000_000d:F0} µs",
            < 1 => $"Held {ageSeconds * 1_000d:F0} ms",
            _ => $"Held {ageSeconds:F2} s"
        };
        var frame = sample.FrameIndex >= 0
            ? $"Recorded frame #{sample.FrameIndex + 1:N0}"
            : "Source frame index unavailable";
        return (age, $"{frame} · t={sample.TimestampNanoseconds / 1_000_000_000d:F6}s · {age}");
    }

    private static readonly Dictionary<string, PlotLineStyle> LineStyles = [];

    /// <summary>Per-signal line style (keyboard: L on the plot). Remembered for the session by signal key, so it
    /// survives the series being rebuilt; not written to disk.</summary>
    public PlotLineStyle LineStyle
    {
        get => LineStyles.TryGetValue(Timeline.Key, out var style) ? style : PlotLineStyle.Auto;
        set
        {
            if (LineStyle == value) return;
            LineStyles[Timeline.Key] = value;
            OnPropertyChanged();
        }
    }

    public bool IsPlotted
    {
        get => _isPlotted;
        set => SetProperty(ref _isPlotted, value);
    }

    /// <summary>True while this row's inline mini-plot is open. Toggled only by the Details
    /// arrow (or row double-click) — never by mere selection, so clicking around the list can't
    /// accidentally open or close charts.</summary>
    public bool IsDetailOpen
    {
        get => _isDetailOpen;
        set => SetProperty(ref _isDetailOpen, value);
    }

    /// <summary>Swatch colour assigned by the plot control while the series is drawn.</summary>
    public Brush PlotColor
    {
        get => _plotColor;
        set => SetProperty(ref _plotColor, value);
    }
}
