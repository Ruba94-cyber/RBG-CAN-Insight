using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RbgCanInsight.App;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.ViewModels;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        // `dotnet run --project tools\RbgCanInsight.GuiVerify -- --benchmark [frameCount]` runs the
        // repeatable performance harness (large-trace scroll/filter/playback/import) instead of the
        // default screenshot/assertion GUI-verification pass, so a change's before/after numbers can
        // be captured with the same tool and the same off-screen-window technique.
        if (args.Length > 0 && string.Equals(args[0], "--benchmark", StringComparison.OrdinalIgnoreCase))
        {
            return BenchmarkRunner.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && string.Equals(args[0], "--record-demo", StringComparison.OrdinalIgnoreCase))
        {
            return DemoVideoRunner.Run(args.Skip(1).ToArray());
        }

        var root = FindRepositoryRoot();
        var artifacts = Path.Combine(root, "artifacts", "gui-verification");
        Directory.CreateDirectory(artifacts);

        var tracePath = Path.Combine(artifacts, "gui-fixture.asc");
        var dbcPath = Path.Combine(artifacts, "gui-fixture.dbc");
        var odxPath = Path.Combine(artifacts, "gui-fixture.odx");
        File.WriteAllText(tracePath, TraceFixture);
        File.WriteAllText(dbcPath, DbcFixture);
        File.WriteAllText(odxPath, OdxFixture);

        // Reset the first-launch flag so the onboarding tour is exercised on every run.
        var appSettingsPath = Path.Combine(AppStorage.ConfigDirectory, "app-settings.json");
        if (File.Exists(appSettingsPath))
        {
            File.Delete(appSettingsPath);
        }

        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/RbgCanInsight;component/Themes/Palette.Dark.xaml")
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/RbgCanInsight;component/Themes/Styles.xaml")
        });

        var window = new MainWindow
        {
            Width = 1440,
            Height = 900,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false
        };
        window.Show();
        DrainUi();

        var model = (MainViewModel)window.DataContext;

        // First launch: the interactive tour must open on its own, be steppable (switching the
        // main window to the workspace each step describes), and persist "shown" once closed.
        var tour = window.OwnedWindows.OfType<OnboardingWindow>().FirstOrDefault();
        var onboardingOpened = tour is not null;
        var onboardingNavigated = false;
        var onboardingExplainsOfflineUds = false;
        if (tour is not null)
        {
            tour.Left = -32000;
            tour.Top = -32000;
            ClickButton(tour, "Next ▶");
            DrainUi();
            ClickButton(tour, "Next ▶");
            DrainUi();
            Capture(tour, Path.Combine(artifacts, "onboarding-tour.png"));
            // Step 3 ("STEP 2 · TRACE") drives the main window to the Trace workspace.
            onboardingNavigated = model.SelectedNavIndex == 1;
            ClickButton(tour, "Next ▶");
            DrainUi();
            ClickButton(tour, "Next ▶");
            DrainUi();
            var tourBody = (TextBlock)tour.FindName("StepBody");
            var tourTip = (TextBlock)tour.FindName("TipBody");
            onboardingExplainsOfflineUds = model.SelectedNavIndex == 8 &&
                                           model.Uds.SelectedViewIndex == 0 &&
                                           tourBody.Text.Contains("read-only", StringComparison.OrdinalIgnoreCase) &&
                                           tourTip.Text.Contains("No Send", StringComparison.OrdinalIgnoreCase);
            Capture(tour, Path.Combine(artifacts, "onboarding-uds-analysis.png"));
            tour.Close();
            DrainUi();
        }

        var onboardingFlagSaved = AppSettingsStore.LoadOnboardingShown();
        model.SelectedNavIndex = 0;
        DrainUi();
        PumpUntil(model.LoadTraceAsync(tracePath));
        var smallTraceAvoidsMillionFramePreallocation =
            model.Frames is List<RbgCanInsight.Domain.CanFrame> loadedFrames &&
            loadedFrames.Capacity < 1 << 20;
        PumpUntil(model.LoadDbcAsync([dbcPath]));
        PumpUntil(model.Mapping.LoadCatalogsAsync([odxPath]));
        DrainUi();

        // The DBC comparison must not mutate the active decoder or source.
        // This synthetic fixture changes only the EngineLoad physical factor.
        var changedDbcPath = Path.Combine(artifacts, "gui-changed-network.dbc");
        File.WriteAllText(changedDbcPath,
            DbcFixture.Replace("(0.5,0)", "(0.75,0)", StringComparison.Ordinal));
        var activeDbcCount = model.Databases.Count;
        PumpUntil(model.CompareDbcFilesAsync(dbcPath, changedDbcPath));
        var compareRow = model.DbcComparisonRows.FirstOrDefault(row =>
            row.Scope == "Signal" && row.Signal == "EngineLoad");
        var dbcComparisonDataWorks =
            model.HasDbcComparison &&
            compareRow is
            {
                Change: RbgCanInsight.Dbc.DbcDefinitionChange.Changed,
                Impact: RbgCanInsight.Dbc.DbcDefinitionImpact.Decoding,
                RequiresReview: true
            } &&
            compareRow.Differences.Contains("Factor", StringComparison.Ordinal) &&
            model.Databases.Count == activeDbcCount &&
            File.ReadAllText(dbcPath) == DbcFixture;

        var parsedBefore = new RbgCanInsight.Dbc.DbcParser().ParseFile(dbcPath);
        var parsedAfter = new RbgCanInsight.Dbc.DbcParser().ParseFile(changedDbcPath);
        var dbcDiffReport = RbgCanInsight.Dbc.DbcDefinitionComparer.Compare(parsedBefore, parsedAfter);
        var dbcDiffSheets = RbgCanInsight.TraceIO.DbcDefinitionComparisonExport.BuildSheets(
            parsedBefore, parsedAfter, dbcDiffReport, dbcPath, changedDbcPath);
        var dbcDiffXlsx = Path.Combine(artifacts, "gui-dbc-comparison.xlsx");
        var dbcDiffZip = Path.Combine(artifacts, "gui-dbc-comparison.zip");
        RbgCanInsight.TraceIO.DbcDefinitionComparisonExport.Write(
            dbcDiffXlsx, dbcDiffSheets, csvZip: false);
        RbgCanInsight.TraceIO.DbcDefinitionComparisonExport.Write(
            dbcDiffZip, dbcDiffSheets, csvZip: true);
        using var dbcZip = ZipFile.OpenRead(dbcDiffZip);
        var dbcComparisonExportWorks = dbcDiffSheets.Count == 3 &&
            dbcDiffSheets[1].Headers.Contains("Changed fields") &&
            dbcZip.Entries.Count == 3 &&
            new FileInfo(dbcDiffXlsx).Length > 0;
        model.SelectedNavIndex = 2;
        DrainUi();
        var dbcDiffExpander = FindVisualChild<Expander>(window, "DbcDefinitionDiffExpander");
        if (dbcDiffExpander is not null)
        {
            dbcDiffExpander.IsExpanded = true;
            DrainUi();
        }
        var defaultChangedOnly = model.DbcComparisonChangesOnly &&
            model.DbcComparisonRows.All(row =>
                row.Change != RbgCanInsight.Dbc.DbcDefinitionChange.Unchanged);
        model.DbcComparisonChangesOnly = false;
        var unfilteredAllRows = model.DbcComparisonRows.Count == dbcDiffReport.Rows.Count;
        var revealsUnchanged = model.DbcComparisonRows.Any(row =>
            row.Change == RbgCanInsight.Dbc.DbcDefinitionChange.Unchanged);
        model.DbcComparisonChangesOnly = true;
        var dbcComparisonUiWorks = dbcComparisonDataWorks &&
            dbcComparisonExportWorks && defaultChangedOnly &&
            unfilteredAllRows && revealsUnchanged &&
            dbcDiffExpander?.IsExpanded == true &&
            FindVisualChild<DataGrid>(window, "DbcDefinitionDiffGrid") is not null;
        Capture(window, Path.Combine(artifacts, "dbc-definition-comparison.png"));

        model.SelectedDbcComparisonDiffRow = compareRow;
        // A stale/pending debounced DBC search must never hide an explicit
        // jump to an otherwise uniquely resolved exact CAN source definition.
        model.DbcCatalogSearchText = "no-such-signal-in-this-dbc";
        var baselineDbcNavPassed = compareRow is not null &&
            model.InspectDbcDiffInLoadedDatabase(currentVersion: false) &&
            model.SelectedDbcMessage is { Name: "EngineData" } &&
            model.SelectedDbcSignal is { Signal: "EngineLoad" } &&
            model.DbcCatalogSearchText == string.Empty &&
            model.SelectedNavIndex == 2;
        var selectedDbcBeforeRejectedNav = model.SelectedDbcSignal;
        var unloadedDbcNavRejected = compareRow is not null &&
            !model.InspectDbcDiffInLoadedDatabase(currentVersion: true) &&
            ReferenceEquals(model.SelectedDbcSignal, selectedDbcBeforeRejectedNav) &&
            model.StatusText.Contains("not loaded", StringComparison.OrdinalIgnoreCase);
        var dbcDiffNavigationUiPassed = baselineDbcNavPassed && unloadedDbcNavRejected &&
            FindButton(window, "Inspect baseline") is not null &&
            FindButton(window, "Inspect current") is not null;
        Capture(window, Path.Combine(artifacts, "dbc-diff-inspect-baseline.png"));
        model.SelectedNavIndex = 0;
        DrainUi();

        // Persist/reload a reviewed synthetic mapping on disk, then deliberately
        // invalidate its DBC fingerprint. This is a GUI state regression, not
        // a claim that the synthetic DID/CAN relationship is OEM-verified.
        var savedReviewRestoreWorks = false;
        var savedMapComparisonUiPassed = false;
        var reviewDid = model.Mapping.Rows.FirstOrDefault()?.Did;
        var reviewDb = model.Databases.FirstOrDefault();
        var reviewMessage = reviewDb?.AllMessageDefinitions.FirstOrDefault(message =>
            message.Signals.Count > 0);
        var reviewSignal = reviewMessage?.Signals.FirstOrDefault();
        if (reviewDid is not null && reviewDb is not null &&
            reviewMessage is not null && reviewSignal is not null)
        {
            var mappingPath = Path.Combine(artifacts, "gui-mapping-review.rbgmap.json");
            var mapping = new RbgCanInsight.DiagData.DidSignalMapping(
                reviewDid.Did, reviewDid.Name, reviewDid.Ecu,
                reviewDb.Name, reviewMessage.Name, reviewSignal.Name,
                0.91, "synthetic GUI review", RbgCanInsight.DiagData.MappingStatus.Confirmed,
                "This note must survive a definition mismatch",
                MessageIdentifier: reviewMessage.Identifier,
                IsExtended: reviewMessage.IsExtended,
                DidDefinitionFingerprint:
                    RbgCanInsight.Correlation.MappingDefinitionFingerprint.ForDid(reviewDid),
                CanSignalFingerprint:
                    RbgCanInsight.Correlation.MappingDefinitionFingerprint.ForSignal(
                        reviewDb.Name, reviewMessage, reviewSignal));
            new RbgCanInsight.DiagData.MappingDocument("GUI fixture",
                DateTimeOffset.UtcNow, [mapping]).Save(mappingPath);
            PumpUntil(model.Mapping.LoadMapFileAsync(mappingPath));
            var mappingRow = model.Mapping.Rows.FirstOrDefault(row =>
                ReferenceEquals(row.Did, reviewDid));
            var confirmedRestored = mappingRow?.Status ==
                    RbgCanInsight.DiagData.MappingStatus.Confirmed &&
                string.IsNullOrEmpty(mappingRow.ReviewWarning);

            new RbgCanInsight.DiagData.MappingDocument("GUI stale fixture",
                DateTimeOffset.UtcNow,
                [mapping with { CanSignalFingerprint = "sem-v1:" + new string('F', 64) }])
                .Save(mappingPath);
            PumpUntil(model.Mapping.LoadMapFileAsync(mappingPath));
            savedReviewRestoreWorks = confirmedRestored &&
                mappingRow is { Status: RbgCanInsight.DiagData.MappingStatus.ToVerify } &&
                mappingRow.ReviewWarning.Contains("DBC signal definition changed",
                    StringComparison.Ordinal) &&
                mappingRow.Notes == "This note must survive a definition mismatch";

            var originalMapFile = Path.Combine(artifacts, "gui-mapping-baseline.rbgmap.json");
            var originalMap = new RbgCanInsight.DiagData.MappingDocument(
                "GUI baseline", DateTimeOffset.UtcNow, [mapping]);
            var changedMap = new RbgCanInsight.DiagData.MappingDocument(
                "GUI changed", DateTimeOffset.UtcNow,
                [mapping with { CanSignalFingerprint = "sem-v1:" + new string('F', 64) }]);
            originalMap.Save(originalMapFile);
            PumpUntil(model.Mapping.CompareMapFilesAsync(originalMapFile, mappingPath));
            var diffRow = model.Mapping.ComparisonRows.Count == 1
                ? model.Mapping.ComparisonRows[0] : null;
            var comparisonModelValid = model.Mapping.HasComparison &&
                diffRow is
                {
                    Kind: RbgCanInsight.DiagData.MappingDiffKind.Changed,
                    RequiresReview: true,
                    CanDefinition: RbgCanInsight.DiagData.MappingDefinitionEvidence.Changed
                } &&
                model.Mapping.ComparisonSummary.Contains("1", StringComparison.Ordinal);

            var sheets = RbgCanInsight.TraceIO.MappingComparisonExport.BuildSheets(
                originalMap, changedMap,
                RbgCanInsight.DiagData.MappingDocumentComparer.Compare(originalMap, changedMap),
                originalMapFile, mappingPath);
            var comparisonXlsx = Path.Combine(artifacts, "gui-mapping-comparison.xlsx");
            var comparisonZip = Path.Combine(artifacts, "gui-mapping-comparison.zip");
            if (File.Exists(comparisonXlsx)) File.Delete(comparisonXlsx);
            if (File.Exists(comparisonZip)) File.Delete(comparisonZip);
            RbgCanInsight.TraceIO.StructuredWorkbookExport.Write(comparisonXlsx, sheets);
            RbgCanInsight.TraceIO.StructuredCsvArchiveExport.Write(comparisonZip, sheets);
            using var mappingArchive = ZipFile.OpenRead(comparisonZip);
            var comparisonExportValid = File.Exists(comparisonXlsx) &&
                sheets.Count == 2 &&
                sheets[1].Headers.Contains("Needs review") &&
                sheets[1].Headers.Contains("Current CAN fingerprint") &&
                mappingArchive.Entries.Count == 2;
            model.SelectedNavIndex = 7;
            DrainUi();
            var mapDiffExpander = FindVisualChild<Expander>(window, "SavedMappingDiffExpander");
            if (mapDiffExpander is not null)
            {
                mapDiffExpander.IsExpanded = true;
                DrainUi();
            }
            var diffGridVisible = FindVisualChild<DataGrid>(window, "SavedMappingDiffGrid") is not null;
            savedMapComparisonUiPassed =
                comparisonModelValid && comparisonExportValid &&
                mapDiffExpander?.IsExpanded == true && diffGridVisible;
            model.SelectedNavIndex = 0;
            DrainUi();
        }

        var importHealthIsComplete = model.ImportHealth is { Completed: true, RejectedRecords: 0 } &&
            model.ImportHealth.Frames == model.FrameCount &&
            model.ImportHealth.MetadataLines >= 3;
        model.SelectedNavIndex = 1;
        DrainUi();
        var importToggle = FindVisualChild<ToggleButton>(window, "TraceImportHealthToggle");
        if (importToggle is not null)
            importToggle.IsChecked = true;
        DrainUi();
        var importHealthUiWorks = importHealthIsComplete &&
            FindVisualChild<DataGrid>(window, "TraceImportChannelsGrid") is not null &&
            FindVisualChild<DataGrid>(window, "TraceImportIssuesGrid") is not null &&
            model.ImportChannelRows.Count > 0;
        Capture(window, Path.Combine(artifacts, "trace-import-quality.png"));
        if (importToggle is not null)
            importToggle.IsChecked = false;

        model.SelectedNavIndex = 0;
        DrainUi();
        Capture(window, Path.Combine(artifacts, "store-dashboard.png"));
        model.StartInvestigationCommand.Execute("uds");
        var dashboardUdsWorkflowWorks = model.SelectedNavIndex == 8 &&
            model.Uds.SelectedViewIndex == 1 && model.Uds.FilterIndex == 1;
        model.SelectedNavIndex = 0;

        model.SelectedNavIndex = 2;
        DrainUi();
        Capture(window, Path.Combine(artifacts, "store-dbc-manager.png"));

        // CANdb-like inspector must show the actual bit layout and a source-backed
        // physical observation, not a fabricated value from signal metadata.
        model.SelectedDbcSignal = model.DbcSignals.FirstOrDefault(row =>
            row.Signal == "EngCoolantTemp");
        DrainUi();
        var dbcInspectorWorks = model.DbcInspector.HasSelection &&
                                model.DbcInspector.BitRows.Count == 8 &&
                                model.DbcInspector.BitRows[0].Cells.Count(cell => cell.InSelectedSignal) == 8 &&
                                model.DbcInspector.BoundsText.Contains("8 bits", StringComparison.Ordinal) &&
                                model.DbcInspector.SourceFrameIndex >= 0 &&
                                model.DbcInspector.ExampleText.Contains("raw", StringComparison.OrdinalIgnoreCase) &&
                                model.DbcInspector.BitRows[0].Cells.All(cell => cell.RecordedValue is not null) &&
                                model.DbcInspector.CodedRangeText.Contains("255", StringComparison.Ordinal) &&
                                model.DbcInspector.SourceAttributes.Any(attribute =>
                                    attribute.Name == "GenSigStartValue");
        var dbcInspectorControlsVisible = FindVisualChild<DataGrid>(window, "DbcSignalsGrid") is not null &&
                                          FindButton(window, "Plot signal") is not null;
        Capture(window, Path.Combine(artifacts, "dbc-bit-inspector.png"));
        // New engineering editor: inspect/stage a copy without touching active decode,
        // visually verify the tab, save a new DBC, reparse, then revert the staged draft.
        var dbcOriginalBeforeEditor = File.ReadAllText(dbcPath);
        var editorTabs = FindVisualChildren<TabItem>(window).ToArray();
        var engineeringTab = editorTabs.FirstOrDefault(tab =>
            string.Equals(tab.Header?.ToString(), "Edit DBC (working copy)", StringComparison.Ordinal));
        var bitLayoutTab = editorTabs.FirstOrDefault(tab =>
            string.Equals(tab.Header?.ToString(), "Bit layout", StringComparison.Ordinal));
        if (engineeringTab is not null) engineeringTab.IsSelected = true;
        DrainUi();
        var dbcEditControlsVisible = engineeringTab?.IsSelected == true &&
            FindButton(window, "Stage edit") is not null &&
            FindButton(window, "Save DBC copy…") is not null &&
            FindButton(window, "Discard all") is not null;
        var originalCoolantSignal = model.Databases
            .SelectMany(database => database.AllMessageDefinitions)
            .SelectMany(message => message.Signals)
            .Single(signal => signal.Name == "EngCoolantTemp");
        model.DbcEditor.FactorText = "2";
        model.DbcEditor.UnitText = "degK";
        model.DbcEditor.StageCommand.Execute(null);
        DrainUi();
        var dbcEditStageWorks = model.DbcEditor.CanSave &&
            model.DbcEditor.ChangeRows.Count == 1 &&
            model.DbcEditor.ChangeRows[0].Signal == "EngCoolantTemp" &&
            model.DbcEditor.ChangeRows[0].Differences.Contains("factor", StringComparison.Ordinal) &&
            originalCoolantSignal.Factor == 1 &&
            File.ReadAllText(dbcPath) == dbcOriginalBeforeEditor;
        Capture(window, Path.Combine(artifacts, "dbc-working-copy-editor.png"));
        var editedCopyPath = Path.Combine(artifacts, "gui-edited-copy.dbc");
        if (File.Exists(editedCopyPath)) File.Delete(editedCopyPath);
        var dbcEditSaveWorks = false;
        try
        {
            model.DbcEditor.SaveToPath(editedCopyPath);
            var parsedCopy = new RbgCanInsight.Dbc.DbcParser().ParseFile(editedCopyPath);
            dbcEditSaveWorks = parsedCopy.TryGetMessage(256, false, out var editedMessage) &&
                editedMessage.Signals.Single(signal => signal.Name == "EngCoolantTemp").Factor == 2 &&
                editedMessage.Signals.Single(signal => signal.Name == "EngCoolantTemp").Unit == "degK" &&
                File.ReadAllText(dbcPath) == dbcOriginalBeforeEditor;
        }
        catch (Exception)
        {
            dbcEditSaveWorks = false;
        }
        model.DbcEditor.RevertSignalCommand.Execute(null);
        var dbcEditRevertWorks = !model.DbcEditor.CanSave &&
            model.DbcEditor.ChangeRows.Count == 0 &&
            File.ReadAllText(dbcPath) == dbcOriginalBeforeEditor;
        // Value-table engineering: edit a declared VAL_ meaning, add a raw
        // code, reject a duplicate, Save As and check the original still decodes.
        model.SelectedDbcSignal = model.DbcSignals.FirstOrDefault(row =>
            row.Signal == "MissingMuxBranch");
        DrainUi();
        var dbcValueEditorGridVisible =
            FindVisualChild<DataGrid>(window, "DbcNamedValuesEditorGrid") is not null &&
            FindButton(window, "Stage VAL_ values") is not null &&
            model.DbcEditor.NamedValueRows.Count == 2;
        model.DbcEditor.NamedValueRows.Single(row => row.RawCode == "1").Meaning = "Enabled & ready";
        model.DbcEditor.AddNamedValueCommand.Execute(null);
        var newValueRow = model.DbcEditor.NamedValueRows.Last();
        newValueRow.RawCode = "1";
        newValueRow.Meaning = "Duplicate";
        model.DbcEditor.StageNamedValuesCommand.Execute(null);
        var duplicateRawCodeRejected =
            model.DbcEditor.StatusText.Contains("Duplicate raw code", StringComparison.Ordinal) &&
            !model.DbcEditor.CanSave;
        newValueRow.RawCode = "2";
        newValueRow.Meaning = "Fault";
        model.DbcEditor.StageNamedValuesCommand.Execute(null);
        DrainUi();
        var dbcValueStageWorks = model.DbcEditor.CanSave &&
            model.DbcEditor.ChangeRows.Count == 1 &&
            model.DbcEditor.ChangeRows[0].Differences.Contains("VAL_", StringComparison.Ordinal) &&
            File.ReadAllText(dbcPath) == dbcOriginalBeforeEditor;
        Capture(window, Path.Combine(artifacts, "dbc-value-table-editor.png"));
        var valueEditedPath = Path.Combine(artifacts, "gui-edited-values.dbc");
        if (File.Exists(valueEditedPath)) File.Delete(valueEditedPath);
        var dbcValueSaveWorks = false;
        try
        {
            model.DbcEditor.SaveToPath(valueEditedPath);
            var valuesDb = new RbgCanInsight.Dbc.DbcParser().ParseFile(valueEditedPath);
            dbcValueSaveWorks = valuesDb.TryGetMessage(256, false, out var valuesMessage) &&
                valuesMessage.Signals.Single(signal => signal.Name == "MissingMuxBranch")
                    .ValueDescriptions is { } values &&
                values.TryGetValue(1, out var ready) && ready == "Enabled & ready" &&
                values.TryGetValue(2, out var fault) && fault == "Fault" &&
                File.ReadAllText(dbcPath) == dbcOriginalBeforeEditor;
        }
        catch (Exception) { dbcValueSaveWorks = false; }
        model.DbcEditor.RevertSignalCommand.Execute(null);
        var dbcValueRevertWorks = !model.DbcEditor.CanSave &&
            model.DbcEditor.NamedValueRows.Count == 2;
        model.SelectedDbcSignal = model.DbcSignals.FirstOrDefault(row =>
            row.Signal == "EngCoolantTemp");
        if (bitLayoutTab is not null) bitLayoutTab.IsSelected = true;
        DrainUi();

        // Activate the actual diagnostic catalog before checking its WPF
        // controls. A collapsed nonselected tab is not required to have
        // instantiated its nested DataGrid in WPF's visual tree.
        var navBeforeDiagnosticExportCheck = model.SelectedNavIndex;
        var udsBeforeDiagnosticExportCheck = model.Uds.SelectedViewIndex;
        var inspectorBeforeDiagnosticExportCheck = model.CatalogExplorer.InspectorTabIndex;
        model.SelectedNavIndex = 8;
        model.Uds.SelectedViewIndex = 11;
        model.CatalogExplorer.InspectorTabIndex = 1;
        DrainUi();
        var engineeringInspectorUiVisible =
            FindVisualChild<Button>(window, "ExportEngineeringParametersButton") is not null &&
            FindVisualChild<DataGrid>(window, "DiagnosticEngineeringParametersGrid") is not null &&
            FindVisualChild<TextBox>(window, "DiagnosticEngineeringParametersFilter") is not null &&
            FindVisualChild<ComboBox>(window, "DiagnosticEcuFilter") is not null &&
            FindVisualChild<DataGrid>(window, "EngineeringValueMeaningsGrid") is not null &&
            FindVisualChild<ScrollViewer>(window, "DiagnosticInspectorDetailsScroller") is not null;

        var selectedEcuBeforeCheck = model.CatalogExplorer.SelectedEcuFilter;
        var selectedEcuForCheck = model.CatalogExplorer.EcuFilters
            .FirstOrDefault(ecu => ecu != "All ECUs");
        var engineeringEcuFilterWorks = true;
        if (selectedEcuForCheck is not null)
        {
            model.CatalogExplorer.SelectedEcuFilter = selectedEcuForCheck;
            var scopedSheets = RbgCanInsight.TraceIO.CatalogDefinitionExport.CreateDiagnosticSheets(
                model.Mapping.Catalogs, engineeringOnly: true,
                ecuFilter: model.CatalogExplorer.SelectedEcuForExport);
            engineeringEcuFilterWorks =
                model.CatalogExplorer.Rows.All(row =>
                    string.Equals(row.Ecu, selectedEcuForCheck,
                        StringComparison.OrdinalIgnoreCase)) &&
                scopedSheets.Single(sheet => sheet.Name == "Engineering Parameters")
                    .Rows.All(row => string.Equals(row[1], selectedEcuForCheck,
                        StringComparison.OrdinalIgnoreCase)) &&
                scopedSheets.Single(sheet => sheet.Name == "PAR Extraction Coverage")
                    .Rows.All(row => string.Equals(row[1], selectedEcuForCheck,
                        StringComparison.OrdinalIgnoreCase));
            model.CatalogExplorer.SelectedEcuFilter = selectedEcuBeforeCheck;
        }

        // The local inspector filter changes only the selected DID's UI rows;
        // it must not mutate the underlying catalog or the full export.
        var engineeringInspectorFilterWorks = true;
        var selectedBeforeEngineeringFilter = model.CatalogExplorer.SelectedDefinition;
        var filterBeforeEngineeringCheck = model.CatalogExplorer.EngineeringFilterText;
        var candidateForEngineeringFilter = model.CatalogExplorer.Rows.FirstOrDefault(row =>
            row.Kind == "DID" && row.EngineeringRows.Count > 0);
        if (candidateForEngineeringFilter is not null)
        {
            model.CatalogExplorer.SelectedDefinition = candidateForEngineeringFilter;
            var allEngineeringCount = candidateForEngineeringFilter.EngineeringRows.Count;
            var targetPar = candidateForEngineeringFilter.EngineeringRows[0].Parameter.Name;
            model.CatalogExplorer.EngineeringFilterText = targetPar;
            engineeringInspectorFilterWorks =
                model.CatalogExplorer.EngineeringRows.Count > 0 &&
                model.CatalogExplorer.EngineeringRows.Count <= allEngineeringCount &&
                model.CatalogExplorer.EngineeringRows.Contains(
                    candidateForEngineeringFilter.EngineeringRows[0]) &&
                candidateForEngineeringFilter.EngineeringRows.Count == allEngineeringCount;
            model.CatalogExplorer.EngineeringFilterText = filterBeforeEngineeringCheck;
            model.CatalogExplorer.SelectedDefinition = selectedBeforeEngineeringFilter;
        }

        // Independent catalog exports are definitions, not sample values: no trace
        // dependency, exact CAN identity, separate DIDs/fields and re-openable ZIP parts.
        var dbcDefinitionWorkbookPath = Path.Combine(artifacts, "gui-dbc-definitions.xlsx");
        var diagDefinitionWorkbookPath = Path.Combine(artifacts, "gui-diagnostic-definitions.xlsx");
        var engineeringWorkbookPath = Path.Combine(artifacts, "gui-engineering-parameters.xlsx");
        var dbcSheets = RbgCanInsight.TraceIO.CatalogDefinitionExport.CreateDbcSheets(model.Databases);
        var diagSheets = RbgCanInsight.TraceIO.CatalogDefinitionExport.CreateDiagnosticSheets(model.Mapping.Catalogs);
        var engineeringSheets = RbgCanInsight.TraceIO.CatalogDefinitionExport.CreateDiagnosticSheets(
            model.Mapping.Catalogs, engineeringOnly: true);
        if (File.Exists(dbcDefinitionWorkbookPath)) File.Delete(dbcDefinitionWorkbookPath);
        if (File.Exists(diagDefinitionWorkbookPath)) File.Delete(diagDefinitionWorkbookPath);
        if (File.Exists(engineeringWorkbookPath)) File.Delete(engineeringWorkbookPath);
        RbgCanInsight.TraceIO.StructuredWorkbookExport.Write(dbcDefinitionWorkbookPath, dbcSheets);
        RbgCanInsight.TraceIO.StructuredWorkbookExport.Write(diagDefinitionWorkbookPath, diagSheets);
        RbgCanInsight.TraceIO.StructuredWorkbookExport.Write(engineeringWorkbookPath, engineeringSheets);
        var definitionExportsWork = dbcSheets.Single(sheet => sheet.Name == "Signals").Rows.Count >= 4 &&
            diagSheets.Single(sheet => sheet.Name == "DIDs").Rows.Count > 0 &&
            diagSheets.Single(sheet => sheet.Name == "DID Fields").Rows.Count > 0 &&
            diagSheets.Single(sheet => sheet.Name == "Engineering Parameters").Rows.Count > 0 &&
            diagSheets.Single(sheet => sheet.Name == "Engineering Parameters")
                .Headers.Contains("Definition origin") &&
            diagSheets.Single(sheet => sheet.Name == "Engineering PAR Values")
                .Headers.Contains("Boundary status") &&
            diagSheets.Single(sheet => sheet.Name == "PAR Extraction Coverage").Rows.Count > 0 &&
            diagSheets.Single(sheet => sheet.Name == "PAR Extraction Coverage")
                .Headers.Contains("Definition issues") &&
            model.CatalogExplorer.Rows.Where(row => row.Kind == "DID")
                .All(row => row.EngineeringParameterCount == row.EngineeringRows.Count) &&
            engineeringSheets.Count == 3 &&
            engineeringSheets[0].Name == "Engineering Parameters" &&
            engineeringSheets[0].Rows.Count > 0 &&
            engineeringSheets[1].Name == "PAR Extraction Coverage" &&
            engineeringSheets[1].Rows.Count > 0 &&
            engineeringSheets[2].Name == "Engineering PAR Values" &&
            engineeringInspectorUiVisible && engineeringInspectorFilterWorks &&
            engineeringEcuFilterWorks &&
            diagSheets.Single(sheet => sheet.Name == "PAR Extraction Coverage")
                .Headers.Contains("Variable-length definitions") &&
            diagSheets.Single(sheet => sheet.Name == "PAR Extraction Coverage")
                .Headers.Contains("Unverifiable value boundaries") &&
            diagSheets.Single(sheet => sheet.Name == "Engineering Parameters")
                .Headers.Contains("Declared PAR code") &&
            diagSheets.Single(sheet => sheet.Name == "Engineering Parameters")
                .Headers.Contains("Source byte position (unverified)") &&
            diagSheets.Single(sheet => sheet.Name == "Engineering Parameters")
                .Headers.Contains("Source bit position (unverified)") &&
            diagSheets.Single(sheet => sheet.Name == "Engineering Parameters")
                .Headers.Contains("Source bit offset (unverified)");
        using (var dbcWorkbookZip = ZipFile.OpenRead(dbcDefinitionWorkbookPath))
            definitionExportsWork &= dbcWorkbookZip.GetEntry("xl/workbook.xml") is not null &&
                dbcWorkbookZip.GetEntry("xl/worksheets/sheet1.xml") is not null;
        using (var diagWorkbookZip = ZipFile.OpenRead(diagDefinitionWorkbookPath))
            definitionExportsWork &= diagWorkbookZip.GetEntry("xl/workbook.xml") is not null &&
                diagWorkbookZip.GetEntry("xl/worksheets/sheet4.xml") is not null &&
                diagWorkbookZip.GetEntry("xl/worksheets/sheet5.xml") is not null;
        using (var engineeringZip = ZipFile.OpenRead(engineeringWorkbookPath))
            definitionExportsWork &= engineeringZip.GetEntry("xl/workbook.xml") is not null &&
                engineeringZip.GetEntry("xl/worksheets/sheet1.xml") is not null &&
                engineeringZip.GetEntry("xl/worksheets/sheet2.xml") is not null &&
                engineeringZip.GetEntry("xl/worksheets/sheet3.xml") is not null;

        model.CatalogExplorer.InspectorTabIndex = inspectorBeforeDiagnosticExportCheck;
        model.Uds.SelectedViewIndex = udsBeforeDiagnosticExportCheck;
        model.SelectedNavIndex = navBeforeDiagnosticExportCheck;
        DrainUi();

        model.OpenSelectedDbcSignalFrameCommand.Execute(null);
        var dbcSourceFrameNavigationWorks = model.SelectedNavIndex == 1 &&
            model.Trace.SelectedRow?.FrameIndex == model.DbcInspector.SourceFrameIndex;
        model.Trace.TailCommand.Execute(null); // Restore full replay window for later GUI checks.
        model.Trace.SelectedRow = model.Trace.Rows.First(row => row.IdentifierHex == "100");
        var originatingFrame = model.Trace.SelectedRow.FrameIndex;
        var rawBit7 = (model.Trace.SelectedRow.Frame.GetByte(0) >> 7) & 1;
        model.InspectSelectedTraceDbcCommand.Execute(null);
        var traceToDbcNavigationWorks = model.SelectedNavIndex == 2 &&
            model.SelectedDbcMessage?.Name == "EngineData" &&
            model.SelectedDbcSignal is not null && model.DbcInspector.HasSelection &&
            model.DbcInspector.SourceFrameIndex == originatingFrame &&
            model.DbcInspector.BitRows[0].Cells.Single(cell => cell.BitNumber == 7)
                .RecordedValue == rawBit7;
        // DBC nominal timing is source-backed and separate from the empirical
        // average-period anomaly detector. Keep the no-CI Windows visual gate.
        model.SelectedNavIndex = 6;
        DrainUi();
        var nominalCycleAnalysisWorks = model.CycleTiming.Rows.Any(row =>
            row.Identifier == "0x100" && row.NominalMs == 100 &&
            row.Frames > 3 && row.LateGaps > 0) &&
            model.CycleTiming.Incidents.Any(incident =>
                incident.Model.ObservedGapMilliseconds > 1000);
        var nominalCycleUiVisible =
            FindVisualChild<DataGrid>(window, "DbcCycleTimingGrid") is not null &&
            FindVisualChild<DataGrid>(window, "DbcCycleIncidentsGrid") is not null;
        Capture(window, Path.Combine(artifacts, "dbc-nominal-cycle-evidence.png"));
        model.CycleTiming.SelectedIncident = model.CycleTiming.Incidents.FirstOrDefault();
        var selectedGapIndex = model.CycleTiming.SelectedIncident?.Model.FrameIndex ?? -1;
        model.CycleTiming.OpenIncidentCommand.Execute(null);
        var nominalCycleTraceLinkWorks = selectedGapIndex >= 0 &&
            model.SelectedNavIndex == 1 &&
            model.Trace.SelectedRow?.FrameIndex == selectedGapIndex;
        model.Trace.TailCommand.Execute(null);
        model.SelectedNavIndex = 2;
        DrainUi();

        // The fixture's 0x7E0/0x7E8 frames must be read as a UDS RDBI request/response pair.
        var udsDecoded =
            model.Trace.Rows.Any(row => row.UdsText.Contains("UDS Req ReadDataByIdentifier", StringComparison.Ordinal)) &&
            model.Trace.Rows.Any(row => row.UdsText.Contains("UDS Resp ReadDataByIdentifier", StringComparison.Ordinal));

        // Verify one-click Trace lenses, reversibility and direct UDS evidence links.
        var quickAnalysisControlsVisible =
            FindButton(window, "CAN errors") is not null &&
            FindButton(window, "No DBC") is not null &&
            FindButton(window, "Inspect UDS") is not null &&
            window.FindName("TraceGoTimeBox") is TextBox;
        model.Trace.QuickAnalysisCommand.Execute("uds");
        var quickUdsFilterWorks = model.Trace.Rows.Count > 0 &&
            model.Trace.Rows.All(row => !string.IsNullOrWhiteSpace(row.UdsText));
        model.Trace.QuickAnalysisCommand.Execute("fd");
        var quickFdFilterWorks = model.Trace.Rows.All(row =>
            row.Frame.Flags.HasFlag(RbgCanInsight.Domain.CanFrameFlags.FlexibleDataRate) ||
            row.Frame.DataLength > 8);
        model.Trace.QuickAnalysisCommand.Execute("errors");
        var quickErrorFilterWorks = model.Trace.Rows.All(row => row.Frame.IsError);
        model.Trace.QuickAnalysisCommand.Execute("all");
        var quickAllRestoresTrace = model.Trace.Rows.Count == model.FrameCount &&
                                   model.Trace.ViewModeIndex == 0 && !model.Trace.IsFilterActive;
        var measurementControlsVisible = FindButton(window, "Set A") is not null &&
                                         FindButton(window, "Set B") is not null;
        model.Trace.SelectedRow = model.Trace.Rows[0];
        model.Trace.SetMeasurementCommand.Execute("a");
        model.Trace.SelectedRow = model.Trace.Rows[1];
        model.Trace.SetMeasurementCommand.Execute("b");
        var abBeforeFilter = model.Trace.MeasurementSummaryText;
        model.Trace.QuickAnalysisCommand.Execute("uds");
        var abAcrossFilter = model.Trace.MeasurementSummaryText;
        model.Trace.QuickAnalysisCommand.Execute("all");
        var abMeasurementWorks = measurementControlsVisible &&
            abBeforeFilter.Contains("Δt", StringComparison.Ordinal) &&
            abBeforeFilter.Contains("A #1", StringComparison.Ordinal) &&
            abBeforeFilter == abAcrossFilter;
        model.Trace.SetMeasurementCommand.Execute("clear");
        abMeasurementWorks &= model.Trace.MeasurementSummaryText.Contains("set A", StringComparison.Ordinal);

        model.Trace.TimeDisplayModeIndex = 0;
        var exampleTime = model.Trace.Rows[Math.Min(2, model.Trace.Rows.Count - 1)].TimestampNanoseconds;
        model.Trace.GoToTimeCommand.Execute((exampleTime / 1_000_000_000d)
            .ToString("F9", System.Globalization.CultureInfo.InvariantCulture));
        var timeJumpWorks = model.Trace.SelectedRow is { } jump &&
            Math.Abs(jump.TimestampNanoseconds - exampleTime) <= 1000;
        var diagnosticFrameIndex = model.Uds.AllRows.FirstOrDefault()
            ?.Transaction.FrameIndices.FirstOrDefault(-1) ?? -1;
        if (diagnosticFrameIndex >= 0)
        {
            model.Trace.JumpToFrameIndex(diagnosticFrameIndex);
            model.InspectSelectedTraceUdsCommand.Execute(null);
        }
        var inspectUdsWorks = diagnosticFrameIndex >= 0 &&
            model.SelectedNavIndex == 8 && model.Uds.SelectedViewIndex == 1 &&
            model.Uds.SelectedRow?.Transaction.FrameIndices.Contains(diagnosticFrameIndex) == true;
        model.SelectedNavIndex = 1;
        model.Trace.QuickAnalysisCommand.Execute("all");
        model.Trace.TailCommand.Execute(null); // Go To Time now scrubs the frame window as well.
        DrainUi();
        Capture(window, Path.Combine(artifacts, "trace-quick-analysis.png"));
        model.Trace.Rows[0].IsExpanded = true;
        model.Trace.Rows[1].IsExpanded = true;
        model.SelectedNavIndex = 1;
        model.Trace.SelectedRow = model.Trace.Rows[0];
        model.Trace.SelectedMarkerCategory = MarkerCategory.Timing;
        model.Trace.ToggleMarkerCommand.Execute(null);
        DrainUi();
        Capture(window, Path.Combine(artifacts, "trace-inline-details.png"));
        var markerCategoryApplied = model.Trace.Rows[0].Category == MarkerCategory.Timing;
        var markerCount = model.Trace.MarkedFrames.Count;
        var previouslySelectedFrameIndex = model.Trace.SelectedRow?.FrameIndex;
        PumpUntil(model.LoadDbcAsync([dbcPath]));
        var dbcReloadPreservesTraceInvestigation =
            model.Trace.MarkedFrames.Count == markerCount &&
            model.Trace.SelectedRow?.FrameIndex == previouslySelectedFrameIndex &&
            model.Trace.Rows[0].Category == MarkerCategory.Timing;

        model.SelectedNavIndex = 8;
        DrainUi();
        var udsGrid = FindVisualChild<DataGrid>(window, "UdsTransactionsGrid")!;
        var udsWorkspaceHasTransaction = model.Uds.AllRows.Count > 0;
        var udsProtected = ProtectedVisible(udsGrid, "#", "Time [s]", "Direction", "Service", "Outcome");
        model.Uds.SelectedViewIndex = 0;
        DrainUi();
        var udsQuickTaskPresent = FindButton(window, "Fault memory") is not null &&
                                  FindButton(window, "DID values") is not null;
        ClickButton(window, "Fault memory");
        var udsQuickTaskNavigationWorks = udsQuickTaskPresent && model.Uds.SelectedViewIndex == 3;
        model.Uds.SelectedViewIndex = 0;
        DrainUi();
        Capture(window, Path.Combine(artifacts, "uds-overview.png"));
        model.Uds.SelectedViewIndex = 1;
        DrainUi();
        Capture(window, Path.Combine(artifacts, "uds-transactions.png"));
        // CDD/ODX explorer checks source ECU, scalar field metadata and a direct
        // link to DID ↔ CAN mapping without starting a live diagnostic session.
        model.CatalogExplorer.SearchText = "F405";
        var catalogDid = model.CatalogExplorer.SelectedDefinition;
        var diagnosticCatalogWorks = catalogDid?.Did == 0xF405 &&
                                     model.CatalogExplorer.Rows.Count > 0 &&
                                     model.CatalogExplorer.Fields.Count > 0 &&
                                     model.CatalogExplorer.SelectedField?.PositionText.Contains("byte 0", StringComparison.Ordinal) == true;
        model.Uds.SelectedViewIndex = 11;
        DrainUi();
        var diagnosticCatalogControlsVisible = FindVisualChild<DataGrid>(window, "DiagnosticCatalogGrid") is not null &&
                                               FindVisualChild<DataGrid>(window, "DiagnosticCatalogFieldsGrid") is not null;
        Capture(window, Path.Combine(artifacts, "diagnostic-catalog-explorer.png"));
        // Compare a baseline and updated synthetic ODX without replacing the
        // active diagnostic catalog or any of its source definitions.
        var updatedOdxPath = Path.Combine(artifacts, "gui-updated-diagnostic.odx");
        File.WriteAllText(updatedOdxPath,
            OdxFixture.Replace("SensorPlausibility", "SensorPlausibilityRev2",
                StringComparison.Ordinal));
        var activeDiagnosticCatalogCount = model.Mapping.Catalogs.Count;
        PumpUntil(model.CompareDiagnosticFilesAsync(odxPath, updatedOdxPath));
        var diagnosticDiff = model.DiagnosticComparisonRows.FirstOrDefault(row =>
            row.Scope == "DTC" && row.Change == RbgCanInsight.DiagData.DiagnosticDefinitionChange.Changed);
        var diagnosticComparisonDataWorks = model.HasDiagnosticComparison &&
            diagnosticDiff is { RequiresReview: true } &&
            diagnosticDiff.ChangedFields.Contains("Name", StringComparison.Ordinal) &&
            model.Mapping.Catalogs.Count == activeDiagnosticCatalogCount &&
            model.DiagnosticComparisonChangesOnly &&
            model.DiagnosticComparisonRows.All(row =>
                row.Change != RbgCanInsight.DiagData.DiagnosticDefinitionChange.Unchanged);

        var odxBefore = RbgCanInsight.DiagData.DidCatalog.LoadAll(odxPath);
        var odxAfter = RbgCanInsight.DiagData.DidCatalog.LoadAll(updatedOdxPath);
        var diagnosticDiffReport = RbgCanInsight.DiagData.DiagnosticDefinitionComparer.Compare(
            odxBefore, odxAfter);
        var diagnosticSheets = RbgCanInsight.TraceIO.DiagnosticDefinitionComparisonExport.BuildSheets(
            odxBefore, odxAfter, diagnosticDiffReport, odxPath, updatedOdxPath);
        var diagnosticComparisonXlsx = Path.Combine(artifacts, "gui-diagnostic-comparison.xlsx");
        var diagnosticComparisonZip = Path.Combine(artifacts, "gui-diagnostic-comparison.zip");
        RbgCanInsight.TraceIO.DiagnosticDefinitionComparisonExport.Write(
            diagnosticComparisonXlsx, diagnosticSheets, csvZip: false);
        RbgCanInsight.TraceIO.DiagnosticDefinitionComparisonExport.Write(
            diagnosticComparisonZip, diagnosticSheets, csvZip: true);
        using var diagnosticComparisonArchive = ZipFile.OpenRead(diagnosticComparisonZip);
        var diagnosticComparisonExportWorks = diagnosticSheets.Count == 3 &&
            diagnosticSheets[1].Headers.Contains("Field / PAR path") &&
            diagnosticComparisonArchive.Entries.Count == 3 &&
            new FileInfo(diagnosticComparisonXlsx).Length > 0;
        model.DiagnosticComparisonChangesOnly = false;
        var diagnosticComparisonShowsUnchanged = model.DiagnosticComparisonRows.Count ==
            diagnosticDiffReport.Rows.Count && model.DiagnosticComparisonRows.Any(row =>
                row.Change == RbgCanInsight.DiagData.DiagnosticDefinitionChange.Unchanged);
        model.DiagnosticComparisonChangesOnly = true;
        var diagnosticDiffExpander = FindVisualChild<Expander>(window, "DiagnosticDefinitionDiffExpander");
        if (diagnosticDiffExpander is not null)
        {
            diagnosticDiffExpander.IsExpanded = true;
            DrainUi();
        }
        var diagnosticComparisonUiWorks = diagnosticComparisonDataWorks &&
            diagnosticComparisonExportWorks && diagnosticComparisonShowsUnchanged &&
            diagnosticDiffExpander?.IsExpanded == true &&
            FindVisualChild<DataGrid>(window, "DiagnosticDefinitionDiffGrid") is not null;
        Capture(window, Path.Combine(artifacts, "diagnostic-definition-comparison.png"));

        // A comparison target may be inspected only when its exact file is
        // already loaded in the diagnostic workspace. The baseline DTC is
        // loaded; the updated version intentionally is not.
        model.SelectedDiagnosticDiffRow = diagnosticDiff;
        var baselineDiffNavigationWorks = diagnosticDiff is not null &&
            model.InspectDiagnosticDiffInLoadedCatalog(currentVersion: false) &&
            model.CatalogExplorer.SelectedDefinition is
            {
                Kind: "DTC",
                Identifier: "0x123456"
            } &&
            model.SelectedNavIndex == 8 &&
            model.Uds.SelectedViewIndex == 11;
        var previousSelection = model.CatalogExplorer.SelectedDefinition;
        var unloadedCurrentWasRejected = diagnosticDiff is not null &&
            !model.InspectDiagnosticDiffInLoadedCatalog(currentVersion: true) &&
            ReferenceEquals(model.CatalogExplorer.SelectedDefinition, previousSelection) &&
            model.StatusText.Contains("not loaded", StringComparison.OrdinalIgnoreCase);
        var diagnosticDiffNavigationUiWorks =
            baselineDiffNavigationWorks && unloadedCurrentWasRejected &&
            FindButton(window, "Inspect baseline") is not null &&
            FindButton(window, "Inspect current") is not null;

        // The compact comparison preview must filter without altering the
        // source-backed full export. Use the DTC changed in our ODX fixture.
        var fullDiffRows = diagnosticDiffReport.Rows.Count;
        var targetDiffEcu = diagnosticDiff?.Ecu ?? "ECM";
        model.SelectedDiagnosticComparisonEcu = targetDiffEcu;
        var ecuScopePreserved = model.DiagnosticComparisonRows.Count > 0 &&
            model.DiagnosticComparisonRows.All(row =>
                string.Equals(row.Ecu, targetDiffEcu, StringComparison.OrdinalIgnoreCase));
        model.SelectedDiagnosticComparisonScope = "DTC";
        var scopeFilterWorks = model.DiagnosticComparisonRows.Count > 0 &&
            model.DiagnosticComparisonRows.All(row => row.Scope == "DTC");
        model.DiagnosticComparisonSearchText = "SensorPlausibilityRev2";
        var searchFilterWorks = model.DiagnosticComparisonRows.Count > 0 &&
            model.DiagnosticComparisonRows.All(row =>
                row.CurrentDefinition.Contains("SensorPlausibilityRev2",
                    StringComparison.OrdinalIgnoreCase) ||
                row.CurrentName.Contains("SensorPlausibilityRev2",
                    StringComparison.OrdinalIgnoreCase));
        model.DiagnosticComparisonSearchText = "not-a-real-diagnostic-identifier";
        var emptySearchDoesNotModifySource = model.DiagnosticComparisonRows.Count == 0 &&
            diagnosticDiffReport.Rows.Count == fullDiffRows &&
            !model.HasSelectedDiagnosticDiff &&
            diagnosticSheets[1].Rows.Count == fullDiffRows;
        model.DiagnosticComparisonSearchText = string.Empty;
        model.SelectedDiagnosticComparisonScope = "All types";
        model.SelectedDiagnosticComparisonEcu = "All ECUs";
        var resetDiffFilterWorks = model.DiagnosticComparisonRows.Count ==
            diagnosticDiffReport.Rows.Count(row =>
                row.Change != RbgCanInsight.DiagData.DiagnosticDefinitionChange.Unchanged);
        var diagnosticComparisonFiltersPassed = ecuScopePreserved &&
            scopeFilterWorks && searchFilterWorks &&
            emptySearchDoesNotModifySource && resetDiffFilterWorks;

        // Preserve the older diagnostic → mapping regression precondition:
        // its selected entity must be a DID, not the navigated DTC.
        model.CatalogExplorer.SearchText = "F405";
        model.CatalogExplorer.SelectedDefinition =
            model.CatalogExplorer.Rows.FirstOrDefault(row => row.Did == 0xF405);
        DrainUi();

        model.MapSelectedDiagnosticDidCommand.Execute(null);
        var didCatalogCrossNavigationWorks = model.SelectedNavIndex == 7 &&
                                              model.Mapping.SearchText.Contains("F405", StringComparison.OrdinalIgnoreCase) &&
                                              model.Mapping.Rows.Count == 1 &&
                                              model.Mapping.SelectedRow?.Did.Did == 0xF405 &&
                                              string.Equals(model.Mapping.SelectedRow.Did.Ecu,
                                                  catalogDid?.Ecu, StringComparison.OrdinalIgnoreCase) &&
                                              string.Equals(model.Mapping.SelectedRow.Did.SourceFile,
                                                  catalogDid?.SourceFile, StringComparison.OrdinalIgnoreCase);
        model.BrowseSelectedMappingDidCommand.Execute(null);
        var mappingToCatalogNavigationWorks =
            model.SelectedNavIndex == 8 && model.Uds.SelectedViewIndex == 11 &&
            model.CatalogExplorer.SelectedDefinition?.Did == 0xF405 &&
            string.Equals(model.CatalogExplorer.SelectedDefinition.SourceFile,
                catalogDid?.SourceFile, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(model.CatalogExplorer.SelectedDefinition.Ecu,
                catalogDid?.Ecu, StringComparison.OrdinalIgnoreCase);
        // Editing the search and status filter must release the exact-source
        // focus, otherwise other DIDs would silently remain invisible.
        model.Mapping.SearchText = string.Empty;
        model.Mapping.StatusFilterIndex = 1;
        model.Mapping.StatusFilterIndex = 0;
        var catalogFocusCanBeCleared = model.Mapping.Rows.Count == model.Mapping.DidCount;

        // DID → offline UDS sample → physical CAN response → restore all samples.
        // A matching numeric DID alone cannot prove a specific ECU identity,
        // so all reconstructed endpoint groups remain visible.
        var observedCatalogDid = model.CatalogExplorer.Rows.FirstOrDefault(row =>
            row.Did is int did && model.Uds.DidSampleRows.Any(sample => sample.Did == did));
        if (observedCatalogDid is not null)
            model.CatalogExplorer.SelectedDefinition = observedCatalogDid;
        var didSamplesFocused = observedCatalogDid is not null &&
            model.ShowSelectedDiagnosticDidSamples() &&
            model.Uds.SelectedViewIndex == 2 &&
            model.SelectedNavIndex == 8 &&
            model.Uds.HasDidSampleFocus &&
            model.Uds.VisibleDidSampleRows.Count > 0 &&
            model.Uds.VisibleDidSampleRows.All(row => row.Did == observedCatalogDid.Did);
        DrainUi();
        var didSampleGridVisible = FindVisualChild<DataGrid>(window, "UdsDidSamplesGrid") is not null;
        var lastDidSampleFrame = model.Uds.SelectedDidSample?.Samples.LastOrDefault()?.FrameIndex ?? -1;
        model.Uds.OpenDidSampleSourceFrameCommand.Execute(null);
        var didSampleTraceLinkWorks = lastDidSampleFrame >= 0 &&
            model.SelectedNavIndex == 1 &&
            model.Trace.SelectedRow?.FrameIndex == lastDidSampleFrame;
        model.Uds.ClearDidSampleFocusCommand.Execute(null);
        var didSampleFilterResets = !model.Uds.HasDidSampleFocus &&
            model.Uds.VisibleDidSampleRows.Count == model.Uds.DidSampleRows.Count;
        var didToRecordedEvidenceUiPassed = didSamplesFocused &&
            didSampleGridVisible && didSampleTraceLinkWorks && didSampleFilterResets;
        model.SelectedNavIndex = 8;
        model.Uds.SelectedViewIndex = 1;
        DrainUi();

        var udsInsightViewsPassed = model.Uds.DidSampleRows.Count > 0 &&
                                    model.Uds.FaultRows.Count > 0 &&
                                    model.Uds.RoutineRows.Count > 0 &&
                                    model.Uds.CorrelationRows.Any(row => row.Verdict == RbgCanInsight.Correlation.CorrelationVerdict.StrongCandidate);
        model.Uds.SelectedViewIndex = 3;
        DrainUi();
        Capture(window, Path.Combine(artifacts, "uds-fault-memory.png"));
        model.Uds.SelectedViewIndex = 4;
        DrainUi();
        Capture(window, Path.Combine(artifacts, "uds-routines.png"));
        model.Uds.SelectedViewIndex = 5;
        DrainUi();
        var udsCorrelationEvidenceUiVisible =
            FindVisualChild<DataGrid>(window, "UdsTemporalCorrelationGrid") is not null &&
            FindVisualChild<ScrollViewer>(window, "UdsCorrelationDetailsScroller") is not null &&
            model.Uds.CorrelationRows.All(item =>
                item.PairCount > 0 || item.EvidenceSummary == item.Explanation);
        Capture(window, Path.Combine(artifacts, "uds-can-correlation.png"));
        model.Uds.SelectedViewIndex = 7;
        DrainUi();
        Capture(window, Path.Combine(artifacts, "offline-findings.png"));
        var offlineAnalysisViewsPassed = model.Uds.FindingRows.Count > 0 &&
                                         model.Uds.SequenceRows.Count == model.Uds.AllRows.Count &&
                                         model.Uds.EcuSnapshotRows.Count > 0 &&
                                         model.Uds.CatalogCoverageRows.Count == 4;
        model.Uds.SelectedViewIndex = 8;
        DrainUi();
        Capture(window, Path.Combine(artifacts, "diagnostic-sequence.png"));
        model.Uds.SelectedViewIndex = 9;
        DrainUi();
        Capture(window, Path.Combine(artifacts, "recorded-ecu-snapshot.png"));
        model.Uds.SelectedViewIndex = 10;
        DrainUi();
        Capture(window, Path.Combine(artifacts, "diagnostic-catalog-coverage.png"));
        model.Uds.SelectedViewIndex = 1;
        model.SelectedNavIndex = 1;
        DrainUi();

        // Every marker category + an expanded-but-unselected row, to catch per-row chrome
        // regressions (stray borders, layout shifts) that only show in specific row states.
        model.Trace.SelectedRow = model.Trace.Rows[2];
        model.Trace.SelectedMarkerCategory = MarkerCategory.Manual;
        model.Trace.ToggleMarkerCommand.Execute(null);
        model.Trace.SelectedRow = model.Trace.Rows[3];
        model.Trace.SelectedMarkerCategory = MarkerCategory.Diagnostic;
        model.Trace.ToggleMarkerCommand.Execute(null);
        model.Trace.SelectedRow = model.Trace.Rows[4];
        model.Trace.SelectedMarkerCategory = MarkerCategory.Range;
        model.Trace.ToggleMarkerCommand.Execute(null);
        model.Trace.SelectedRow = null;
        DrainUi();
        Capture(window, Path.Combine(artifacts, "trace-marker-styles.png"));
        model.Trace.SelectedRow = model.Trace.Rows[0];
        DrainUi();

        var traceGrid = FindVisualChild<DataGrid>(window, "TraceGrid")!;
        var traceScrollArrows = FindVisualChildren<RepeatButton>(traceGrid)
            .Select(button => button.Content?.ToString())
            .Where(content => content is not null)
            .ToHashSet();
        var traceScrollbarHasArrows = traceScrollArrows.Contains("▲") && traceScrollArrows.Contains("▼");
        var utilityPanel = (Expander)window.FindName("UtilityPanelExpander");
        var utilityInitiallyCollapsed = !utilityPanel.IsExpanded;
        utilityPanel.IsExpanded = true;
        DrainUi();
        var utilityExpandedSaved = AppSettingsStore.LoadUtilityPanelExpanded();
        utilityPanel.IsExpanded = false;
        DrainUi();
        var utilityPanelCollapsible = utilityInitiallyCollapsed && utilityExpandedSaved &&
                                      !AppSettingsStore.LoadUtilityPanelExpanded();
        var overviewRow = (RowDefinition)window.FindName("TraceOverviewRow");
        overviewRow.Height = new GridLength(255);
        window.UpdateLayout();
        var overviewSplitter = (GridSplitter)window.FindName("TraceOverviewSplitter");
        overviewSplitter.RaiseEvent(new DragCompletedEventArgs(0, 65, false)
        {
            RoutedEvent = Thumb.DragCompletedEvent
        });
        var traceOverviewResizable = Math.Abs(AppSettingsStore.LoadTraceOverviewHeight() - overviewRow.ActualHeight) < 2;
        var requestedOrder = new[]
        {
            "NavUds", "NavTrace", "NavDashboard", "NavDbcManager", "NavSignals",
            "NavMatrix", "NavByteMatrix", "NavStatistics", "NavMapping", "NavInvestigation"
        };
        AppSettingsStore.SaveWorkspaceOrder(requestedOrder);
        var orderProbe = new MainWindow();
        var probePanel = (StackPanel)orderProbe.FindName("WorkspaceNavPanel");
        var workspaceOrderPersists = probePanel.Children.OfType<RadioButton>().Select(button => button.Name)
            .SequenceEqual(requestedOrder);
        orderProbe.Close();
        var matrixGrid = FindVisualChild<DataGrid>(window, "MatrixGrid")!;
        var signalsGrid = FindVisualChild<DataGrid>(window, "SignalsGrid")!;
        var byteMatrixGrid = FindVisualChild<DataGrid>(window, "ByteMatrixGrid")!;
        var traceProtected = ProtectedVisible(traceGrid, "Expand", "Plot", "Mark", "#");
        var traceInlineExpandedRows = model.Trace.Rows.Take(2).Count(row => row.IsExpanded);
        var health = new TraceHealthWindow(model)
        {
            Owner = window,
            Width = 980,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false
        };
        health.Show();
        DrainUi();
        Capture(health, Path.Combine(artifacts, "trace-health.png"));
        var hasTraceHealthRows = model.TraceHealthRows.Count > 0;
        health.Close();

        model.SelectedNavIndex = 4;
        DrainUi();
        Capture(window, Path.Combine(artifacts, "change-matrix-columns.png"));
        var matrixProtected = ProtectedVisible(matrixGrid, "Actions", "Activity");

        model.SelectedNavIndex = 5;
        var byteDetailRow = model.ByteMatrix.Rows.FirstOrDefault(row => row.MessageName.Length > 0);
        model.ByteMatrix.SelectedRow = byteDetailRow;
        DrainUi();
        Capture(window, Path.Combine(artifacts, "byte-matrix-details.png"));
        var byteMatrixProtected = ProtectedVisible(byteMatrixGrid, "Expand", "Plot");
        var byteMatrixDetailsTogglePassed = byteDetailRow is not null &&
                                            byteDetailRow.IsDetailOpen &&
                                            model.ByteMatrix.SelectedRowSignals.Count > 0;
        model.ByteMatrix.SelectedRow = null;
        DrainUi();
        byteMatrixDetailsTogglePassed = byteMatrixDetailsTogglePassed && !byteDetailRow!.IsDetailOpen;

        // Statistics: exercises the new per-channel bus load panel and the std-dev/burst columns.
        model.SelectedNavIndex = 6;
        DrainUi();
        Capture(window, Path.Combine(artifacts, "statistics-bus-load.png"));

        // Trace: exercises the new Time column mode selector and a stop-filter query.
        model.SelectedNavIndex = 1;
        model.Trace.SearchText = "-ch2";
        model.Trace.ApplyFilterCommand.Execute(null);
        model.Trace.TimeDisplayModeIndex = 3; // delta from previous frame of the same identifier
        DrainUi();
        Capture(window, Path.Combine(artifacts, "trace-stop-filter-time-mode.png"));

        // Full-query index is reused during replay; only the currently
        // visible prefix is exposed, including after backwards seeking.
        var stopFilterCandidateIndices = Enumerable.Range(0, model.FrameCount)
            .Where(i => model.Frames[i].Channel != 1).ToArray();
        var filteredReplayPrefixesCorrect = true;
        foreach (var percent in new[] { 10d, 65d, 30d, 100d })
        {
            model.Trace.TimelinePercent = percent;
            var end = (int)Math.Round((model.FrameCount - 1) * percent / 100d);
            var expected = stopFilterCandidateIndices.Count(index => index <= end);
            filteredReplayPrefixesCorrect &=
                model.Trace.Rows.Count == expected &&
                (model.Trace.Rows.Count == 0 ||
                 model.Trace.Rows.FrameIndexAt(model.Trace.Rows.Count - 1) <= end) &&
                model.Trace.Rows.PositionOfFrameIndex(model.FrameCount - 1) ==
                    (end == model.FrameCount - 1 &&
                     stopFilterCandidateIndices.Contains(model.FrameCount - 1)
                        ? model.Trace.Rows.Count - 1 : -1);
        }
        model.Trace.TailCommand.Execute(null);
        DrainUi();
        Capture(window, Path.Combine(artifacts, "trace-cached-filtered-replay.png"));

        // "Delta from previous row" must read the row actually above it under a filter, not the
        // previous frame in the whole trace (which could be one this filter just hid).
        model.Trace.TimeDisplayModeIndex = 2; // delta from the immediately preceding grid row
        DrainUi();
        Capture(window, Path.Combine(artifacts, "trace-stop-filter-previous-row-time-mode.png"));

        model.Trace.ClearFilterCommand.Execute(null);
        model.Trace.TimeDisplayModeIndex = 0;
        DrainUi();

        // Large-trace path: a first-time filter is built on a worker thread. It must give exactly
        // the rows the synchronous path gives, and leave no "filtering" state behind.
        model.Trace.QuickAnalysisCommand.Execute("uds");
        var syncUdsFrames = Enumerable.Range(0, model.Trace.Rows.Count)
            .Select(model.Trace.Rows.FrameIndexAt).ToArray();
        model.Trace.QuickAnalysisCommand.Execute("all");
        model.Trace.BackgroundFilterThreshold = 1;
        model.Trace.QuickAnalysisCommand.Execute("errors"); // different query: cache miss
        PumpUntil(model.Trace.FilterCompletion);
        model.Trace.QuickAnalysisCommand.Execute("uds");
        PumpUntil(model.Trace.FilterCompletion);
        var backgroundUdsFrames = Enumerable.Range(0, model.Trace.Rows.Count)
            .Select(model.Trace.Rows.FrameIndexAt).ToArray();
        var backgroundFilterWorks = syncUdsFrames.Length > 0 &&
            syncUdsFrames.SequenceEqual(backgroundUdsFrames) &&
            !model.Trace.IsFiltering && model.Trace.FilterProgressText.Length == 0;
        model.Trace.BackgroundFilterThreshold = 250_000;
        model.Trace.QuickAnalysisCommand.Execute("all");
        PumpUntil(model.Trace.FilterCompletion);
        DrainUi();

        // Guided query builder: build "100 -error", apply it to the Trace, reject bad input and
        // keep a named query across window instances.
        var queryStorePath = Path.Combine(artifacts, "gui-saved-queries.json");
        File.Delete(queryStorePath);
        var queryWindow = window.OpenQueryBuilder(new SavedQueryStore(queryStorePath));
        queryWindow.Width = 900;
        queryWindow.Show();
        DrainUi();
        var builder = (QueryBuilderViewModel)queryWindow.DataContext;
        builder.AddConditionCommand.Execute(null);
        builder.Conditions[0].Value = "100";
        builder.AddConditionCommand.Execute(null);
        builder.Conditions[1].Field = builder.FieldChoices.First(choice => choice.Field == RbgCanInsight.Analysis.QueryField.FrameKind);
        builder.Conditions[1].Value = "Error";
        builder.Conditions[1].Exclude = true;
        DrainUi();
        var builtText = builder.QueryText;
        builder.SaveName = "Only 0x100 without errors";
        builder.SaveCommand.Execute(null);
        builder.ApplyCommand.Execute(null);
        PumpUntil(model.Trace.FilterCompletion);
        DrainUi();
        Capture(queryWindow, Path.Combine(artifacts, "query-builder.png"));
        var appliedToTrace = model.Trace.AppliedQuery == "100 -error" &&
            model.Trace.Rows.Count > 0 &&
            Enumerable.Range(0, model.Trace.Rows.Count).All(i => model.Trace.Rows[i].IdentifierHex == "100");
        builder.Conditions[0].Value = "ZZ";
        var rejectsBadInput = !builder.IsValid && !builder.ApplyCommand.CanExecute(null) &&
            builder.Messages.Contains("hexadecimal", StringComparison.Ordinal);
        builder.Conditions[0].Value = "100";
        var reloaded = new QueryBuilderViewModel(new SavedQueryStore(queryStorePath), _ => { }, "ch1 -payload:AA");
        var savedQueryPersists = reloaded.SavedQueries.Any(item => item.Query == "100 -error") &&
            reloaded.Conditions.Count == 2 && reloaded.QueryText == "ch1 -payload:AA";
        var queryBuilderWorks = builtText == "100 -error" && appliedToTrace && rejectsBadInput && savedQueryPersists &&
            builder.Description.Contains("NOT frame is one of CAN error frames", StringComparison.Ordinal);
        queryWindow.Close();
        model.Trace.QuickAnalysisCommand.Execute("all");
        PumpUntil(model.Trace.FilterCompletion);
        DrainUi();

        // Multi-trace regression: a candidate recording that drops 0x200, adds 0x300, changes the
        // 0x100 period and signal values, loses the DTC/routine traffic and adds a negative response.
        var candidatePath = Path.Combine(artifacts, "gui-candidate.asc");
        File.WriteAllText(candidatePath, CandidateTraceFixture);
        PumpUntil(model.CompareWithAsync(candidatePath));
        DrainUi();
        var regressions = model.RegressionRows.ToArray();
        var regressionWorks =
            regressions.Any(r => r.Category == "Bus" && r.Subject == "CH1 200" && r.Summary.Contains("missing from the candidate")) &&
            regressions.Any(r => r.Category == "Bus" && r.Subject == "CH1 300" && r.Summary.Contains("only in the candidate")) &&
            regressions.Any(r => r.Category == "Timing" && r.Subject == "CH1 100" && r.Summary.Contains("Average period")) &&
            regressions.Any(r => r.Category == "Signal" && r.Summary == "Signal status changed") &&
            regressions.Any(r => r.Category == "Diagnostics" && r.Summary == "More negative responses") &&
            regressions.Any(r => r.Category == "Diagnostics" && r.Summary.Contains("missing from the candidate")) &&
            model.RegressionSummary.Contains("difference(s)", StringComparison.Ordinal) &&
            model.HasReferenceTrace;
        if (window.FindName("RegressionTab") is TabItem regressionTab) regressionTab.IsSelected = true;
        model.SelectedNavIndex = 6;
        DrainUi();
        Capture(window, Path.Combine(artifacts, "trace-regression.png"));
        var referenceButton = window.FindName("CompareWithReferenceButton") as Button;
        regressionWorks &= referenceButton is not null && model.CompareWithReferenceCommand.CanExecute(null);
        model.SelectedNavIndex = 2;
        DrainUi();

        // Investigation workspace: four synchronized panes on one cursor, plus saved layouts.
        // The comparison above replaced nothing in the base trace, so the base UDS analysis is intact.
        var investigation = model.Investigation;
        model.SelectedNavIndex = MainViewModel.InvestigationNavIndex;
        investigation.SelectedLayout = investigation.Layouts.First(layout => layout.Name == "Full investigation");
        DrainUi();
        var panesVisible = investigation.Slots.All(slot => slot.IsVisible) &&
            investigation.Slots.Select(slot => slot.Kind).Distinct().Count() == 4;
        var gridsRealized = window.FindName("InvestigationPaneGrid") is Grid paneGrid &&
            FindVisualChild<DataGrid>(paneGrid, "InvestigationTraceGrid") is not null &&
            FindVisualChild<DataGrid>(paneGrid, "InvestigationUdsGrid") is not null &&
            FindVisualChild<DataGrid>(paneGrid, "InvestigationFindingsGrid") is not null;
        var lastTransaction = model.Uds.Rows.Last();
        var lastFrame = lastTransaction.Transaction.FrameIndices.Min();
        model.Trace.JumpToFrameIndex(lastFrame);
        DrainUi();
        var traceDrivesUds = model.Trace.SelectedRow?.FrameIndex == lastFrame &&
            model.Uds.SelectedRow?.Transaction.FrameIndices.Contains(lastFrame) == true;
        var firstTransaction = model.Uds.Rows.First();
        model.Uds.SelectedRow = firstTransaction;
        DrainUi();
        var udsDrivesTrace = model.Trace.SelectedRow?.FrameIndex == firstTransaction.Transaction.FrameIndices.Min();
        var finding = model.Uds.FindingRows.First(row => row.FirstFrameIndex >= 0);
        model.Uds.SelectedFinding = finding;
        DrainUi();
        var findingDrivesTrace = model.Trace.SelectedRow?.FrameIndex == finding.FirstFrameIndex &&
            model.Uds.SelectedRow is not null;
        model.Trace.SelectedRow = model.Trace.Rows.First(row => row.IdentifierHex == "100");
        DrainUi();
        var decodedPaneHasSignals = model.Trace.SelectedRow?.DetailedSignals.Count > 0;
        Capture(window, Path.Combine(artifacts, "investigation-synchronized-panes.png"));

        var layoutStorePath = Path.Combine(artifacts, "gui-investigation-layouts.json");
        File.Delete(layoutStorePath);
        var layoutStore = new RbgCanInsight.Analysis.InvestigationLayoutStore(layoutStorePath);
        var scratch = new InvestigationViewModel(model.Trace, model.Uds, () => false, layoutStore);
        scratch.ArrangementChoice = scratch.ArrangementChoices.First(choice =>
            choice.Arrangement == RbgCanInsight.Analysis.InvestigationArrangement.Stacked);
        scratch.Slots[0].Kind = RbgCanInsight.Analysis.InvestigationPane.Findings;
        scratch.Slots[1].Kind = RbgCanInsight.Analysis.InvestigationPane.Trace;
        scratch.SaveName = "Findings over trace";
        scratch.SaveCommand.Execute(null);
        scratch.SaveName = "full investigation"; // built-in names are reserved
        scratch.SaveCommand.Execute(null);
        var reservedRejected = scratch.Status.Contains("built-in", StringComparison.Ordinal);
        var restored = new InvestigationViewModel(model.Trace, model.Uds, () => false, layoutStore);
        restored.SelectedLayout = restored.Layouts.First(layout => layout.Name == "Findings over trace");
        var layoutsPersist = restored.Arrangement == RbgCanInsight.Analysis.InvestigationArrangement.Stacked &&
            restored.Slots[0].Kind == RbgCanInsight.Analysis.InvestigationPane.Findings &&
            restored.Slots[1].Kind == RbgCanInsight.Analysis.InvestigationPane.Trace &&
            restored.Slots[0].IsVisible && restored.Slots[1].IsVisible && !restored.Slots[2].IsVisible &&
            reservedRejected;
        var investigationWorks = panesVisible && gridsRealized && traceDrivesUds && udsDrivesTrace &&
            findingDrivesTrace && decodedPaneHasSignals == true && layoutsPersist;
        model.SelectedNavIndex = 2;
        DrainUi();

        model.SelectedNavIndex = 3;
        model.Signals.IsCumulativeReveal = true;
        var series = model.Signals.EnsurePlotted("EngineData", "EngCoolantTemp") ??
                     model.Signals.AllSeries.First(item => item.Samples.Count > 0);
        DrainUi();
        Capture(window, Path.Combine(artifacts, "main-plot.png"));

        // Plot a second and third signal with different physical units (degC / % / km/h) so the
        // Y-axis auto-scale path (single shared axis vs. one scale per series once units mix) is
        // actually exercised, not just single-series rendering.
        model.Signals.EnsurePlotted("EngineData", "EngineLoad");
        model.Signals.EnsurePlotted("VehicleData", "VehicleSpeed");
        DrainUi();
        Capture(window, Path.Combine(artifacts, "main-plot-mixed-units-auto.png"));
        model.Signals.YAxisModeIndex = 1; // force shared
        DrainUi();
        Capture(window, Path.Combine(artifacts, "main-plot-shared-axis.png"));
        model.Signals.YAxisModeIndex = 2; // force per-signal
        DrainUi();
        Capture(window, Path.Combine(artifacts, "main-plot-per-signal-axis.png"));
        model.Signals.YAxisModeIndex = 0; // back to auto
        // The "Open" popup-window trigger was intentionally replaced by an inline
        // RowDetailsTemplate (click a signal row to preview its chart in place), so it no longer
        // exists as a column to protect.
        var signalsProtected = ProtectedVisible(signalsGrid, "Plot");

        var detail = new SignalDetailWindow(series)
        {
            Owner = window,
            Width = 920,
            Height = 620,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false
        };
        detail.Show();
        DrainUi();
        var before = detail.DetailWindowEndNanoseconds;
        ClickButton(detail, "▶ Play");
        var playChangedToPause = FindButton(detail, "⏸ Pause") is not null;
        var playbackPositions = new List<long> { detail.DetailWindowEndNanoseconds };
        for (var frameIndex = 0; frameIndex < 8; frameIndex++)
        {
            PumpFor(TimeSpan.FromMilliseconds(120));
            playbackPositions.Add(detail.DetailWindowEndNanoseconds);
        }

        if (FindButton(detail, "⏸ Pause") is not null)
        {
            ClickButton(detail, "⏸ Pause");
        }

        DrainUi();
        var after = playbackPositions.FirstOrDefault(position => position != before, detail.DetailWindowEndNanoseconds);
        var playbackMoved = playbackPositions.Any(position => position != before);
        var pauseChangedToPlay = FindButton(detail, "▶ Play") is not null;
        Capture(detail, Path.Combine(artifacts, "quick-plot-playback.png"));

        var presetPath = Path.Combine(artifacts, "gui-fixture.rbgpreset");
        var preset = new WorkspacePreset(1, 0, 0, 1.0, 1.0, 30.0, 1.0, 0, []);
        File.WriteAllText(presetPath, JsonSerializer.Serialize(preset));
        PumpUntil(model.LoadPresetFileAsync(presetPath));
        DrainUi();
        var presetLibrary = new PresetLibraryWindow(model)
        {
            Owner = window,
            Width = 680,
            Height = 440,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false
        };
        presetLibrary.Show();
        DrainUi();
        Capture(presetLibrary, Path.Combine(artifacts, "preset-library.png"));
        var presetLibraryHasEntries = model.HasPresetLibraryEntries && model.PresetLibraryEntries.Count > 0;
        model.PresetLibraryEntries[0].Name = "renamed-preset";
        var presetLibraryRenamed = PresetLibraryStore.Load().Any(entry => entry.Name == "renamed-preset");
        var presetLibraryEntryCountBeforeRemove = model.PresetLibraryEntries.Count;
        model.RemovePresetLibraryEntry(presetPath);
        DrainUi();
        var presetLibraryRemoved = model.PresetLibraryEntries.Count < presetLibraryEntryCountBeforeRemove;
        presetLibrary.Close();

        var reportPath = Path.Combine(artifacts, "gui-analysis-report.zip");
        typeof(MainViewModel).GetMethod("WriteAnalysisReport", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [reportPath, null, model.Triggers.LastReport, model.Triggers.LastEvaluatedRule, model.Sequences.LastReport, model.DbcValues.LastReport]);
        using var reportArchive = ZipFile.OpenRead(reportPath);
        var requiredDiagnosticEntries = new[]
        {
            "report.html", "diagnostic-analysis.json", "uds-transactions.csv", "did-samples.csv",
            "trace-import-health.json", "trace-import-channels.csv", "trace-import-issues.csv",
            "fault-memory.csv", "routine-analysis.csv", "uds-can-correlation.csv", "diagnostic-report.pdf",
            "dbc-cycle-timing.csv", "dbc-cycle-gap-evidence.csv",
            "offline-findings.csv", "diagnostic-sequence.csv", "ecu-snapshot.csv", "diagnostic-catalog-coverage.csv"
        };
        var diagnosticReportPassed = requiredDiagnosticEntries.All(name => reportArchive.GetEntry(name) is not null);
        var pdfEntry = reportArchive.GetEntry("diagnostic-report.pdf");
        if (pdfEntry is not null)
        {
            using var pdfStream = pdfEntry.Open();
            using var pdfBytes = new MemoryStream();
            pdfStream.CopyTo(pdfBytes);
            var pdfText = System.Text.Encoding.ASCII.GetString(pdfBytes.ToArray());
            diagnosticReportPassed &= pdfText.StartsWith("%PDF-", StringComparison.Ordinal) &&
                                      pdfText.Contains("Page 1 of ", StringComparison.Ordinal) &&
                                      !pdfText.Contains('?', StringComparison.Ordinal);
        }

        var localTracePath = Environment.GetEnvironmentVariable("RBG_CAN_LOCAL_UDS_TRACE");
        var localCatalogPath = Environment.GetEnvironmentVariable("RBG_CAN_LOCAL_UDS_CATALOG");
        var localOemConfigured = !string.IsNullOrWhiteSpace(localTracePath) && !string.IsNullOrWhiteSpace(localCatalogPath);
        var localOemUiPassed = !localOemConfigured;
        var localOemTransactionCount = 0;
        var localOemDtcTransactionCount = 0;
        if (localOemConfigured)
        {
            PumpUntil(model.Mapping.LoadCatalogsAsync([localCatalogPath!]));
            PumpUntil(model.LoadTraceAsync(localTracePath!));
            model.SelectedNavIndex = 8;
            DrainUi();
            localOemTransactionCount = model.Uds.Rows.Count;
            localOemDtcTransactionCount = model.Uds.Rows.Count(row => row.Transaction.Decoded.ServiceId == 0x19);
            localOemUiPassed = localOemTransactionCount > 0 && localOemDtcTransactionCount > 0 &&
                               model.Mapping.DidCount > 0;
            model.Uds.FilterIndex = 2;
            model.Uds.SelectedRow = model.Uds.Rows.FirstOrDefault();
            DrainUi();
            Capture(window, Path.Combine(artifacts, "uds-local-oem.png"));
        }

        var result = new
        {
            model.TraceSummary,
            TraceInlineExpandedRows = traceInlineExpandedRows,
            TraceProtectedColumnsVisible = traceProtected,
            TraceHealthRowsVisible = hasTraceHealthRows,
            TraceImportHealthIsComplete = importHealthIsComplete,
            TraceImportHealthUiWorks = importHealthUiWorks,
            SmallTraceAvoidsMillionFramePreallocation = smallTraceAvoidsMillionFramePreallocation,
            MatrixProtectedColumnsVisible = matrixProtected,
            SignalsProtectedColumnsVisible = signalsProtected,
            ByteMatrixProtectedColumnsVisible = byteMatrixProtected,
            ByteMatrixDetailsTogglePassed = byteMatrixDetailsTogglePassed,
            QuickPlotPlaybackMoved = playbackMoved,
            QuickPlotUsesSingleToggle = playChangedToPause && pauseChangedToPlay,
            TraceScrollbarHasArrows = traceScrollbarHasArrows,
            WorkspaceOrderPersists = workspaceOrderPersists,
            UtilityPanelCollapsible = utilityPanelCollapsible,
            TraceOverviewResizable = traceOverviewResizable,
            UdsWorkspaceHasTransaction = udsWorkspaceHasTransaction,
            UdsProtectedColumnsVisible = udsProtected,
            UdsInsightViewsPassed = udsInsightViewsPassed,
            SavedMappingReviewRestoreWorks = savedReviewRestoreWorks,
            SavedMappingComparisonUiPassed = savedMapComparisonUiPassed,
            DbcDefinitionComparisonUiPassed = dbcComparisonUiWorks,
            DbcDiffToManagerNavigationPassed = dbcDiffNavigationUiPassed,
            DiagnosticDefinitionComparisonUiPassed = diagnosticComparisonUiWorks,
            DiagnosticDiffToCatalogNavigationPassed = diagnosticDiffNavigationUiWorks,
            DiagnosticDiffPreviewFiltersPassed = diagnosticComparisonFiltersPassed,
            UdsCorrelationEvidenceUiVisible = udsCorrelationEvidenceUiVisible,
            OfflineAnalysisViewsPassed = offlineAnalysisViewsPassed,
            DiagnosticReportPassed = diagnosticReportPassed,
            LocalOemConfigured = localOemConfigured,
            LocalOemUiPassed = localOemUiPassed,
            LocalOemTransactionCount = localOemTransactionCount,
            LocalOemDtcTransactionCount = localOemDtcTransactionCount,
            QuickPlotBeforeNs = before,
            QuickPlotAfterNs = after,
            MarkerCategoryApplied = markerCategoryApplied,
            MarkerCount = markerCount,
            DbcReloadPreservesTraceInvestigation = dbcReloadPreservesTraceInvestigation,
            FilteredReplayPrefixesCorrect = filteredReplayPrefixesCorrect,
            PresetLibraryHasEntries = presetLibraryHasEntries,
            PresetLibraryRenameWorks = presetLibraryRenamed,
            PresetLibraryRemoveWorks = presetLibraryRemoved,
            OnboardingOpenedOnFirstLaunch = onboardingOpened,
            OnboardingNavigatesWorkspaces = onboardingNavigated,
            OnboardingExplainsOfflineUds = onboardingExplainsOfflineUds,
            OnboardingFlagSaved = onboardingFlagSaved,
            UdsRequestAndResponseDecoded = udsDecoded,
            DbcBitInspectorWorks = dbcInspectorWorks,
            DbcEditControlsVisible = dbcEditControlsVisible,
            DbcEditStagesWithoutMutatingSource = dbcEditStageWorks,
            DbcEditSaveAsWorks = dbcEditSaveWorks,
            DbcEditRevertWorks = dbcEditRevertWorks,
            DbcNamedValueGridVisible = dbcValueEditorGridVisible,
            DbcNamedValueDuplicateRejected = duplicateRawCodeRejected,
            DbcNamedValueStageWorks = dbcValueStageWorks,
            DbcNamedValueSaveAsWorks = dbcValueSaveWorks,
            DbcNamedValueRevertWorks = dbcValueRevertWorks,
            DefinitionWorkbookExportsWork = definitionExportsWork,
            DbcInspectorControlsVisible = dbcInspectorControlsVisible,
            DbcSourceFrameNavigationWorks = dbcSourceFrameNavigationWorks,
            DbcNominalCycleAnalysisWorks = nominalCycleAnalysisWorks,
            DbcNominalCycleUiVisible = nominalCycleUiVisible,
            DbcNominalCycleTraceLinkWorks = nominalCycleTraceLinkWorks,
            TraceToDbcNavigationWorks = traceToDbcNavigationWorks,
            DiagnosticCatalogExplorerWorks = diagnosticCatalogWorks,
            DiagnosticCatalogControlsVisible = diagnosticCatalogControlsVisible,
            DidCatalogCrossNavigationWorks = didCatalogCrossNavigationWorks,
            MappingToCatalogNavigationPassed = mappingToCatalogNavigationWorks,
            DidToRecordedEvidenceUiPassed = didToRecordedEvidenceUiPassed,
            ExactCatalogFocusCanBeCleared = catalogFocusCanBeCleared,
            DashboardUdsWorkflowWorks = dashboardUdsWorkflowWorks,
            TraceQuickAnalysisControlsVisible = quickAnalysisControlsVisible,
            TraceQuickUdsFilterWorks = quickUdsFilterWorks,
            TraceQuickFdFilterWorks = quickFdFilterWorks,
            TraceQuickErrorFilterWorks = quickErrorFilterWorks,
            TraceQuickAllRestoresTrace = quickAllRestoresTrace,
            TraceABMeasurementWorks = abMeasurementWorks,
            TraceBackgroundFilterMatchesSynchronous = backgroundFilterWorks,
            QueryBuilderWorks = queryBuilderWorks,
            TraceRegressionWorks = regressionWorks,
            InvestigationPanesSynchronized = investigationWorks,
            TraceGoToTimeWorks = timeJumpWorks,
            TraceInspectUdsWorks = inspectUdsWorks,
            UdsQuickTaskNavigationWorks = udsQuickTaskNavigationWorks,
            Screenshots = Directory.GetFiles(artifacts, "*.png").Select(Path.GetFileName).Order().ToArray()
        };

        File.WriteAllText(
            Path.Combine(artifacts, "gui-verification.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

        detail.Close();
        window.Close();
        return result.DbcEditControlsVisible &&
               result.DbcEditStagesWithoutMutatingSource &&
               result.DbcEditSaveAsWorks &&
               result.DbcEditRevertWorks &&
               result.DbcNamedValueGridVisible &&
               result.DbcNamedValueDuplicateRejected &&
               result.DbcNamedValueStageWorks &&
               result.DbcNamedValueSaveAsWorks &&
               result.DbcNamedValueRevertWorks &&
               result.DefinitionWorkbookExportsWork &&
               result.QuickPlotPlaybackMoved &&
               result.QuickPlotUsesSingleToggle &&
               result.TraceScrollbarHasArrows &&
               result.WorkspaceOrderPersists &&
               result.UtilityPanelCollapsible &&
               result.TraceOverviewResizable &&
               result.UdsWorkspaceHasTransaction &&
               result.UdsProtectedColumnsVisible &&
               result.UdsInsightViewsPassed &&
               result.SavedMappingReviewRestoreWorks &&
               result.SavedMappingComparisonUiPassed &&
               result.DbcDefinitionComparisonUiPassed &&
               result.DbcDiffToManagerNavigationPassed &&
               result.DiagnosticDefinitionComparisonUiPassed &&
               result.DiagnosticDiffToCatalogNavigationPassed &&
               result.DiagnosticDiffPreviewFiltersPassed &&
               result.UdsCorrelationEvidenceUiVisible &&
               result.OfflineAnalysisViewsPassed &&
               result.DiagnosticReportPassed &&
               result.LocalOemUiPassed &&
               result.TraceInlineExpandedRows == 2 &&
               result.TraceHealthRowsVisible &&
               importHealthIsComplete &&
               importHealthUiWorks &&
               smallTraceAvoidsMillionFramePreallocation &&
               traceProtected &&
               matrixProtected &&
               signalsProtected &&
               byteMatrixProtected &&
               byteMatrixDetailsTogglePassed &&
               markerCategoryApplied &&
               markerCount == 1 &&
               dbcReloadPreservesTraceInvestigation &&
               filteredReplayPrefixesCorrect &&
               presetLibraryHasEntries &&
               presetLibraryRenamed &&
               presetLibraryRemoved &&
               onboardingOpened &&
               onboardingNavigated &&
               onboardingExplainsOfflineUds &&
               onboardingFlagSaved &&
               udsDecoded &&
               dbcInspectorWorks &&
               dbcInspectorControlsVisible &&
               dbcSourceFrameNavigationWorks &&
               nominalCycleAnalysisWorks &&
               nominalCycleUiVisible &&
               nominalCycleTraceLinkWorks &&
               traceToDbcNavigationWorks &&
               diagnosticCatalogWorks &&
               diagnosticCatalogControlsVisible &&
               didCatalogCrossNavigationWorks &&
               result.MappingToCatalogNavigationPassed &&
               result.DidToRecordedEvidenceUiPassed &&
               result.ExactCatalogFocusCanBeCleared &&
               dashboardUdsWorkflowWorks &&
               quickAnalysisControlsVisible &&
               quickUdsFilterWorks &&
               quickFdFilterWorks &&
               quickErrorFilterWorks &&
               quickAllRestoresTrace &&
               abMeasurementWorks &&
               backgroundFilterWorks &&
               queryBuilderWorks &&
               regressionWorks &&
               investigationWorks &&
               timeJumpWorks &&
               inspectUdsWorks &&
               udsQuickTaskNavigationWorks
            ? 0
            : 2;
    }

    internal static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "RbgCanInsight.sln")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException("Could not locate RbgCanInsight.sln.");
    }

    internal static void PumpUntil(Task task)
    {
        while (!task.IsCompleted)
        {
            DrainUi();
            Thread.Sleep(15);
        }

        task.GetAwaiter().GetResult();
        DrainUi();
    }

    internal static void PumpFor(TimeSpan duration)
    {
        var end = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < end)
        {
            DrainUi();
            Thread.Sleep(15);
        }
    }

    internal static void DrainUi()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static bool ProtectedVisible(DataGrid grid, params string[] headers)
    {
        foreach (var header in headers)
        {
            var column = grid.Columns.FirstOrDefault(item => (item.Header?.ToString() ?? string.Empty) == header);
            if (column is null || column.Visibility != Visibility.Visible || column.ActualWidth < Math.Min(40, column.MinWidth))
            {
                return false;
            }
        }

        return true;
    }

    private static void ClickButton(DependencyObject root, string content)
    {
        var button = FindButton(root, content);
        if (button is not null)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return;
        }

        throw new InvalidOperationException($"Button '{content}' was not found.");
    }

    private static Button? FindButton(DependencyObject root, string content) =>
        FindVisualChildren<Button>(root).FirstOrDefault(button => button.Content?.ToString() == content);

    internal static T? FindVisualChild<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        return FindVisualChildren<T>(root).FirstOrDefault(item => item.Name == name);
    }

    internal static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private const string TraceFixture = """
date Tue Jul 07 10:00:00.000 2026
base hex  timestamps absolute
internal events logged
0.000000 1 100 Rx d 8 32 20 00 00 00 00 00 00
0.100000 1 100 Rx d 8 37 28 00 00 00 00 00 00
0.200000 1 100 Rx d 8 3C 30 00 00 00 00 00 00
1.600000 1 100 Rx d 8 FF 80 00 00 00 00 00 00
1.300000 1 200 Rx d 8 88 13 00 00 00 00 00 00
1.400000 1 200 Rx d 8 10 27 00 00 00 00 00 00
1.500000 1 200 Rx d 8 98 3A 00 00 00 00 00 00
1.700000 1 7E0 Rx d 8 03 22 F1 90 00 00 00 00
1.750000 1 7E8 Rx d 8 05 62 F1 90 00 5A 00 00
2.000000 1 100 Rx d 8 32 20 00 00 00 00 00 00
2.010000 1 7E0 Rx d 8 03 22 F4 05 00 00 00 00
2.020000 1 7E8 Rx d 8 04 62 F4 05 32 00 00 00
3.000000 1 100 Rx d 8 37 20 00 00 00 00 00 00
3.010000 1 7E0 Rx d 8 03 22 F4 05 00 00 00 00
3.020000 1 7E8 Rx d 8 04 62 F4 05 37 00 00 00
4.000000 1 100 Rx d 8 3C 20 00 00 00 00 00 00
4.010000 1 7E0 Rx d 8 03 22 F4 05 00 00 00 00
4.020000 1 7E8 Rx d 8 04 62 F4 05 3C 00 00 00
5.000000 1 100 Rx d 8 41 20 00 00 00 00 00 00
5.010000 1 7E0 Rx d 8 03 22 F4 05 00 00 00 00
5.020000 1 7E8 Rx d 8 04 62 F4 05 41 00 00 00
6.000000 1 100 Rx d 8 46 20 00 00 00 00 00 00
6.010000 1 7E0 Rx d 8 03 22 F4 05 00 00 00 00
6.020000 1 7E8 Rx d 8 04 62 F4 05 46 00 00 00
7.000000 1 7E0 Rx d 8 03 19 02 FF 00 00 00 00
7.010000 1 7E8 Rx d 8 07 59 02 FF 12 34 56 0D
7.100000 1 7E0 Rx d 8 04 14 FF FF FF 00 00 00
7.110000 1 7E8 Rx d 8 01 54 00 00 00 00 00 00
8.000000 1 7E0 Rx d 8 05 31 01 02 03 AA 00 00
8.010000 1 7E8 Rx d 8 03 7F 31 78 00 00 00 00
8.020000 1 7E8 Rx d 8 05 71 01 02 03 00 00 00
8.100000 1 7E0 Rx d 8 04 31 03 02 03 00 00 00
8.110000 1 7E8 Rx d 8 05 71 03 02 03 01 00 00
8.120000 1 200 Rx d 8 A0 4B 00 00 00 00 00 00
""";

    private const string CandidateTraceFixture = """
date Tue Jul 07 10:00:00.000 2026
base hex  timestamps absolute
internal events logged
0.000000 1 100 Rx d 8 32 20 00 00 00 00 00 00
0.400000 1 7E0 Rx d 8 03 22 F4 05 00 00 00 00
0.410000 1 7E8 Rx d 8 03 7F 22 31 00 00 00 00
0.500000 1 100 Rx d 8 32 20 00 00 00 00 00 00
1.000000 1 100 Rx d 8 32 20 00 00 00 00 00 00
1.500000 1 100 Rx d 8 32 20 00 00 00 00 00 00
2.000000 1 100 Rx d 8 32 20 00 00 00 00 00 00
2.010000 1 7E0 Rx d 8 03 22 F4 05 00 00 00 00
2.020000 1 7E8 Rx d 8 04 62 F4 05 32 00 00 00
3.000000 1 300 Rx d 8 01 00 00 00 00 00 00 00
3.100000 1 300 Rx d 8 01 00 00 00 00 00 00 00
3.200000 1 300 Rx d 8 01 00 00 00 00 00 00 00
""";

    private const string DbcFixture = """
VERSION ""

BO_ 256 EngineData: 8 ECM
 SG_ EngCoolantTemp : 0|8@1+ (1,-40) [-40|200] "degC" Vector__XXX
 SG_ EngineLoad : 8|8@1+ (0.5,0) [0|100] "%" Vector__XXX
 SG_ MissingMuxBranch : 16|8@1+ (1,0) [0|10] "" Vector__XXX

BO_ 512 VehicleData: 8 BCM
 SG_ VehicleSpeed : 0|16@1+ (0.01,0) [0|250] "km/h" Vector__XXX

BA_ "GenMsgCycleTime" BO_ 256 100;
BA_ "GenSigStartValue" SG_ 256 EngCoolantTemp 80;
VAL_ 256 MissingMuxBranch 0 "Off" 1 "On";
""";

    private const string OdxFixture = """
<ODX>
  <DIAG-LAYER-CONTAINER><SHORT-NAME>ECM</SHORT-NAME></DIAG-LAYER-CONTAINER>
  <ECU-VARIANT><SHORT-NAME>ECM</SHORT-NAME></ECU-VARIANT>
  <REQUEST ID="REQ_F405">
    <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
    <PARAM SEMANTIC="ID"><CODED-VALUE>62469</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
  </REQUEST>
  <POS-RESPONSE ID="RESP_F405"><PARAM SEMANTIC="DATA"><BYTE-POSITION>3</BYTE-POSITION><DOP-REF ID-REF="DOP_TEMP" /></PARAM></POS-RESPONSE>
  <DATA-OBJECT-PROP ID="DOP_TEMP">
    <SHORT-NAME>EngineCoolantTemperature</SHORT-NAME>
    <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT8"><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
    <PHYSICAL-TYPE BASE-DATA-TYPE="A_FLOAT64" />
    <COMPU-METHOD><COMPU-INTERNAL-TO-PHYS><COMPU-SCALES><COMPU-SCALE><COMPU-RATIONAL-COEFFS><COMPU-NUMERATOR><V>-40</V><V>1</V></COMPU-NUMERATOR></COMPU-RATIONAL-COEFFS><LOWER-LIMIT>-40</LOWER-LIMIT><UPPER-LIMIT>215</UPPER-LIMIT></COMPU-SCALE></COMPU-SCALES></COMPU-INTERNAL-TO-PHYS></COMPU-METHOD>
    <UNIT-REF ID-REF="UNIT_C" />
  </DATA-OBJECT-PROP>
  <UNIT ID="UNIT_C"><SHORT-NAME>degC</SHORT-NAME><DISPLAY-NAME>degC</DISPLAY-NAME></UNIT>
  <DIAG-SERVICE>
    <SHORT-NAME>Read_EngineCoolantTemperature</SHORT-NAME><LONG-NAME>Engine coolant temperature</LONG-NAME>
    <REQUEST-REF ID-REF="REQ_F405" /><POS-RESPONSE-REF ID-REF="RESP_F405" />
  </DIAG-SERVICE>
  <COMPARAM><SHORT-NAME>CP_REQUEST_ID</SHORT-NAME><VALUE>2016</VALUE></COMPARAM>
  <COMPARAM><SHORT-NAME>CP_RESPONSE_ID</SHORT-NAME><VALUE>2024</VALUE></COMPARAM>
  <DTC><SHORT-NAME>SensorPlausibility</SHORT-NAME><TROUBLE-CODE>1193046</TROUBLE-CODE><DISPLAY-TROUBLE-CODE>Sensor plausibility</DISPLAY-TROUBLE-CODE></DTC>
</ODX>
""";
}
