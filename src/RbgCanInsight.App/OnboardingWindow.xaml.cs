using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App;

/// <summary>
/// First-launch interactive tour. Each step describes one workspace with an invented worked
/// example (nothing on disk is required), and switches the main window behind it to the
/// workspace being described so the user sees the real UI while reading. Fully skippable:
/// "Skip tour" or Esc closes it at any point, and it never comes back on its own — it can be
/// reopened from the "Quick tour" button in the navigation rail.
/// </summary>
public partial class OnboardingWindow : Window
{
    private sealed record TourStep(
        string Kicker,
        string Title,
        string Body,
        string? ExampleTitle,
        string? Example,
        string? Tip,
        int? NavIndex,
        int? UdsViewIndex = null);

    private readonly MainViewModel _viewModel;
    private readonly IReadOnlyList<TourStep> _steps;
    private int _stepIndex;

    public OnboardingWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        TitleBarTheme.Hook(this);
        _steps = BuildSteps();
        Closed += (_, _) => AppSettingsStore.SaveOnboardingShown();
        ShowStep(0);
    }

    private static IReadOnlyList<TourStep> BuildSteps() =>
    [
        new TourStep(
            "WELCOME",
            "Welcome to RBG CAN Insight",
            "RBG CAN Insight is an offline studio for analysing automotive CAN traces: open a recorded " +
            "trace, decode it with your DBC databases, find what changed, and map diagnostic DIDs to CAN " +
            "signals — all without connecting to a vehicle.\n\n" +
            "This short tour walks through every workspace with small invented examples. It takes about " +
            "two minutes, and you can leave at any time with Skip tour or Esc.",
            "SUPPORTED FILES",
            "Traces:      ASC · BLF · TRC · CSV · candump LOG · MF4\n" +
            "Databases:   DBC (and best-effort ARXML)\n" +
            "Diagnostics: CDD · ODX",
            "Press F1 to reopen this tour. Ctrl+K searches across loaded data; Ctrl+F finds inside the current workspace.",
            0),

        new TourStep(
            "STEP 1 · DASHBOARD",
            "Load your files from the Dashboard",
            "The Dashboard is the starting point: card 1 opens a CAN trace, card 2 loads one or more DBC " +
            "databases, card 3 loads diagnostic data (CDD/ODX). It also shows the top changing signals of " +
            "the loaded trace and your recent files.\n\n" +
            "You can also drag && drop any mix of these files anywhere in the window — the tool sorts out " +
            "what each file is.",
            "A TYPICAL SESSION",
            "1. Open  road_test_brake_noise.blf   (a 20-minute recording)\n" +
            "2. Load  powertrain.dbc + chassis.dbc\n" +
            "3. Load  brake_ecu.cdd               (optional, for diagnostics)\n" +
            "4. Save session → reopen everything with one click tomorrow",
            "\"Save session\" remembers the loaded files, filters, watch list and plots. \"Save preset\" only remembers UI settings.",
            0),

        new TourStep(
            "STEP 2 · TRACE",
            "Explore frames in the Trace view",
            "Every frame of the trace in a fast virtual table: timestamp, ID, ECU, decoded message name, " +
            "payload bytes and a live interpretation column. Click any row to expand its decoded signals " +
            "inline — with previous value, delta and meaning per signal.\n\n" +
            "One search box does two jobs: press Enter to filter rows, or F3/▲▼ to jump between matches " +
            "without hiding anything. Replay the trace at any speed, scrub the timeline, and mark rows " +
            "with categorised markers (manual, timing, diagnostic, range).",
            "SEARCH EXAMPLES",
            "18FEF100            → frames of this hex ID\n" +
            "payload:62F190      → frames whose data contains 62 F1 90\n" +
            "ch1 EngineData      → channel 1 AND message name\n" +
            "Bookmark rule:  VehicleSpeed > 120   marks every match",
            "Bookmark rules (the collapsible bar above the table) can mark hundreds of matching frames in one click — e.g. every frame where a signal crosses a threshold.",
            1),

        new TourStep(
            "STEP 2b · TRACE + UDS",
            "Read diagnostic (UDS) traffic in the trace",
            "Frames on diagnostic identifiers are interpreted automatically in the UDS column: ISO-TP " +
            "framing plus the UDS service inside — session control, ECU reset, security access, routine " +
            "control, and Read/WriteDataByIdentifier with the DID spelled out.\n\n" +
            "If a CDD/ODX file is loaded, the DID number is resolved to its parameter name, so you can " +
            "see at a glance which diagnostic value the tester was reading.",
            "EXAMPLE (INVENTED)",
            "ID 7E0  data 03 22 F1 90 …   → UDS Req ReadDataByIdentifier — DID 0xF190 (VIN)\n" +
            "ID 7E8  data 10 17 62 F1 90… → UDS Resp ReadDataByIdentifier — DID 0xF190 (VIN) · first frame\n" +
            "ID 7E8  data 03 7F 22 31 …   → UDS Neg ✗ ReadDataByIdentifier → requestOutOfRange",
            "Tip: type \"uds\" in the search box (then Enter) to keep only diagnostic frames — or \"uds:f190\" to isolate one DID conversation.",
            1),

        new TourStep(
            "STEP 2c · UDS ANALYSIS",
            "Reconstruct complete diagnostic conversations",
            "UDS Analysis is a read-only forensic workspace built from the recorded trace. Overview " +
            "counts each service and its positive, negative or missing responses. Transactions joins " +
            "single-frame and multi-frame ISO-TP traffic into tester/ECU conversations, keeps responsePending " +
            "inside the same transaction, measures response time and links every result back to its raw CAN frames.\n\n" +
            "DID samples turns positive ReadDataByIdentifier responses into timestamped physical values using " +
            "CDD/ODX scaling, units, structured fields and enumerations.",
            "INVENTED OFFLINE RESULT",
            "0x22 ReadDataByIdentifier · DID 0xF405 Coolant temperature\n" +
            "Request frame 18,420 → response frames 18,427–18,429\n" +
            "Value: 91.0 °C · response time 18.6 ms · addressing confirmed from catalog",
            "No Send, Start, Clear or Unlock command exists: this area only explains traffic already present in the recording.",
            8,
            0),

        new TourStep(
            "STEP 2d · FAULTS & ROUTINES",
            "Follow fault memory and recorded routines",
            "Fault Memory groups all ReadDTCInformation observations into one lifecycle per DTC: first and " +
            "last observation, ISO status bits, snapshot/extended data, occurrence count and any successful " +
            "ClearDiagnosticInformation event found later in the trace. Select a DTC to inspect its chronological evidence.\n\n" +
            "Routines reconstruct recorded Start, Stop and RequestResults steps, including input bytes, output/result " +
            "bytes, negative responses, responsePending count, duration and every contributing frame. This does not " +
            "execute a routine; it analyses one that another tester already executed while the trace was recorded.",
            "INVENTED ROUTINE TIMELINE",
            "0x0203 Sensor calibration\n" +
            "12.400 s  Start · input StaticMode\n" +
            "12.430 s  NRC 0x78 responsePending\n" +
            "15.910 s  RequestResults · Completed · deviation 0.42°",
            "A missing terminal result is shown as Incomplete; a negative response is shown as Failed with its NRC.",
            8,
            3),

        new TourStep(
            "STEP 2e · UDS ↔ CAN",
            "Validate diagnostic values against CAN signals",
            "The UDS ↔ CAN view does more than compare names. It aligns physical DID samples with decoded DBC " +
            "signal samples, searches for timing lag, fits scale and offset, and calculates coverage, Pearson " +
            "correlation, R², mean absolute error and residuals. Confirmed requires enough aligned values and strong " +
            "numeric evidence; weak or constant look-alikes remain Candidate, Rejected or InsufficientData.\n\n" +
            "The normalized overlay makes the two recorded curves easy to compare. The ZIP report exports the " +
            "transactions, DID samples, fault memory, routines and correlation evidence as HTML, JSON, CSV and PDF.",
            "HOW TO READ A RESULT",
            "DID 0x4A21 → VehicleData.VehicleSpeed\n" +
            "Confirmed 96% · 148/152 paired values · lag +20 ms\n" +
            "R² 0.992 · MAE 0.18 km/h · fit y=1.000x+0.02",
            "Treat Candidate as a review queue, not as proof. Confirmed is based on recorded values; the explanation always states why.",
            8,
            5),

        new TourStep(
            "STEP 3 · DBC MANAGER",
            "Manage databases in the DBC Manager",
            "A master-detail catalog of every loaded database: databases → messages → signals, with live " +
            "decode coverage against the open trace. Per database you can enable J1939 PGN matching or " +
            "pin it to one bus so multi-bus traces are not mis-decoded.\n\n" +
            "The \"Unmatched trace IDs\" panel explains why an identifier did not match any database — " +
            "and fixes the options automatically when only one explanation exists.",
            "EXAMPLE",
            "Unmatched ID 18F00503x · 1,204 frames\n" +
            "Hint: matches J1939 PGN 0xF005 in powertrain.dbc —\n" +
            "      enabled \"Match by J1939 PGN\" automatically.",
            "Hover a database's ⚠ badge to read its parse warnings without leaving the view.",
            2),

        new TourStep(
            "STEP 4 · SIGNALS & PLOT",
            "Plot signals over time",
            "Use Ctrl+K to find an observed signal from anywhere and open its exact CAN channel. " +
            "In Signals & Plot, the FIRST columns show signal name, physical value and age of the " +
            "last recorded source frame. Tick Plot for the shared chart or press ↗ Plot for its own " +
            "Quick Plot (Ctrl+Enter on a selected row). Click 'Clear filters' to reveal all recorded " +
            "signals without losing your plotted curves.\n\n" +
            "Click the graph to set X1, Shift/right-click to set X2 and measure Δt. The mouse wheel " +
            "zooms, Shift+wheel pans and Ctrl+0 resets. F3 / Shift+F3 jump to the next/previous " +
            "real value change for the selected signal. By default lines HOLD the last recorded " +
            "value; turn on 'Linear lines' only for visual interpolation. Signals with no new " +
            "samples in the current time window are explicitly marked as such.",
            "MEASURING WITH CURSORS (INVENTED)",
            "Plot EngineSpeed and VehicleSpeed.\n" +
            "Click just before the rev spike → X1\n" +
            "Shift+click at the peak         → X2\n" +
            "Readout:  Δt = 250.000 ms (4.00 Hz)\n" +
            "          EngineSpeed: 840 → 3120 rpm  (Δ 2280)",
            "A hollow full-scale marker identifies an all-ones raw code, NOT necessarily an error or unavailable sensor. Only the actual DBC value table can establish its meaning. Quick Plot shows source frame and age; no fabricated intermediate samples.",
            3),

        new TourStep(
            "STEP 5 · CHANGE MATRIX",
            "Find what changed in the Change Matrix",
            "Every decoded signal ranked by activity: stable, variable, anomalous or missing, with change " +
            "counts, first/last values, amplitude and the largest single step. Select a row to list every " +
            "individual change with timestamps — double-click a change to open its exact frame in Trace.\n\n" +
            "This is the fastest way to answer \"what happened at second 12?\" or \"which signal moved " +
            "when the fault appeared?\".",
            "EXAMPLE WORKFLOW (INVENTED)",
            "Symptom: warning lamp at ~12.4 s.\n" +
            "1. Sort by \"last change\", filter Changed only\n" +
            "2. LampStatus jumps 0 → 1 at 12.402 s\n" +
            "3. One row above: BrakePressure spiked 2.1 → 9.8 bar at 12.398 s\n" +
            "4. Double-click → the exact frame opens in Trace",
            "Use \"Min step\" to hide jittery signals whose largest change is below a threshold you choose.",
            4),

        new TourStep(
            "STEP 6 · BYTE MATRIX",
            "Watch raw payloads in the Byte Matrix",
            "One row per CAN ID showing its payload bytes B0…B7 at any point of the trace timeline — " +
            "changed bytes and bits are highlighted as you scrub or replay. Perfect for reverse " +
            "engineering an unknown message before any DBC exists.\n\n" +
            "Select a row to open the decoded signal panel for that ID; it live-updates during playback.",
            "EXAMPLE (INVENTED)",
            "ID 3B2:  B0 00 · B1 4C · B2 FF · B3 27 …\n" +
            "Scrub the timeline: B1 counts up with vehicle speed,\n" +
            "B3 flips 27 → A7 exactly when the door opens\n" +
            "→ bit 7 of B3 is the door switch.",
            "The tooltip on a changed byte lists exactly which bits flipped.",
            5),

        new TourStep(
            "STEP 7 · STATISTICS",
            "Check bus health in Statistics",
            "Per-ID frame counts, average rate, min/max period, jitter and DLC — sortable, so a message " +
            "that should be cyclic at 100 Hz but shows 7 Hz average stands out immediately.\n\n" +
            "\"Compare trace…\" loads a second file and compares frame counts, rates and decoded signal " +
            "ranges against it: ideal for before/after-fix recordings. The Trace view's \"Health…\" button " +
            "complements this with a chronological timeline of timing gaps and estimated missing frames.",
            "EXAMPLE (INVENTED)",
            "ID 0C4 EngineData   expected 100.0 Hz\n" +
            "  baseline.blf   → 100.1 Hz\n" +
            "  after_fix.blf  →  99.8 Hz    Status: OK\n" +
            "ID 3D2 ClimateCtrl  baseline 10 Hz → missing in compare ⚠",
            "Right-click a row to jump straight to that ID's first or last frame in the Trace view.",
            6),

        new TourStep(
            "STEP 8 · DID ↔ CAN MAPPING",
            "Correlate diagnostics to CAN in Mapping",
            "Load CDD/ODX files listing the ECU's DIDs, then press Auto-correlate: every DID is scored " +
            "against every DBC signal using names, units, ranges, scaling, bit lengths — and, when a " +
            "trace is open, actual observed values. Review each proposal, pick another candidate from the " +
            "drop-down, confirm ✓ or reject ✕, add notes, then save or export the map.\n\n" +
            "\"Remember this mapping\" teaches the tool an alias so the same DID name is proposed first " +
            "in every future project.",
            "EXAMPLE (INVENTED)",
            "DID 0x4A21 \"CoolantTemperature\" (°C, 0–150)\n" +
            "  → EngineData.EngCoolantTemp   confidence 92%\n" +
            "     same unit · same range · trace evidence:\n" +
            "     UDS readings 88–92 °C match the CAN signal",
            "Everything stays offline: the correlation uses only the files you load and the open trace.",
            7),

        new TourStep(
            "DATA STUDIO",
            "Build and save an analysis",
            "Data Studio guides you through Datasets, Analyses, Results and Compare. Load your " +
            "recordings, create analysis tabs and review results against their original sources.\n\n" +
            "In Analyses, Save analysis stores a local recipe and fingerprints of the source files. " +
            "Open saved checks those files before rebuilding the analysis; keep the originals available.",
            "EXAMPLE WORKFLOW",
            "1. Load a recording and its DBC\n" +
            "2. Create a Trace analysis tab and add notes\n" +
            "3. Save analysis → reopen it later from Analyses",
            "Saved analyses do not contain CAN frames or computed results. Changed sources require confirmation; missing sources prevent reopening.",
            MainViewModel.DataStudioNavIndex),

        new TourStep(
            "CUSTOMIZE THE WORKSPACE",
            "Fit the studio to your screen and workflow",
            "Drag any workspace button in the left rail to move it above or below the others. The visual " +
            "order is remembered, while saved sessions and shortcuts keep using stable workspace identities.\n\n" +
            "Session & tools is collapsed by default so Save/Load session, presets, reports, layouts, theme " +
            "and About do not consume height on a small display. Expand it only when needed. In Trace, drag " +
            "the horizontal splitter below the density overview to give the graph more or less height; that " +
            "height is also remembered.",
            "SMALL-SCREEN SETUP",
            "1. Drag Trace and UDS Analysis to the top\n" +
            "2. Collapse Session & tools\n" +
            "3. Reduce the Trace overview to ~100 px\n" +
            "4. Keep the raw frame table as the largest area",
            "These are local UI preferences; they do not modify trace, DBC, CDD/ODX or report data.",
            1),

        new TourStep(
            "FINISH",
            "Shortcuts & where to go next",
            "That's the whole studio. A few power features to remember:\n\n" +
            "•  Ctrl+K — search everywhere (IDs, messages, signals, DIDs)\n" +
            "•  Ctrl+F — focus the current view's filter box\n" +
            "•  Ctrl+1…8 — switch workspace  ·  Space — play/pause replay\n" +
            "•  M — mark the selected trace row  ·  F3 — find next\n" +
            "•  Ctrl+O — open trace  ·  Ctrl+Shift+O — load DBC  ·  Ctrl+E — export report\n\n" +
            "The theme selector (Dark, Light, High Contrast), session save/restore, named presets, " +
            "column layouts and the ZIP report export all live in the left rail.",
            null,
            null,
            "Enjoy! You can replay this tour any time via the \"Quick tour\" button in the left rail.",
            0)
    ];

    private void ShowStep(int index)
    {
        _stepIndex = Math.Clamp(index, 0, _steps.Count - 1);
        var step = _steps[_stepIndex];

        StepKicker.Text = step.Kicker;
        StepTitle.Text = step.Title;
        StepBody.Text = step.Body.Replace("&&", "&");

        var hasExample = !string.IsNullOrEmpty(step.Example);
        ExampleCard.Visibility = hasExample ? Visibility.Visible : Visibility.Collapsed;
        ExampleTitle.Text = step.ExampleTitle ?? string.Empty;
        ExampleBody.Text = step.Example ?? string.Empty;

        TipCard.Visibility = string.IsNullOrEmpty(step.Tip) ? Visibility.Collapsed : Visibility.Visible;
        TipBody.Text = step.Tip ?? string.Empty;

        BackButton.IsEnabled = _stepIndex > 0;
        var isLast = _stepIndex == _steps.Count - 1;
        NextButton.Content = isLast ? "Finish ✓" : "Next ▶";
        SkipButton.Visibility = isLast ? Visibility.Hidden : Visibility.Visible;
        LiveHint.Visibility = step.NavIndex is > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Switch the real app behind the tour to the workspace being described.
        if (step.NavIndex is { } navIndex && _viewModel.SelectedNavIndex != navIndex)
        {
            _viewModel.SelectedNavIndex = navIndex;
        }
        if (step.UdsViewIndex is { } udsViewIndex)
        {
            _viewModel.Uds.SelectedViewIndex = udsViewIndex;
        }

        RebuildDots();
    }

    private void RebuildDots()
    {
        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
        var idle = TryFindResource("BorderBrush") as Brush ?? Brushes.Gray;
        var dots = new List<UIElement>(_steps.Count);
        for (var index = 0; index < _steps.Count; index++)
        {
            dots.Add(new Ellipse
            {
                Width = index == _stepIndex ? 10 : 7,
                Height = index == _stepIndex ? 10 : 7,
                Margin = new Thickness(3, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = index <= _stepIndex ? accent : idle,
                ToolTip = _steps[index].Title
            });
        }

        StepDots.ItemsSource = dots;
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => ShowStep(_stepIndex - 1);

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_stepIndex == _steps.Count - 1)
        {
            Close();
            return;
        }

        ShowStep(_stepIndex + 1);
    }

    private void OnSkipClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Right or Key.Enter:
                OnNextClick(sender, e);
                e.Handled = true;
                break;
            case Key.Left:
                ShowStep(_stepIndex - 1);
                e.Handled = true;
                break;
        }
    }
}
