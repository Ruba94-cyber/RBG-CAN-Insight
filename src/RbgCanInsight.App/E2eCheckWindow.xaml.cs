using RbgCanInsight.App.Common;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using RbgCanInsight.Analysis.E2E;
using RbgCanInsight.App.ViewModels;
using RbgCanInsight.Domain;

namespace RbgCanInsight.App;

/// <summary>Self-contained AUTOSAR E2E (profiles 1, 2, 5) check of one CAN ID in the loaded trace.</summary>
public partial class E2eCheckWindow : Window
{
    private readonly IReadOnlyList<CanFrame> _frames;
    private readonly IReadOnlyList<E2eIdEntry> _ids;
    private IReadOnlyList<E2eRow> _rows = [];
    private E2eIdEntry? _selected;
    private E2eHint? _hint;

    public static void Open(Window owner, MainViewModel vm)
    {
        new E2eCheckWindow(vm.Frames) { Owner = owner }.Show();
    }

    public E2eCheckWindow(IReadOnlyList<CanFrame> frames)
    {
        InitializeComponent();
        RbgCanInsight.App.Common.TitleBarTheme.Hook(this);
        _frames = frames;
        _ids = E2eCheckLogic.ListIds(frames);
        RefreshIds();
        if (_ids.Count == 0)
            SummaryHeadline.Text = "No CAN frames are loaded. Open a trace first, then reopen this window.";
    }

    private static E2eProfile ProfileOf(ComboBox box) =>
        ((box.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "P02" => E2eProfile.P02,
            "P05" => E2eProfile.P05,
            _ => E2eProfile.P01
        };

    private void RefreshIds()
    {
        var keep = _selected;
        IdList.ItemsSource = _ids.Where(e => E2eCheckLogic.Matches(e, SearchBox.Text)).ToList();
        if (keep is not null && IdList.Items.Contains(keep)) IdList.SelectedItem = keep;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (IdList is not null) RefreshIds();
    }

    private void OnIdSelected(object sender, SelectionChangedEventArgs e)
    {
        _selected = IdList.SelectedItem as E2eIdEntry;
        SettingsPanel.IsEnabled = _selected is not null;
        CheckButton.IsEnabled = _selected is not null;
        _hint = null;
        HintButton.Visibility = Visibility.Collapsed;
        if (_selected is null) return;
        try
        {
            var stream = E2eCheckLogic.StreamFrames(_frames, _selected);
            _hint = E2eCheckLogic.SuggestHint(stream);
            if (_hint is not null)
            {
                HintText.Text = _hint.Text;
                HintButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            _hint = null;
            SummaryDetails.Text = "Could not analyse this ID for hints: " + ex.Message;
        }
    }

    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CrcByteBox is null || CounterBitBox is null) return;
        var d = E2eCheckLogic.DefaultLayout(ProfileOf(ProfileBox));
        CrcByteBox.Text = d.CrcByte.ToString();
        CounterBitBox.Text = d.CounterBit.ToString();
    }

    private void OnHintClick(object sender, RoutedEventArgs e)
    {
        if (_hint is null) return;
        ProfileBox.SelectedIndex = _hint.Profile switch { E2eProfile.P02 => 1, E2eProfile.P05 => 2, _ => 0 };
        CrcByteBox.Text = _hint.CrcByte.ToString();
        CounterBitBox.Text = _hint.CounterBitOffset.ToString();
    }

    private void OnCheckClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_selected is null)
            {
                ShowResult(E2eChip.Amber, "Pick a CAN ID from the list first.", "");
                return;
            }
            var stream = E2eCheckLogic.StreamFrames(_frames, _selected);
            var (cfg, error) = E2eCheckLogic.BuildConfig(_selected, ProfileOf(ProfileBox), DataIdBox.Text, CycleBox.Text,
                CrcByteBox.Text, CounterBitBox.Text, E2eCheckLogic.DominantLength(stream));
            if (cfg is null)
            {
                ShowResult(E2eChip.Amber, "Check the settings", error ?? "");
                return;
            }
            var report = E2eChecker.Check(stream, [cfg]);
            if (report.ConfigError is not null || report.Summaries.Count == 0)
            {
                ShowResult(E2eChip.Amber, "Check the settings", report.ConfigError ?? "No result.");
                return;
            }
            var summary = report.Summaries[0];
            _rows = E2eCheckLogic.BuildRows(summary);
            var outcome = E2eCheckLogic.Summarize(summary);
            ShowResult(outcome.Chip, outcome.Headline, outcome.Details);
            ApplyFilter();
            ExportButton.IsEnabled = _rows.Count > 0;
        }
        catch (Exception ex)
        {
            ShowResult(E2eChip.Amber, "The check could not run", ex.Message);
        }
    }

    private void ShowResult(E2eChip chip, string headline, string details)
    {
        SummaryHeadline.Text = headline;
        SummaryDetails.Text = details;
        var key = chip switch { E2eChip.Green => "TealBrush", E2eChip.Red => "ErrorBrush", _ => "WarningBrush" };
        Chip.BorderBrush = (System.Windows.Media.Brush)FindResource(key);
        SummaryHeadline.Foreground = (System.Windows.Media.Brush)FindResource(key);
        // The chip state is also exposed as text for assistive technology and UI tests.
        AutomationProperties.SetName(SummaryHeadline, headline);
        AutomationProperties.SetHelpText(SummaryHeadline, chip.ToString());
        if (_rows.Count == 0) ResultGrid.ItemsSource = null;
    }

    private void OnProblemsOnlyClick(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter() =>
        ResultGrid.ItemsSource = ProblemsOnly.IsChecked == true ? _rows.Where(r => r.IsProblem).ToList() : _rows;

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "e2e_check.csv" };
            if (dialog.ShowDialog(this) != true) return;
            var visible = ProblemsOnly.IsChecked == true ? _rows.Where(r => r.IsProblem) : _rows;
            File.WriteAllText(dialog.FileName, E2eCheckLogic.ToCsv(visible), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            ShowResult(E2eChip.Amber, "Export failed", ex.Message);
        }
    }
}
