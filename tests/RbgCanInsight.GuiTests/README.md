# Windows FlaUI test suite — RBG CAN Insight

This is an **opt-in, real-process, UI Automation 3 suite** using FlaUI 5,
.NET 10 and xUnit. It is separate from the headless unit tests, so normal
dotnet test on RbgCanInsight.sln does **not** launch a graphical process.
There is **no GitHub Actions workflow**. Use an interactive, **unlocked**
Windows desktop session, with .NET 10 Desktop SDK and a working display.

Run from the repository root on the Windows development PC:

    dotnet build RbgCanInsight.sln -c Release
    dotnet test tests\RbgCanInsight.GuiTests\RbgCanInsight.GuiTests.csproj -c Release --logger "console;verbosity=normal"

Override app executable only when needed:

    $env:RBG_CAN_INSIGHT_GUI_APP_PATH = "C:\path\to\RbgCanInsight.exe"

The harness launches the real RbgCanInsight.exe for each scenario, creates
an isolated temporary config root using RBG_CAN_INSIGHT_GUI_TEST_ROOT, and
cleans up test processes and fixtures. The production application ignores
this setting unless the environment variable is explicitly present. Never
run UI tests in Session 0, through a locked RDP session, or alongside other
desktop automation that may steal keyboard focus. File picker tests use
English Windows Ctrl+A and Alt+N fallback and may require adjustment for
localized picker templates; UIA IDs take precedence.

## Covered external-user workflows

- All 10 workspace navigation choices and their primary controls.
- Search-everywhere UI, query-builder add/reset/close, first-run tour, high
  contrast theme and direct UDS investigation navigation.
- Real file-picker import of synthetic ASC and DBC, Trace filter/reset.
- DBC definition XLSX export, including Bit Positions, Bit Layout Issues
  and Raw Physical Limits sheets introduced in the latest increments.
- Synthetic ODX import, engineering PAR/Sub-PAR and coverage XLSX export.
- DID-to-CAN mapping, saving a JSON snapshot and tabular CSV export.
- Assertions that source DBC and ODX fixtures are unchanged.

These are **source-authored tests and not certified results**. Real OEM
variant coverage, invalid-file recovery, all menu branches, all DBC editing
interactions, 13-inch/high-DPI acceptance, full color contrast, large traces,
numerical accuracy, and physical Windows file-dialog compatibility require
further PC validation. Existing in-process GuiVerify and domain xUnit suites
continue to cover complementary behavior. Review failures locally before
declaring any workflow complete or shipping a release.


## Additional advanced scenarios

The separate AdvancedGuiWorkflows suite checks actual DBC working-copy staging
and Save As without changing the source DBC; Trace health's secondary window;
read-only saved-map version comparison from two synthetic snapshots; and the
presence of Engineering PAR export controls after resizing the main window to
1280x720. These four cases are particularly desktop/UIA-provider-sensitive,
and are also PENDING Windows execution. Failures on a laptop at larger display
scaling must be investigated as genuine UI issues, not treated as automatic
test skips.

The tests await source-loaded status (one DBC, and the synthetic ODX DID
catalog) and the end of async correlation before invoking Save/Export. Closed
secondary windows are polled by live UIA top-level enumeration rather than
reading properties from stale/disposed UIA peers.

Additional real native-dialog tests cover DBC version-diff XLSX, CDD/ODX
version-diff XLSX, exported ASC traces and saved-map comparison XLSX.
They check source immutability; interpretation of real OEM variant data is
still outside the synthetic test boundary.

The synthetic CDD fixture comes from the nested-PAR xUnit regression and
exercises actual source-backed EngineeringPackage → VehicleSpeed / ModeGroup →
ModeFlag export. A further full workflow imports trace+DBC and asserts populated
Signals, Change Matrix, Byte Matrix, Statistics and UDS transaction grids.

Sequential file pickers (baseline/current comparisons) are synchronized by
the departing dialog's exact title rather than waiting for every native dialog
to disappear; this avoids a race when the second picker opens immediately.

## Additional export-content gates (2026-11-05)

`GuiExportContentWorkflows` extends the real-process FlaUI tests beyond
workbook sheet presence. It imports a synthetic DBC through the native
file picker, exports a real workbook and verifies Motorola @0 bit order,
raw-bit significance, 64-bit raw bounds as exact text, enum meanings,
and source immutability. It also exports a correlated mapping CSV and
checks UTF-8 BOM, quoted headers, fingerprint columns and original
fixture integrity. This suite has **NOT RUN** on Windows; a green
test result is required before accepting the corresponding features.


## Expanded GUI regression inventory (2026-11-05)

The test project now defines **116 xUnit/FlaUI test cases in source** (77 declared
test methods: 68 `[Fact]` and 9 parameterized `[Theory]` methods with 48 data rows).
The earlier 71-case expansion is retained, with added source-defined coverage
for DBC Undo/Redo, symbolic states, source integrity, network-matrix ambiguity,
signal triggers and locally saved rules. These counts reflect source annotations,
**not successful Windows test discovery or execution**. The MainWindow now
declares 182 unique explicit Automation IDs (no duplicates found in source).

| Area | Real UI actions exercised |
| --- | --- |
| Navigation and accessibility | All 10 workspaces; all 12 UDS views; quick-view navigation; Dark/Light/High Contrast; 1280x720 window; reopened tour and global search |
| Search and filters | Trace, DBC, Signals, Change Matrix, Byte Matrix, Statistics, Mapping, UDS transactions and diagnostic-definition search; clear/reset |
| Trace and UDS | Synthetic ASC import, recorded rows, UDS transactions, transport head/tail/step, go-to-time, marker navigation, trace export and health dialog |
| DBC | Source import, signal inspector, bit-layout tab, stage/discard/save-copy workflow, catalog search, read-only file version comparison, comparison export, XLSX content including Motorola bits, unsigned raw limits and value meanings |
| CDD/ODX and engineering parameters | Synthetic nested-PAR catalog import, field inspector, Sub-PAR search, Engineering export, general diagnostic catalog export, read-only diagnostic version comparison |
| DID ↔ CAN | Source-backed offline auto-correlation, map JSON save/reopen, CSV export, mapping version diff/export, source immutability checks |
| Investigation and utilities | Four pane arrangements, named layout save/delete, built-in layout protection, session save/reopen, workspace preset export, UI layout round-trip, ZIP report export, column chooser, window reset and About dialog |
| DBC working-copy history | Stage scalar fields; Undo/Redo with redo-branch invalidation; source unchanged; domain tests also cover VAL_ and 128-step history |
| Named-state timeline | DBC symbolic state transitions, source frame navigation, search and uncertainty reporting |
| DBC network matrix | Declared TX/RX versus observed CAN identities, ambiguous multi-profile matches, definition navigation and ZIP export |
| Offline signal triggers | Configured threshold/named-state predicates, source before/after frames, report ZIP and named local Save/Load |

Run the full suite **locally on an unlocked interactive Windows PC**:

    .\tools\run-flaui.ps1 -Configuration Release

Run a targeted class or method using a VSTest filter:

    .\tools\run-flaui.ps1 -Filter "FullyQualifiedName~GuiWorkflowRegressionTests"

The script builds the opt-in GUI project and writes a TRX under
`artifacts\flaui`. It adds no GitHub Actions/CI workflows. The regular
solution-based domain tests remain separate from interactive FlaUI tests.

### Acceptance status and remaining coverage

**SOURCE IMPLEMENTED — WINDOWS NOT VERIFIED.** The environment used to author
these commits cannot launch WPF, operate the Windows common file picker, run
FlaUI/UIA3, or certify the test results. A test count is not code-coverage
percentage. Run the suite and fix genuine failures on a Windows desktop before
marking the relevant features verified. In particular, independently check:
native dialog language/DPI differences; keyboard-only focus and shortcuts;
1024x640 minimum size and 125%/150% scaling; long multi-file/large-trace
performance; corrupt and malicious inputs; all individual popup/menu branches;
multi-channel BLF/MF4 imports; and non-synthetic automotive datasets where
legally permitted. Complement these UI tests with the existing xUnit parser,
trace, regression and export tests. Do not ship a release based solely on
source-authored FlaUI tests.

### Latest source-only acceptance additions (2026-11-05)

- `GuiDbcUndoRedoTests`: 3 WPF/UIA3 cases; `DbcUndoRedoTests` exercises scalar and
  VAL_ stage history, branch invalidation, exact frame format and safe discard.
- `GuiNamedStateTransitionTests`: 3 cases for observed named transitions and
  source frame navigation.
- `GuiNetworkMatrixTests`: 4 cases for DBC filtering, ambiguous claims and ZIP export.
- `GuiSignalTriggerTests`: 5 cases for threshold/state triggers, invalidation, original trace
  navigation, ZIP completeness and local saved-rule reload.
- `SignalTriggerAnalyzerTests` and `SignalTriggerPresetStoreTests`: domain tests
  cover predicates, uncertainty, unknown codes, cancellation, invalid files,
  source isolation and atomic local persistence.

**All these tests are still unexecuted in the remote environment.** Build and
run the domain tests and opt-in FlaUI tests on an unlocked Windows PC before
marking their behavior verified or shipping a release.

### Event-sequence source-only UIA3 additions

`GuiSignalSequenceTests` introduces four UI cases: source-qualified A/B
event selection, chronological matching, context-frame navigation,
rule-change invalidation, re-navigation and ZIP source-frame evidence.
The test code has not been executed on Windows in this session.

### Exact DBC raw-value acceptance (2026-11-05)

`GuiDbcValueExplorerTests` adds four true-process WPF/UIA3 scenarios: on-demand raw-code histogram, first/last source-frame links, stale-result invalidation on signal change, no invented observations when trace absent, and ZIP exported raw-code completeness metadata. Domain assertions in `DbcValueExplorerAnalyzerTests` independently cover exact UInt64/Int64 and Motorola reconstruction. **All are unexecuted on Windows in this session.**
