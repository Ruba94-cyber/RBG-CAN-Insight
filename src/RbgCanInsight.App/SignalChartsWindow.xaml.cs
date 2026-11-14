using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using RbgCanInsight.Analysis.Analytics;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App;

/// <summary>
/// Histogram, XY (scatter) and Spectrum views over the recorded signals. All numbers come from
/// <see cref="SignalAnalytics"/>; results that cannot be computed are explained in text, never in a dialog.
/// </summary>
public partial class SignalChartsWindow : Window
{
    private sealed record SignalChoice(PlotSeriesViewModel Series, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record PeakRow(int Rank, string Frequency, string Amplitude);

    private readonly List<SignalChoice> _choices;
    private bool _loading = true;

    private HistogramResult? _hist;
    private string _histName = "signal";
    private HistogramWeighting _histWeighting;
    private List<XyPoint>? _xy;
    private FftResult? _fft;

    public static void Open(Window owner, MainViewModel vm, PlotSeriesViewModel? preselected)
    {
        var window = new SignalChartsWindow(vm, preselected) { Owner = owner };
        window.Show();
    }

    public SignalChartsWindow(MainViewModel vm, PlotSeriesViewModel? preselected)
    {
        InitializeComponent();
        TitleBarTheme.Hook(this);
        _choices = vm.Signals.AllSeries
            .Where(s => s.SampleCount > 0)
            .OrderBy(s => s.SignalName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.MessageName, StringComparer.OrdinalIgnoreCase)
            .Select(s => new SignalChoice(s, $"{s.SignalName} - {s.MessageName} ({s.ChannelText})"))
            .ToList();

        SpWindow.ItemsSource = Enum.GetValues<FftWindow>();
        SpWindow.SelectedItem = FftWindow.Hann;

        var first = _choices.FirstOrDefault(c => ReferenceEquals(c.Series, preselected)) ?? _choices.FirstOrDefault();
        var second = _choices.FirstOrDefault(c => c != first && c.Series.SignalName != first?.Series.SignalName)
                     ?? _choices.FirstOrDefault(c => c != first) ?? first;
        Wire(HistSignal, HistFilter, first, OnHistogramUpdate);
        Wire(XySignalX, XyFilterX, first, OnXyChanged);
        Wire(XySignalY, XyFilterY, second, OnXyChanged);
        Wire(SpSignal, SpFilter, first, OnSpectrumSignalChanged);
        HistBins.KeyDown += (_, e) => { if (e.Key == Key.Enter) OnHistogramUpdate(this, e); };
        HistWeight.SelectionChanged += (_, _) => { if (!_loading) OnHistogramUpdate(this, EventArgs.Empty); };
        SpWindow.SelectionChanged += (_, _) => { if (!_loading) OnSpectrumCompute(this, EventArgs.Empty); };
        SpResample.SelectionChanged += (_, _) => { if (!_loading) OnSpectrumCompute(this, EventArgs.Empty); };
        SpRemoveMean.Click += (_, _) => OnSpectrumCompute(this, EventArgs.Empty);

        _loading = false;
        FillSpectrumRate();
        RefreshHistogram();
        RefreshXy();
        RefreshSpectrum();
    }

    // ------------------------------------------------------------ pickers

    private void Wire(ComboBox combo, TextBox filter, SignalChoice? initial, Action<object, EventArgs> changed)
    {
        var view = new ListCollectionView(_choices);
        combo.ItemsSource = view;
        combo.SelectedItem = initial;
        filter.TextChanged += (_, _) =>
        {
            var keep = combo.SelectedItem;
            var terms = filter.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            view.Filter = o => o == keep || (o is SignalChoice c &&
                terms.All(t => c.Label.Contains(t, StringComparison.OrdinalIgnoreCase)));
            combo.SelectedItem = keep;
            if (terms.Length > 0 && view.Count > 0) combo.IsDropDownOpen = true;
        };
        combo.SelectionChanged += (_, _) => { if (!_loading && combo.SelectedItem is not null) changed(this, EventArgs.Empty); };
    }

    private static PlotSeriesViewModel? Selected(ComboBox combo) => (combo.SelectedItem as SignalChoice)?.Series;

    private static List<AnalyticsSample> ToSeries(PlotSeriesViewModel s)
    {
        var src = s.Samples;
        var list = new List<AnalyticsSample>(src.Count);
        foreach (var x in src)
            list.Add(new AnalyticsSample(x.TimestampNanoseconds, x.Value));
        return list;
    }

    private static string F(double v) => double.IsNaN(v) ? "n/a" : v.ToString("G6", CultureInfo.InvariantCulture);

    private static string Unit(PlotSeriesViewModel s) => string.IsNullOrWhiteSpace(s.Unit) ? "" : " " + s.Unit;

    private static string AxisName(PlotSeriesViewModel s) =>
        string.IsNullOrWhiteSpace(s.Unit) ? s.SignalName : $"{s.SignalName} [{s.Unit}]";

    // ---------------------------------------------------------- histogram

    private void OnHistogramUpdate(object sender, EventArgs e) => RefreshHistogram();

    private void RefreshHistogram()
    {
        _hist = null;
        var s = Selected(HistSignal);
        if (s is null)
        {
            HistSummary.Text = _choices.Count == 0
                ? "No recorded signals yet. Load a DBC and a trace, then open this window again."
                : "Choose a signal to see its histogram.";
            HistChart.ShowMessage(HistSummary.Text);
            return;
        }
        if (!int.TryParse(HistBins.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bins) || bins is < 1 or > 500)
        {
            HistSummary.Text = "Bins must be a whole number from 1 to 500.";
            HistChart.ShowMessage(HistSummary.Text);
            return;
        }
        var series = ToSeries(s);
        var weighting = HistWeight.SelectedIndex == 1 ? HistogramWeighting.BySample : HistogramWeighting.ByTimeHeld;
        var sum = SignalAnalytics.Summarize(series);
        if (sum.Count == 0)
        {
            HistSummary.Text = $"{s.SignalName} has no valid (non-NaN) samples, so no histogram can be drawn.";
            HistChart.ShowMessage(HistSummary.Text);
            return;
        }
        var h = SignalAnalytics.Histogram(series, bins, weighting);
        double[] heights = weighting == HistogramWeighting.ByTimeHeld ? h.Weights : h.Counts.Select(c => (double)c).ToArray();
        if (heights.All(v => v <= 0))
        {
            HistSummary.Text = $"{s.SignalName}: all {sum.Count:N0} samples fall at one instant, so there is no time to weight by. Switch to \"Per sample (count)\".";
            HistChart.ShowMessage(HistSummary.Text);
            return;
        }
        _hist = h; _histName = s.SignalName; _histWeighting = weighting;
        int top = Array.IndexOf(heights, heights.Max());
        string valueRange = sum.Min == sum.Max ? $"constant at {F(sum.Min)}{Unit(s)}" : $"from {F(sum.Min)} to {F(sum.Max)}{Unit(s)}";
        HistSummary.Text = $"{s.SignalName}: {sum.Count:N0} samples, {valueRange}. Mean {F(sum.Mean)}, median {F(sum.Median)}, " +
            $"time-weighted mean {F(sum.TimeWeightedMean)}. Most {(weighting == HistogramWeighting.ByTimeHeld ? "time" : "samples")} " +
            $"in [{F(h.Edges[top])} .. {F(h.Edges[top + 1])}{(top == heights.Length - 1 ? "]" : ")")} ({bins} bins).";
        HistChart.ShowBars(h.Edges, heights, AxisName(s), weighting == HistogramWeighting.ByTimeHeld ? "Time held [s]" : "Samples");
    }

    private void OnHistogramExport(object sender, RoutedEventArgs e)
    {
        if (_hist is null) { HistSummary.Text = "Nothing to export yet. " + HistSummary.Text; return; }
        var rows = new List<string[]>();
        for (int i = 0; i < _hist.Counts.Length; i++)
            rows.Add([Num(_hist.Edges[i]), Num(_hist.Edges[i + 1]), _hist.Counts[i].ToString(CultureInfo.InvariantCulture), Num(_hist.Weights[i])]);
        ExportCsv($"histogram_{_histName}", ["bin_low", "bin_high", "samples",
            _histWeighting == HistogramWeighting.ByTimeHeld ? "time_held_s" : "weight_samples"], rows);
    }

    // ------------------------------------------------------------------ XY

    private void OnXyChanged(object sender, EventArgs e) => RefreshXy();

    private void RefreshXy()
    {
        _xy = null;
        var sx = Selected(XySignalX);
        var sy = Selected(XySignalY);
        if (sx is null || sy is null)
        {
            XyCorrelation.Text = _choices.Count == 0
                ? "Correlation r = n/a. No recorded signals yet - load a DBC and a trace first."
                : "Correlation r = n/a. Choose a signal for both X and Y.";
            XyChart.ShowMessage(XyCorrelation.Text);
            return;
        }
        var pts = SignalAnalytics.XyPairs(ToSeries(sx), ToSeries(sy));
        var r = SignalAnalytics.PearsonCorrelation(pts);
        var reg = SignalAnalytics.Regress(pts);
        if (pts.Count < 2)
        {
            XyCorrelation.Text = $"Correlation r = n/a. Only {pts.Count} moment(s) where both signals have a value; at least 2 are needed.";
            XyChart.ShowMessage(XyCorrelation.Text);
            return;
        }
        _xy = pts;
        if (double.IsNaN(r))
        {
            XyCorrelation.Text = $"Correlation r = n/a, R² = n/a. {(pts.Select(p => p.X).Distinct().Count() < 2 ? sx.SignalName : sy.SignalName)} never changes, " +
                $"so there is no trend to measure ({pts.Count:N0} paired points).";
        }
        else
        {
            string strength = Math.Abs(r) switch { >= 0.9 => "very strong", >= 0.7 => "strong", >= 0.4 => "moderate", >= 0.2 => "weak", _ => "almost none" };
            XyCorrelation.Text = $"Correlation r = {r:0.000}, R² = {F(reg.RSquared)} ({strength} {(r < 0 ? "inverse " : "")}relationship). " +
                $"Regression line: Y = {F(reg.Slope)} × X + {F(reg.Intercept)} ({pts.Count:N0} paired points).";
        }
        XyChart.ShowScatter(pts.Select(p => (p.X, p.Y)).ToList(),
            double.IsNaN(reg.Slope) ? null : (reg.Slope, reg.Intercept), AxisName(sx), AxisName(sy));
    }

    private void OnXyExport(object sender, RoutedEventArgs e)
    {
        if (_xy is null) { XyCorrelation.Text = "Nothing to export yet. " + XyCorrelation.Text; return; }
        var sx = Selected(XySignalX); var sy = Selected(XySignalY);
        var rows = _xy.Select(p => new[] { Num(p.TimestampNs / 1e9), Num(p.X), Num(p.Y) }).ToList();
        ExportCsv($"xy_{sx?.SignalName}_{sy?.SignalName}", ["time_s", "x_" + sx?.SignalName, "y_" + sy?.SignalName], rows);
    }

    // ------------------------------------------------------------ spectrum

    private void OnSpectrumSignalChanged(object sender, EventArgs e)
    {
        FillSpectrumRate();
        RefreshSpectrum();
    }

    private void FillSpectrumRate()
    {
        var s = Selected(SpSignal);
        var rate = s is null ? null : ChartMath.MedianSampleRateHz(ToSeries(s));
        SpRate.Text = rate is { } r && double.IsFinite(r) ? r.ToString("G6", CultureInfo.InvariantCulture) : "";
    }

    private void OnSpectrumRateKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) RefreshSpectrum();
    }

    private void OnSpectrumCompute(object sender, EventArgs e)
    {
        if (!_loading) RefreshSpectrum();
    }

    private void RefreshSpectrum()
    {
        _fft = null;
        SpPeaks.ItemsSource = null;
        var s = Selected(SpSignal);
        if (s is null)
        {
            Fail(_choices.Count == 0 ? "No recorded signals yet - load a DBC and a trace first." : "Choose a signal to see its spectrum.");
            return;
        }
        var text = SpRate.Text.Trim().Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) || !(rate > 0) || double.IsInfinity(rate))
        {
            Fail("Enter the grid rate as a positive number of hertz (for example 100). If it is empty, the trace does not have enough distinct timestamps to derive one.");
            return;
        }
        var series = ToSeries(s);
        var window = SpWindow.SelectedItem is FftWindow w ? w : FftWindow.Hann;
        var mode = SpResample.SelectedIndex == 1 ? ResampleMode.Linear : ResampleMode.SampleAndHold;
        FftResult fft;
        try
        {
            fft = SignalAnalytics.Fft(series, rate, mode, window, SpRemoveMean.IsChecked == true, excludeDcFromPeak: true);
        }
        catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "gridRateHz")
        {
            Fail($"Spectrum not computed: the grid is too large for this rate and recording length. Lower the grid rate (limit {SignalAnalytics.MaxFftGridPoints:N0} points). {FirstSentence(ex.Message)}");
            return;
        }
        catch (ArgumentException ex)
        {
            Fail($"Spectrum not computed: {FirstSentence(ex.Message)}");
            return;
        }
        _fft = fft;
        var peaks = ChartMath.TopPeaks(fft, 5);
        SpPeaks.ItemsSource = peaks.Select((p, i) => new PeakRow(i + 1, F(p.FrequencyHz), F(p.Amplitude))).ToList();
        SpMessage.Text = $"Strongest frequency: {F(fft.PeakFrequencyHz)} Hz (amplitude {F(fft.PeakAmplitude)}{Unit(s)}). " +
            $"{fft.GridPoints:N0} grid points at {F(fft.SampleRateHz)} Hz, frequency step {F(fft.SampleRateHz / fft.FftSize)} Hz, " +
            $"highest frequency shown {F(fft.FrequenciesHz[^1])} Hz.";
        var (fx, fy) = Decimate(fft.FrequenciesHz, fft.Amplitudes, 4000);
        SpChart.ShowLine(fx, fy, "Frequency [Hz]", "Amplitude" + Unit(s));
    }

    private void Fail(string message)
    {
        SpMessage.Text = message;
        SpChart.ShowMessage(message);
    }

    private static string FirstSentence(string message)
    {
        var i = message.IndexOf(" (Parameter", StringComparison.Ordinal);
        return i > 0 ? message[..i] : message;
    }

    /// <summary>Peak-preserving reduction for drawing only: keeps the maximum of each bucket.</summary>
    private static (double[] X, double[] Y) Decimate(double[] x, double[] y, int maxPoints)
    {
        if (x.Length <= maxPoints) return (x, y);
        var ox = new List<double>(maxPoints); var oy = new List<double>(maxPoints);
        double bucket = (double)x.Length / maxPoints;
        for (int b = 0; b < maxPoints; b++)
        {
            int from = (int)(b * bucket), to = Math.Min(x.Length, (int)((b + 1) * bucket));
            if (to <= from) continue;
            int best = from;
            for (int i = from + 1; i < to; i++) if (y[i] > y[best]) best = i;
            ox.Add(x[best]); oy.Add(y[best]);
        }
        return (ox.ToArray(), oy.ToArray());
    }

    private void OnSpectrumExport(object sender, RoutedEventArgs e)
    {
        if (_fft is null) { SpMessage.Text = "Nothing to export yet. " + SpMessage.Text; return; }
        var rows = new List<string[]>(_fft.Amplitudes.Length);
        for (int i = 0; i < _fft.Amplitudes.Length; i++)
            rows.Add([Num(_fft.FrequenciesHz[i]), Num(_fft.Amplitudes[i])]);
        ExportCsv($"spectrum_{Selected(SpSignal)?.SignalName}", ["frequency_hz", "amplitude"], rows);
    }

    // -------------------------------------------------------------- export

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private void ExportCsv(string suggestedName, string[] header, IEnumerable<string[]> rows)
    {
        var safe = string.Concat(suggestedName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = safe + ".csv" };
        if (dialog.ShowDialog(this) != true)
            return;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", header.Select(Quote)));
        // Numeric cells are written raw so negative numbers stay numeric; only text cells are neutralised.
        foreach (var row in rows)
            sb.AppendLine(string.Join(",", row));
        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
    }

    private static string Quote(string text)
    {
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@')
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
