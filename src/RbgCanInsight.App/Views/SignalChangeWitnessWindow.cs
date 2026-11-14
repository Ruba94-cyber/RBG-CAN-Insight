using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;

namespace RbgCanInsight.App.Views;

/// <summary>
/// An evidence drill-down, not a second workbench. Lists actual before/after
/// CAN frames behind a selected pairwise statistical link.
/// </summary>
public sealed class SignalChangeWitnessWindow : Window
{
    public SignalChangeWitnessWindow(
        Window owner, SignalChangeWitnessReport report,
        Func<bool> evidenceCurrent, Action<int> openSourceFrame)
    {
        Owner = owner;
        Icon = owner.Icon;
        Title = "Observed CAN changes — original frame evidence";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = Math.Min(1090, SystemParameters.WorkArea.Width - 24);
        Height = Math.Min(580, SystemParameters.WorkArea.Height - 24);
        MinWidth = 550;
        MinHeight = 350;
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        TitleBarTheme.Hook(this);

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
        header.Children.Add(new TextBlock
        {
            Text = $"{report.Reference.Message.Name}.{report.Reference.Signal.Name}" +
                   "  ↔  " +
                   $"{report.Candidate.Message.Name}.{report.Candidate.Signal.Name}",
            FontWeight = FontWeights.SemiBold, FontSize = 16,
            TextWrapping = TextWrapping.Wrap
        });
        header.Children.Add(new TextBlock
        {
            Text = report.Summary + Environment.NewLine +
                "Each row pairs TWO separate actual recorded value changes, never interpolated or held values. " +
                "The observed time order is not proof that one CAN parameter influences the other.",
            Foreground = Brushes.Gray, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
        Grid.SetRow(header, 0); root.Children.Add(header);

        var rows = new DataGrid
        {
            ItemsSource = report.Witnesses,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            EnableRowVirtualization = true, EnableColumnVirtualization = true,
            FontSize = 11.5
        };
        AutomationProperties.SetAutomationId(rows, "relationWitness.events");
        static DataGridTextColumn Col(string title, string prop, int width) =>
            new() { Header = title, Width = new DataGridLength(width),
                Binding = new Binding(prop) };
        rows.Columns.Add(Col("Reference: before → after", nameof(SignalChangeWitness.ReferenceChangeText), 235));
        rows.Columns.Add(Col("Candidate: before → after", nameof(SignalChangeWitness.CandidateChangeText), 235));
        rows.Columns.Add(Col("Observed order", nameof(SignalChangeWitness.TimingText), 210));
        rows.Columns.Add(Col("Original CAN frames", nameof(SignalChangeWitness.OriginalFrameText), 240));
        rows.Columns.Add(Col("Reference timestamp (ns)", "ReferenceAfter.TimestampNanoseconds", 180));
        rows.Columns.Add(Col("Candidate timestamp (ns)", "CandidateAfter.TimestampNanoseconds", 180));
        Grid.SetRow(rows, 1); root.Children.Add(rows);

        var footer = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        Button AddAction(string title, string id, Func<SignalChangeWitness, int> frame)
        {
            var button = new Button
            {
                Content = title, Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 8, 4)
            };
            AutomationProperties.SetAutomationId(button, id);
            button.Click += (_, _) =>
            {
                if (rows.SelectedItem is not SignalChangeWitness witness)
                    return;
                if (!evidenceCurrent())
                {
                    MessageBox.Show(this,
                        "The CAN trace or DBC changed. Please reopen the relationship map.",
                        "Original frame source changed",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                openSourceFrame(frame(witness));
            };
            footer.Children.Add(button);
            return button;
        }
        AddAction("Open reference before", "relationWitness.referenceBefore",
            witness => witness.ReferenceBefore.FrameIndex);
        AddAction("Open reference after", "relationWitness.referenceAfter",
            witness => witness.ReferenceAfter.FrameIndex);
        AddAction("Open related before", "relationWitness.candidateBefore",
            witness => witness.CandidateBefore.FrameIndex);
        AddAction("Open related after", "relationWitness.candidateAfter",
            witness => witness.CandidateAfter.FrameIndex);
        var close = new Button
        {
            Content = "Close", Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 8, 4)
        };
        close.Click += (_, _) => Close();
        footer.Children.Add(close);
        Grid.SetRow(footer, 2); root.Children.Add(footer);

        if (report.Witnesses.Count > 0) rows.SelectedIndex = 0;
    }
}
