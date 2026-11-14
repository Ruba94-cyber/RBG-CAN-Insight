using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;

namespace RbgCanInsight.App;

/// <summary>Difference + statistic view for the frames the analyst marked (M) or selected in the Trace:
/// changed properties, changed payload bytes (with the differing bits) and decoded signal values with
/// min / max / mean over exactly those frames.</summary>
public partial class FrameDifferenceWindow : Window
{
    // Public properties: WPF bindings do not see record positional fields as properties of a private type.
    public sealed record DiffLine(string Label, string[] Cells, bool Differs, string Summary, bool IsSection);

    public FrameDifferenceWindow(FrameDifferenceResult result)
    {
        InitializeComponent();
        TitleBarTheme.Hook(this);
        SubtitleText.Text = $"{result.FrameHeaders.Count} frames compared · {result.DifferingBytes} differing payload byte(s) · " +
                            $"{result.DifferingSignals} differing decoded signal(s). Highlighted rows differ between frames.";

        DiffGrid.Columns.Add(new DataGridTextColumn { Header = "Item", Binding = new Binding(nameof(DiffLine.Label)), Width = 220 });
        for (var index = 0; index < result.FrameHeaders.Count; index++)
        {
            DiffGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Frame " + result.FrameHeaders[index],
                Binding = new Binding($"Cells[{index}]"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto)
            });
        }

        DiffGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Detail",
            Binding = new Binding(nameof(DiffLine.Summary)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });

        var blank = new string[result.FrameHeaders.Count];
        Array.Fill(blank, string.Empty);
        var lines = new List<DiffLine>();
        void Section(string title, IReadOnlyList<FrameDifferenceRow> rows)
        {
            if (rows.Count == 0) return;
            lines.Add(new DiffLine(title, blank, false, string.Empty, true));
            lines.AddRange(rows.Select(row => new DiffLine(row.Label, row.Cells.ToArray(), row.Differs, row.Summary, false)));
        }

        Section("PROPERTIES", result.Properties);
        Section("PAYLOAD BYTES", result.Bytes);
        Section("DECODED SIGNALS", result.Signals);
        DiffGrid.ItemsSource = lines;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
