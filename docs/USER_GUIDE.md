# User Guide

RBG CAN Insight is designed around a simple offline workflow: load a trace, load one or more DBC files, inspect decoded signals, analyze changes, then optionally correlate diagnostic DIDs to CAN signals.

## 0. First Launch — Interactive Tour

The first time the app starts it opens a short interactive tour (in English) that walks
through every workspace with small worked examples. Each step switches the main window to
the workspace being described, so you can try things while reading. The tour is fully
skippable (**Skip tour** or **Esc**) and never reappears on its own; reopen it with
**Help & quick tour** in the navigation rail or by pressing **F1**.

## 1. Load a Trace

Use **Open trace...** or drag a supported trace file into the window.

Supported trace inputs include ASC, BLF, TRC, CSV, LOG/TXT, MDF4/MF4 and SocketCAN
captures in PCAP and PCAPNG format (imported from 4.2.0). A PCAN-View TRC 2.x file with a
`;$COLUMNS=` header is read by column (fixed in 4.1.0).

MF4 files are read as a stream (DT, DL, HL and zlib DZ blocks), so there is no file-size limit any more;
only single blocks are held in memory. The decoded frames themselves are still loaded like any other
trace. Verified on ten third-party logger MF4 files and on synthetic multi-block, compressed and
transposed-compressed files, not on files above 256 MB (that size is covered by a small-budget
equivalent test). Signal-only MF4 files (no raw CAN) are still rejected.

**Several logs at once:** select more than one file in **Open trace...** and choose a merge mode:
*By time* (interleave, channels kept), *Sequential* (append; a file that starts before the previous one
ended is shifted to start where it ended) or *Separate channels* (interleave, each file gets its own
channel range). The session is held fully in memory, has no single-file fingerprint, is not added to
the recent list, and a saved project records only its first file.

After import, the Trace workspace shows frame count, timestamp, channel, CAN ID, payload, message name and decoded signal summary.

## 2. Load DBC Files

Use **Load DBC...** to add one or more CAN databases. DBC and AUTOSAR ARXML files are
accepted; ARXML import brings in CAN frames, signal mappings, comments, signal groups,
units and common linear scaling metadata when available.

When a DBC is loaded, RBG CAN Insight decodes matching frames and rebuilds:

- decoded trace rows
- signal timelines
- plot series
- Change Matrix rows
- DID-CAN candidate context

## 3. Search and Filter

The Trace workspace has a single search box with two explicit behaviours:

- **Enter** applies the text as a row filter (hides non-matching rows)
- **F3 / Shift+F3** (or the ▲▼ buttons) jump between matching rows without hiding anything
- **Esc** (or ✕) clears both

Search terms: CAN ID (`100`, `18FEF100`), channel (`ch1`), message name, `payload:AABB`.
Space-separated terms are combined with AND.

Richer terms (offline equivalents of Vector's search/trigger conditions; all hex unless noted):

| Term | Meaning |
| --- | --- |
| `A OR B`, `A B OR C` | `OR` (or `|`) starts an alternative: `A B OR C` means (A and B) or C |
| `100\|200` | alternatives inside one term; `-100\|200` excludes both |
| `id:100..1FF`, `id>=100`, `id<7FF`, `id!=100` | CAN ID range / relation |
| `d0=5A`, `d1>=10`, `d2!=FF` | data byte relation (`d<index><op><hex byte>`; ops `= == != < <= > >=`) |
| `d0&0F=05` | masked byte: `(byte & mask) == value` |
| `d0=b1xxxxxx0` | bit pattern, `x` = don't care (`=` and `!=` only) |
| `dlc=8`, `dlc>=4` | payload length in bytes (decimal) |
| `t:1.5..3`, `t>=2`, `t:..5` | time window in seconds from the first frame |
| `dir:tx`, `dir:rx` | logged as transmitted / not logged as transmitted (see below) |

A byte relation never matches a frame that is too short to contain that byte. A malformed
advanced term (for example `d0=ZZ`) matches nothing instead of silently meaning something else.
The **Query builder** offers the same fields, an **OR** tick per condition, and validates values.

### Run to match (break conditions) and replay keys

The Trace **Replay** bar and these keys (Trace workspace) follow Vector's offline mode:

- **F7** step one frame forward (**Shift+F7** back), **F8** animate (about 3 frames per second),
  **F9** run, **Esc** break (pauses replay or stops a running search; otherwise Esc still clears the search box).
- **Run to match** button / **Ctrl+F9** (**Ctrl+Shift+F9** backwards): replays from the current position and stops
  on the first frame that matches the condition typed in the search box (any term above, including `OR`, byte
  masks and time windows). If the box is empty, the last applied filter is used. The search runs in the background
  and Esc stops it.

Limit: the break condition is a frame condition. Signal-value triggers (crossing a threshold, sequences) remain in
the Signals/Trigger tools and are not yet usable as a replay break condition.

### Row colours from saved queries

In the Query builder, pick **Row colour in Trace** before **Save current query**. Trace rows matching a saved query
that has a colour are tinted; when several match, the first in the (alphabetical) saved list wins. The colour is
evaluated only for rows that are actually shown, so it does not slow down loading or filtering. A colour tint replaces the
changed-payload row highlight on matching rows; marker underlines still show.

### Tx / Rx direction

Frames logged as transmitted (ASC `Tx`, BLF direction flag, TRC `Tx`) show **Tx** in the **Dir** column and match
`dir:tx`. A blank means received **or direction not recorded** (CSV, candump, MF4, pcap and many logs carry no
direction), so `dir:rx` means "not logged as Tx", not "proven Rx". ASC, TRC and BLF export keep the Tx flag.

### Compare marked frames (Difference view)

Press **M** on two or more Trace rows (or mark one and select another), then right-click and choose
**Compare marked frames (difference)**. The window lists, for up to 12 frames in trace order, the frame properties,
every payload byte (differing bytes are highlighted with the differing bit numbers) and every decoded signal with
min / max / mean over exactly those frames. It is a read-only snapshot of those frames.

The **View** selector controls how rows are listed: *All frames*, *Latest per ID*
(one row per bus/ID, oscilloscope-style) or *Changes only* (rows whose payload differs
from the previous same-ID frame).

Right-click a row for quick actions: filter by its ID, plot all signals of its message,
toggle a marker.

### Trace row buttons

Each decoded row has two buttons in its **Plot** column:

- **↗** (*Quick plot decoded signal of this message*) opens a separate graph window for the
  message's decoded signal. If the frame decodes to several signals you are asked to pick one.
  The main chart is not changed.
- **＋** (*Add decoded signals of this message to the chart*) adds every decoded signal of that
  message to the main Signals & Plot chart and keeps you on the Trace page.

### Diagnostic (UDS) column

Frames that carry diagnostic traffic are interpreted automatically in the **UDS** column:
ISO-TP framing (single/first/consecutive/flow-control) plus the UDS service inside —
session control, ECU reset, security access, routine control, tester present, and
Read/WriteDataByIdentifier with the DID spelled out, e.g.
`UDS Req ReadDataByIdentifier — DID 0xF190 (VIN)` or
`UDS Neg ✗ ReadDataByIdentifier → requestOutOfRange`. When a CDD/ODX file is loaded in the
Mapping workspace, DID numbers are resolved to their catalog parameter names, so you can
see exactly which diagnostic value the tester was reading. Frames on non-diagnostic IDs
are only labelled when they are unambiguously well-formed UDS, so normal signal traffic is
never mislabelled.

### UDS Analysis workspace

Open **UDS Analysis** to work with complete recorded diagnostic evidence instead of individual
CAN frames. This workspace is strictly offline and read-only: it cannot send a request, clear
a DTC, start/stop a routine, change session, provide a security key or transmit any CAN frame.
Words such as *Start*, *Stop* and *Clear* describe operations found in the recording.

The selector at the top switches between seven analysis views.

#### Overview

Overview counts requests and positive, negative or missing responses per UDS service. Average
response time is calculated from terminal replies. The summary also reports physical DID
samples, reconstructed DTCs and routines, and strong **unreviewed correlation candidates**
supported by numeric trace evidence. High statistical fit or matching units alone cannot
establish that a CAN signal is the same physical quantity as a recorded DID field.
The table shows a conservative **Association** indicator (at most 50% for automatic
matches); the raw statistical fit score remains available separately in CSV/JSON
reports. `StrongCandidate` means statistical agreement, **not** analyst confirmation.
The separate DID-CAN Mapping workspace provides an explicit manual review decision.

Address quality is always explicit:

- **Confirmed** — request/response IDs came from CDD/ODX connection parameters;
- **Inferred** — standard 11-bit or 29-bit ISO addressing was recognized;
- **Unknown** — service/timing evidence suggests direction, but the endpoint cannot be proven.

#### Transactions

The ISO-TP reassembler supports classical CAN and CAN FD single frames, multi-frame payloads,
extended first-frame lengths, sequence validation and flow-control references. Requests and
responses are paired by channel, endpoint, service and time. `responsePending` (`NRC 0x78`)
replies stay inside the transaction until a terminal reply is found.

Use the filter to isolate problems, DTC operations, routines, negative responses or missing
responses. The lower panel contains complete request/response payloads, decoded CDD/ODX values,
transport quality and every contributing frame number. **Open raw frames** returns to Trace and
selects the first source frame.

Interpret outcomes as follows:

- **Positive** — a terminal positive response was recorded;
- **Negative** — a terminal `0x7F` reply was recorded; the NRC is translated;
- **No response** — no terminal response was found inside the pairing window;
- **Response suppressed** — the request explicitly set suppressPositiveResponse;
- **Malformed** — ISO-TP was incomplete or its sequence was invalid;
- **Orphan response** — a response exists but its request was not present in the trace.

#### DID samples

Positive `ReadDataByIdentifier` responses become timestamped samples. When CDD/ODX metadata is
available, the decoder applies bit offsets, signedness, factor/offset, units and enumerations.
Multi-DID responses and structured records produce one series per decoded field. The table shows
sample count, first/last time and physical range; every sample retains its response frame index.

Text-only values (for example an identification string) remain available for inspection and
reporting but are not sent to the numeric UDS↔CAN correlator.

#### Fault memory

Fault Memory groups all recorded `ReadDTCInformation` observations into one lifecycle per DTC:

- catalog name and description;
- first and last observation;
- status-byte transitions (`testFailed`, `pending`, `confirmed`, etc.);
- snapshot identifiers and recorded snapshot records, decoded with the loaded catalog when it defines the
  DIDs (see "Snapshot and extended-data records" below);
- extended-data records and fault-detection counters;
- occurrence count;
- a later successful `ClearDiagnosticInformation` event, when present.

Select a DTC to inspect its chronological evidence. **Cleared** means a successful clear response
was found after the DTC observation; it does not claim that a later re-read proved the fault absent.
That distinction prevents an offline trace from implying more than it actually contains.

**Snapshot and extended-data records.** `ReadDTCInformation` sub-functions 0x04, 0x05, 0x06 and 0x10 are
decoded record by record. A snapshot record (`record number, identifier count, {DID, data}*`) is split
with the DID widths declared in the loaded CDD/ODX and every DID is decoded like a normal 0x22 value
(name, unit, physical value). Extended-data records have no length field, so they are decoded only
when the request named one record number (or the catalog's DTC definition covers the whole payload);
otherwise the bytes stay raw and the note says that record boundaries are unresolved. A DID without a
declared width, an unknown DID or a conflicting definition also stays raw with a note; nothing is guessed.
DTC-level snapshot/extended field layouts of a catalog are applied to the bytes after the record number
(the CDD/ODX readers of this version do not yet fill these layouts, so extended data from a real CDD is
usually shown raw). The decoded text appears in the DTC detail and in the "Active recorded DTC" finding.
`rbgcan report faults` writes the **fault-memory table** (one row per ECU and DTC with the eight
status-bit columns of the latest status, first/last seen, occurrences, cleared flag, latest snapshot and
extended-data text and source frames).

**Timing findings.** The Findings tab also lists named timing rules, each with its source frames
(double-click opens the first frame): `TIM-001` P2 response time, `TIM-002` P2*, `TIM-003`/`TIM-004`
NRC 0x78 chains (long chain, chain without final response), `TIM-005` S3 gap and `TIM-006` interrupted
TesterPresent stream while a non-default session was recorded, `TIM-007` N_Bs, `TIM-008` N_Cr,
`TIM-009` STmin violation and `TIM-010` flow-control overflow. Limits are the ISO defaults (P2 50 ms,
P2* 5000 ms, S3 5000 ms, N_Bs/N_Cr 1000 ms) and are replaced per ECU by the P2/P2* a 0x10 positive
response announces (bytes 2..5). Limits can be changed in `rbgcan report timing`. S3 is only judged
after a non-default session was observed (the state before the first 0x10 response is unknown).
N_As/N_Ar are transmit-confirmation timers inside the node and cannot be seen in a bus log, so they are
not evaluated. A late frame in a log can also be a logger artefact: findings are evidence to check, not proof of an ECU defect.

**Authentication, timing and secured-data services.** UDS 0x29 (Authentication), 0x83
(AccessTimingParameter) and 0x84 (SecuredDataTransmission) are named and their structure decoded
(sub-function, return parameter, P2/P2* records; certificate, challenge and signature payloads are only
counted, never interpreted). The negative-response table covers the complete ISO 14229-1 set, with
labelled ranges for reserved and manufacturer-specific codes.

#### Routines

Routines reconstruct `RoutineControl` operations already present in the trace. Steps are grouped
by ECU and routine identifier:

1. recorded `StartRoutine`, including input bytes;
2. any `responsePending` replies;
3. recorded `StopRoutine` or `RequestRoutineResults`;
4. terminal positive result or negative-response code.

The result shows duration, catalog name, input/result bytes, pending count, state and source frames.
`Incomplete` means the recording ended or the terminal step was missing. There are intentionally
no controls for executing a routine.

#### UDS ↔ CAN correlation

This view validates physical DID samples against decoded DBC signal timelines. Names, comments,
unit, range, scaling and bit length define the candidate shortlist; the verdict is then driven by
recorded values. For every candidate the engine searches timing lag and calculates:

- paired sample count and coverage;
- mean timestamp alignment error;
- Pearson correlation and coefficient of determination (R²);
- fitted scale and offset (`CAN = slope × DID + intercept`);
- mean absolute error (MAE) and root mean square error (RMSE).

The normalized overlay draws UDS and CAN series together. Read verdicts conservatively:

- **Confirmed** — at least five independent DID/CAN source observations, sufficient coverage and strong combined evidence; the minimum cannot be lowered by a saved preset;
- **Candidate** — useful evidence exists but still needs engineering review;
- **Rejected** — aligned values contradict the proposed mapping;
- **InsufficientData** — too few physical samples or no suitable time overlap.

Constant look-alike signals are not considered proof. Periodic waveforms can also
produce several nearly equivalent lag hypotheses: when physically distinct delays fit
almost equally well, the verdict remains **Candidate** rather than claiming a unique
Confirmed delay. The evidence summary displays an explicit **Multiple plausible lags**
warning, separate from the detailed fit explanation. **Different CAN signals that
explain the same observed DID field almost equally well are not independently
Confirmed either:** the summary flags competing signals and the explanation names
the alternative DBC signals for manual identity verification. Duplicate DID readings at one timestamp count as one temporal
observation when they agree, while contradictory readings at that instant are not
fitted. Contradictory simultaneous CAN readings block fallback to a nearby unrelated
value. The explanation states the metrics and all metadata evidence so a result is
auditable rather than an unexplained percentage.

For multi-frame UDS responses, the **source-frame evidence** includes the original
ISO-TP response segment indices in addition to the existing DID and CAN source
indices; request and flow-control frames are not represented as decoded DID data.
The DID reference points to the final physical frame of the completed positive
response (not the last aggregate transaction frame). Incomplete or unattributed
ISO-TP replies are excluded from numeric DID samples. When replaying the
evidence, inspect all response segments rather than treating just the final frame
as a complete diagnostic payload.

**Structured multi-field DIDs:** each decoded field is correlated independently. If the
CDD/ODX declares temperature and pressure under one DID, the shortlist uses each
field's own name, physical unit, range, factor, offset and bit length rather than
the parent DID's generic properties. A field with no unique source definition is
not silently mapped to another field. Physical CAN channel and diagnostic ECU are
also kept distinct; coincidentally matching values from a different ECU, incompatible
units, or unverified wide (over 53-bit) CAN integer timelines cannot confirm a mapping.

**Source timing quality:** imported CAN signal timelines are time-ordered after
decoding while preserving their original Trace frame indices. When multiple CAN
frames at one timestamp disagree, that instant is ambiguous, not proof of any
particular signal value. NaN/infinity and invalid timestamp/lag configurations are
excluded from numeric fits without changing source frames. The lag grid always
evaluates exact **0 ms** and both configured limits, even when the step does
not evenly divide the search range. Values marked as a malformed UDS
transport or an explicitly unknown endpoint cannot establish a mapping. If two different
loaded DBC definitions have the same name and collide on the same CAN
channel/identifier/signal, no numeric timeline is silently merged. Use distinct
DBC names or explicit bus assignments to resolve the conflict.

The **correlation results** table now distinguishes the observed DID field and,
for insufficient evidence, displays the actual rejection reason instead of only
"No paired values". Select a result to inspect the original **zero-based DID
and CAN frame indices** supporting each paired observation; the evidence
panel scrolls on compact displays. Index values refer to imported Trace frames,
not row numbers after sorting or filtering. A repeatedly decoded DID from the
same source frame cannot count as independent diagnostic evidence.

**Timestamp pairing details:** if two equidistant CAN readings disagree, the
engine does not arbitrarily choose either one. Simultaneous contradictory CAN
readings block a nearby unrelated frame from being used as a substitute.
The lag search accepts sub-millisecond steps down to 1 ns while limiting the
number of hypotheses for performance; unreasonable or non-finite lag settings
produce an explicit insufficient-evidence result. These safeguards never
modify the source BLF/ASC trace.

#### Transport

Transport lists incomplete ISO-TP assemblies, invalid sequence numbers and other quality findings.
Use transaction frame references to inspect the raw evidence and distinguish a real ECU behavior
from a truncated recording.

Open the collapsible **Bookmark rules** panel (the ▸/▾ arrow next to its title) to mark
frames automatically. Each rule is its own row: a checkbox to enable or skip it, an editable
text field for the rule itself, and a ✕ button to remove it. Use **+ Add rule** to add another
row. Supported offline rule text includes:

- `id:18FEF100`
- `payload:62F190`
- `VehicleSpeed > 20`
- `Message.Signal <= 10`

Rule matches become normal markers, so the marker previous/next controls navigate them.
Name the current rows, then use **Save** to keep them as a reusable offline rule set; pick
one from **Saved sets** to load it into the rows. If you have unsaved edits and want to
discard them, use the ↺ button next to **Saved sets** to reload the selected set's rules
into the rows. The **All on** and **All off** buttons toggle every row's checkbox at once.

Use **Columns...** from the side rail to show or hide columns in the active table view.
Column visibility, order and width are saved with the local application settings.
Use **Export layout** and **Import** to move theme, active view and table column layouts
between workstations or portable folders.

### Analysis tools (Trace page)

The **Analysis tools** expander on the Trace page is collapsed by default; open it to show the
buttons below. Each analysis opens an evidence table, with source frame numbers, in its own
window, and the tables can be exported as CSV. None of these tools transmits frames or queries
an ECU; they only read the loaded trace.

- **J1939 messages…** reassembles multi-packet J1939 messages (BAM and RTS/CTS), lists DM1/DM2
  fault codes and address claims, and gives their source frames. A 29-bit reading is one possible
  interpretation of the identifier, not proof of the network protocol.
- **XCP / CCP…** decodes XCP-on-CAN (classic CAN and CAN-FD) and CCP 2.1 calibration traffic.
  These protocols have no fixed CAN identifiers, so the tool looks for CONNECT handshakes (a
  master frame answered by a positive response within 200 ms) and lists each candidate identifier
  pair as a *Detection* row with a confidence value (at most 0.90) and its evidence frames; pairs
  with confidence 0.60 or more are decoded. Each command is paired with its response (XCP
  RES/ERR/EV/SERV, CCP CRM matched by command counter) with error-code names, latency, unanswered
  commands and an error summary; DAQ packets are grouped per PID and their payloads are not
  interpreted. Multi-byte XCP fields are shown as numbers only when a CONNECT response in the
  trace announces the byte order. The CLI equivalent is `rbgcan report xcp` (optionally with
  `--cmd-id`, `--res-id`, `--protocol`, `--daq-ids` to skip detection). Verified on hand-built
  frames only, not against a reference tool or real ECU logs.
- **OBD-II…** decodes OBD-II requests and responses (modes 01 to 0A) into PID values, DTCs and
  the VIN where present.
- **Counters & checksums…** looks at undocumented identifiers and lists *candidate* rolling
  counters and checksum/CRC bytes. Results are evidence to review, never a signal definition.
  Detection in this release covers Intel bit order and one-byte checksums only.
- **Bus load timeline…** shows per-second bus load for each channel. It uses the bitrate you type
  for that channel in **Statistics** (`bus load @ … kbit/s`). The bitrate is never detected from the
  file, so the result is an upper estimate that is only as good as that input. If an ASC file contains
  recorder-written `Statistic:` / `Status:` lines, the report adds *Logged load %*, *Logged controller
  state* and a *Load source* column (`logged` when the recorder wrote a value for that second,
  otherwise `estimated`), and the Statistics page shows the mean logged load next to the estimate.
  Logged values are the recorder's own figures; they are shown side by side and never replace the estimate.
- **Suggest best DBC…** ranks the loaded DBCs per channel by how well each explains this trace.
  It is a ranking aid: a high rank does not prove that the database matches the vehicle.
- **DBC coverage…** compares the trace with each loaded DBC: CAN IDs the DBC does not know (with frame
  and byte counts), payload bytes that no signal covers or that are missing because a frame is shorter than the
  declared length, and signals whose observed values leave their declared min/max. It reports what the
  files say; a "bytes not covered" entry can simply be a reserved field.
- **LIN frames… / LIN timing… / LIN diagnostics… / LIN signals (LDF)…** work on LIN records found
  in the trace. They are filled from **BLF** files (LIN_MESSAGE, LIN_MESSAGE2, CRC/send/receive
  errors, wake-up, sleep). Frames shows PID parity and whether the recorded checksum is classic or
  enhanced (diagnostic frames 0x3C/0x3D must be classic); Timing gives period and jitter per ID;
  Diagnostics reassembles node-configuration and UDS-on-LIN messages (single, first and
  consecutive frames) from 0x3C/0x3D; Signals asks for a LIN Description File (.ldf) and decodes
  its signals and encodings. Each row names its LIN frame number.
- **FlexRay matrix…** shows, per slot, the cycles seen, null frames, recorder-flagged error frames
  and inferred missing slots. The schedule is not known, so the repetition is inferred from the
  cycles actually seen; header/frame CRCs are not recomputed. FlexRay is read from BLF only.
- **CAN XL frames…** lists CAN XL frames (priority ID, VCID, SDT, SEC, RRS, acceptance field,
  payload). They are read from candump `-L` logs (`.log`/`.txt`) and SocketCAN pcap/pcapng captures;
  payloads must be 1..2048 bytes and invalid records are rejected and counted in the import health.
  The SDT is shown raw; a name appears only for 0x01 (content-based addressing) and 0x03 (CAN CC / CAN FD
  tunneling). CAN XL in BLF, ASC, MF4 and TRC files is counted as unsupported, not decoded.
- The import summary reports counts such as "1,204 LIN frames, 300 FlexRay frames imported". ASC
  LIN lines of the Vector converter form (`time Li id Rx|Tx len data... checksum = XX`, no channel
  number, so channel 1) are decoded; other ASC LIN, FlexRay and CAN XL lines are counted as
  unsupported, not decoded. BLF LIN_*2 error objects (types 58-62) are also counted as unsupported. Command line:
  `rbgcan report lin <trace> <out.csv> [--ldf f.ldf]`, `rbgcan report flexray <trace> <out.csv>` and
  `rbgcan report canxl <trace> <out.csv>`.
- Verification status: LIN_MESSAGE2/sleep BLF and the ASC `Li` form were cross-checked against a
  few small Vector-converter sample files (same frame in BLF and ASC); the other LIN objects and
  all FlexRay and CAN XL paths are tested on hand-built data only (no real FlexRay log, no real CAN XL capture).
- **E2E check…** checks the AUTOSAR E2E counter and CRC (profiles 1, 2 and 5) of one CAN ID.
  Enter the Data ID (hex), the expected cycle time, the CRC byte index and the counter start bit,
  or press **Apply detected settings** after a check suggests them. The verdict covers the counter,
  the CRC, lost or repeated frames and gaps. **Only problems** filters the per-frame table and
  **Export CSV…** saves it. E2E was verified on hand-built frames only, with CRC check values
  confirmed against an independent reference; compare results with your own E2E configuration.
- **Trace tools…** cuts a time range, shifts time, splits at gaps or remaps channels, then
  **Save as…** writes ASC, CSV, TRC, BLF or MF4. The original file is never modified. MF4 cannot
  hold every frame; frames it cannot represent are counted and reported.
- **Merge traces…** merges several trace files into a new file using the chosen mode (by time,
  sequential, separate channels). The sources are not modified.
- **Notes…** attaches analyst notes to a frame or time range (**Use selected Trace frame** fills
  the range). Notes are saved with the project and exported with **Export CSV…** or
  **Export Markdown…** (see section 7).

The same expander also holds trace import health, the A/B measure and the rule bookmarks described
above.

### Customize the navigation rail and small-screen layout

Drag a workspace button in the left rail to place it above or below another workspace. The
custom order is remembered between launches. This changes only visual order: saved sessions,
navigation commands and workspace identities remain stable.

The **Session & tools** expander contains Save/Load session, presets, report export, layouts,
theme, Quick tour and About. It starts collapsed and remembers its state, preventing occasional
commands from taking permanent vertical space on a small display.

In Trace, drag the horizontal splitter immediately below the density overview to resize the
overview from a compact strip to a taller timing graph. Its height is saved locally. The raw
Trace table keeps a minimum usable height, so enlarging the overview cannot hide the frames.

## 3b. DBC Manager

The DBC Manager is a master-detail view:

- select a **database** on the left to scope the message table;
- select a **message** to scope the signal table;
- clear the selection to see everything.

Per database you can set:

- **Match by J1939 PGN** — matches frames by Parameter Group Number so the same PG sent by
  different source addresses still decodes. Enable it for J1939 databases.
- **Bus assignment** — restrict a DBC to one channel so multi-bus traces are not mis-decoded.

The *Unmatched trace IDs* panel lists every trace identifier no DBC matched, with a hint
explaining why (e.g. "same J1939 PGN — enable Match by PGN", or "excluded by channel
assignment"). DBCs with parser warnings show a ⚠ badge; hover it to read what was skipped.

### DBC consistency check

Open the DBC inspector (pop-out) and choose the **Consistency check** tab, then **Run consistency check**.
Every finding has a rule ID (`DBC-001` ... `DBC-074`), a severity (Error, Warning, Review), the message and
signal concerned and a detail text; **Export CSV** saves the list. Rules cover duplicate message IDs and names,
signal overlap, signals beyond the message length, physical min/max that the bit width cannot encode,
value descriptions out of range, undefined or unused nodes, attribute definitions/defaults/values,
multiplexing (including extended `SG_MUL_VAL_`) and J1939 consistency.
Rules that need the original file (nodes, attributes, orphan `VAL_` lines) run only when the DBC is still
readable at its original path; the summary line states how many rules were skipped. The check is
definition-only: it says nothing about what an ECU really sends, and "Review" items are hints, not defects.
The window runs without trace data, so the cycle-time-versus-observed rule (`DBC-051`) is skipped there; it is
available in the library.

### Catalog consistency check (CDD / ODX / PDX)

On the UDS page choose **Catalog check**. The window runs the documented `CAT-001`..`CAT-026` rules over
all loaded diagnostic descriptions and lists findings with severity (Error, Warning, Review), ECU, object
and detail; **Export CSV** saves them. Rules cover duplicate DIDs, DTCs, routines and services per ECU,
DIDs without length or type, overlapping fields or fields beyond the DID length, invalid bit positions,
minimum above maximum, zero scaling factor, inconsistent value tables, DTCs without text or with numbers
above 24 bits, guessed service IDs, services lacking a session/security pre-condition when others declare
one, conditions that name undefined services, impossible CAN identifiers of diagnostic connections,
unresolved or ambiguous type references and fields without physical conversion. Like the DBC check
it reviews the description only. It works on what the readers retained, is **not** an ODX XSD validation
and was not compared with Vector tools; "Review" items are hints. The same check is available as
`rbgcan check-cdd`.

### Inspect recorded DBC bits and exact 64-bit raw codes

Select a DBC message and signal to open the bit-layout inspector. Its recorded
example is tied to one concrete source frame and CAN channel; an inactive mux
branch shows raw source bits but is **not** presented as a decoded observation.
The inspector reports exact integer decimal and hexadecimal raw codes from the
original payload for both Intel and Motorola signal layouts, including signed
64-bit values. Source-backed `VAL_` descriptions use exact integer lookup.

The physical value and time-series graph still use floating-point scaling.
For integer codes outside the unambiguous IEEE-754 range (above 2^53−1 in
magnitude), the inspector explicitly marks physical scaling as approximate.
The exact raw bit pattern is retained in the inspector; do not use the rounded
time-series raw number to verify a large 64-bit state. IEEE-754 signals expose
their exact bit pattern separately and do not treat those bits as an integer
`VAL_` code.

DBC engineering validation checks that classic `mN` multiplex branches have
a valid integer root selector and that the selected code fits its width. For
extended `SG_MUL_VAL_` definitions, it checks named controllers and ranges.
An invalid/missing selector is reported for review; ambiguous branches must
not be treated as definitely active or definitely bit-exclusive. Signed
selectors use signed coded values, not the same bits reinterpreted as unsigned.

## 3c. Pop-out windows

Some analysis views open in their own window so the main workspace stays where it is. A pop-out
shows the loaded analysis; it does not change the trace file. DBC editing from the signal inspector
follows the rules in section 3b.

- **DBC signal inspector** — select a message and signal in the DBC Manager, then press
  **Signal inspector ↗**. The window shows the bit layout, encoding and units, value meanings
  (`VAL_`), observed raw values and source attributes. Its buttons include **Add to chart**,
  **Quick Plot ↗**, **Related DIDs** (recorded UDS-to-CAN correlations involving this signal),
  **Source frame**, and **Open first source frame** / **Open last source frame** for the observed
  raw codes.
- **UDS transaction detail** — double-click a transaction in **UDS Analysis → Transactions**. Its
  full request, response and decoded DID detail opens in a window.
- **Statistics reports** — on the Statistics page press **Reports in a window ↗**. The nominal-cycle,
  transition, network-matrix, trigger, sequence and compare reports open together in one larger window.

Other windows (Quick Plot, Signal charts, Derived signals, Signal analysis, E2E check, Trace tools,
Notes, Merge DBC versions, Compare signal, Trace health, Spec vs trace, Signal overlay) are described
in the section where their button appears.

## 4. Find and plot recorded signals

### The quickest path

Press **Ctrl+K** (or **Search everywhere**) and type a signal name, CAN ID,
message, ECU or DID. Recorded-signal results are taken from actual decoded
Trace timelines, **including constant signals**: the Change Matrix is a
separate result for change investigation. Each recorded result identifies
its DBC, physical CAN channel and 11-/29-bit message ID, so identically
named signals on different buses or original recordings cannot be silently mixed.

Click **Open** on the result (or use Up/Down + Enter) to focus the exact
signal in **Signals & Plot** and add it to the main chart. No other plotted
signals are removed. Click **Clear** to restart universal search without
closing it, or **Close** / Esc to dismiss it.

Alternatively, enter **Signals & Plot** and use the search box. Space-separated
terms are combined as AND on the **same observed series**, for example
`ch2 EngCoolantTemp`, `0x100 Mode`, `ECM EngineLoad` or
`logger:2 EngCoolantTemp`. In a **multi-file analysis**, choose a physical recording directly from the
**Recording** dropdown (**All recordings**, **Logger 1**, **Logger 2**…).
The signal table and main plot follow the chosen logger together; selecting
a different logger does **not** discard previously checked plotted curves.
Returning to All recordings restores those selections without reimport.
You can also search `logger:1`, `logger:2` (1-based) within the
selected scope. The separate **Recording** column identifies each file.
Matching CAN IDs, channels and timestamps from different recordings no longer
merge into one decoded signal curve. Quick Plot shows the logger explicitly.
This does not imply synchronized logger clocks or shared ECU identity.
The signal table begins with **Signal**, **Value @ cursor** and
**Last source**, followed by an explicit **↗ Plot** action for a
dedicated Quick Plot and a checkbox for the main comparison chart.
`Changing only` and `Plotted only` narrow the table.
**Clear filters** returns to all observed signals **without deleting
the chart selections, watch list or time position**. The visible/total
result count explains the current filter scope.

### Saved diagnostic investigations in Data Studio

In **Data Studio → Compare**, set the original logger, DTC search/status
and DID↔DBC hypothesis filters. Choose **Save DTC + DID review** to save
the parameters in the active analysis tab (a tab is created if needed).
The native session stores a compact recipe, **not** DTC tables or copied
CAN frames. Opening that analysis again restores the saved filters and
returns to Compare, using the currently imported original evidence.
A saved logger path that cannot be matched to an imported recording
is **rejected**, never silently changed to All recordings. Use **Save
session** to persist these notebook tabs to disk.

### DTC snapshot CAN byte evidence in Data Studio

Choose **Data Studio → Compare → DTC investigation**, select a fault and
a precise recorded event. Only snapshot/extended-data records belonging
to that event appear below. A clear/status-only event never inherits
a record from a different read. Select a record or field; **Source CAN
frames** and **Verified CAN byte evidence** display the original frame
and payload-byte offsets when the entire ISO-TP response was verified.
Use **Open record in Trace** or **Open field in Trace** to navigate to
the original CAN evidence (first frame for multiframe data). Unverified
values retain raw bytes, not a purportedly verified measurement.

### UDS, DTC and CDD/ODX ↔ DBC in Data Studio

Use **Data Studio → Compare** for source-scoped diagnostic investigation
without running a cloud cluster or connecting to a live ECU.

- **DTC investigation:** one history per original physical logger, ECU,
  channel and fault code. Check the CDD/ODX definition source, status,
  0x14 clear operations and snapshot/extended-data availability; choose a
  timeline event to open its exact recorded CAN frame. **Open in UDS**
  displays the same DTC in the specialist Fault memory view.
- **DID ↔ CAN/DBC investigation:** review the CDD/ODX DID definition,
  DBC candidate signal, actual source identifier, original paired
  UDS/CAN frames and numeric fit. Use the two source-frame actions
  to inspect the request/response or CAN signal evidence, or choose
  **Inspect in UDS** for the complete pair-by-pair view.

The recording picker scopes both tables and does not silently assign
unverified matches to a logger. A strong statistical match is a
**hypothesis**, not proof that two ECU quantities are identical.
Use **Save DTC + DID review** to save the diagnostic filters as a tab recipe.
The saved analysis library also preserves tabs and their recipes along with
source fingerprints. Reopening requires the original files; changed sources
require confirmation and missing sources prevent reopening. CAN frames and
computed results are not stored in the saved analysis.

### Open the exact CAN curve from a DTC snapshot lead

In the DTC snapshot → DID → CAN/DBC candidate grid, select a candidate
and choose **Plot exact CAN source**. This opens the decoded signal
that supplied the independent correlation, matching the original
recording, physical bus, observed wire ID/format and complete DBC
signal identity. A source mismatch or duplicate match is rejected;
the graph is focused on a recorded paired CAN sample, *not* an assumed
snapshot time. Use **Investigate selected candidate in UDS ↔ CAN** to
inspect the underlying time-series DID and CAN witnesses.

### DTC status bit evidence and clear operations

Select a recorded DTC event in **Data Studio → Compare** to inspect the
eight ISO 14229 status flags and their changes from the preceding
comparable status. An acknowledged 0x14 ClearDiagnosticInformation is
**not** a read-back of the DTC's state via 0x19: the explanatory text
marks that distinction and starts a new comparison baseline after clear.

### DTC snapshot → DID → CAN/DBC leads in Data Studio

In **Data Studio → Compare → DTC investigation**, select a fault and the
specific recorded **0x19 snapshot event**. The new **DTC snapshot DID →
CAN/DBC candidates** grid joins only verified snapshot DID source bytes
to independently source-paired DID↔CAN/DBC hypotheses in the **same
physical recording**. It shows the selected raw/engineering snapshot field,
CDD/ODX file, DBC signal, CAN channel and separately recorded value-pair
count. A shared DID and same logger are *navigation leads*, not proof
that the snapshot value was itself paired with the DBC signal. An unknown
physical logger, absent source spans or rejected/unpaired fit produces
no link. You can investigate a selected hypothesis in UDS ↔ CAN and
navigate to its real source frames. Each event is isolated from later
reads and clear operations.

### Change Matrix provenance in a multi-file session

In **Change Matrix**, the **Recording** dropdown provides All recordings or
one original logger. Activity, changed/missing classifications, detailed
source-frame links and CSV/XLSX export follow that physical source. The
Recording and CAN columns identify every finding. Exports append original
source path and CAN channel without changing legacy output columns.
If an imported logger has no DBC signal evidence, the matrix explains this
instead of inventing missing signals from another logger's CAN IDs.
The selector does not synchronize independent recorder clocks.

### Reading the plot correctly

By default analog curves use **sample-and-hold**: the value remains the
last *actually decoded sample* until a new recorded frame arrives.
You can enable **Linear lines** for smoother visual interpolation, but
interpolated slopes are **not additional recorded measurements**.
Enum/multiplex/short digital fields are shown as stepped logic lanes.
Multiple analog signals with different units can use independent Y scales.

The chart remains navigable through a quiet interval after a selected
ECU stops transmitting. When no plotted signal has a **new sample**
in the current window, the UI says so rather than inventing observations.
**Last source** tells you how long ago the value was recorded; its tooltip
identifies the original frame and timestamp. Quick Plot shows the same
source provenance in its side panel.

Pointer and keyboard actions:

- **Click:** X1 cursor; **Shift-click/right-click:** X2 cursor, for
  elapsed time and observed values at both timestamps. With both cursors set, each signal's readout also shows
  `min / max / avg / n` between X1 and X2 (avg is time-weighted over the held signal; min/max include the value held
  at the earlier cursor; nothing is invented before a signal's first recorded sample).
- **Keyboard cursors** (plot focused): **Shift+←/→** moves X1 one pixel, **Ctrl+←/→** moves X2
  (**PageUp/PageDown** with the same modifier = 10 pixels); **Shift/Ctrl+↑/↓** jumps X1/X2 to the previous/next
  recorded sample of the active signal. **Tab / Shift+Tab** choose the active signal (marked ► in the legend) and
  **L** cycles its line style: default, steps (hold), line, dots. Line styles are remembered for the session only.
- **Ctrl-click:** jump to Trace at the clicked time.
- **Mouse wheel:** zoom; **Shift-wheel:** pan; **Ctrl+0:** reset plot zoom.
- **F3 / Shift+F3** in Signals & Plot: next/previous actual recorded
  change for the selected signal.
- **Ctrl+Enter** in Signals & Plot: open the selected signal's Quick Plot.
- **Ctrl+F:** focus search in the current workspace; **Esc** clears a
  focused local search; **Ctrl+K:** universal search; **F1:** guided tour.

### Raw full-scale markers are not error diagnoses

The hollow **○RAW** marker indicates the all-ones raw bit pattern of a
DBC signal, *not necessarily* an invalid reading. Some DBCs explicitly
assign that code to “unavailable”; others permit it as valid data.
Check the actual `VAL_` descriptions and source definition before
concluding that a sensor failed. The source frame remains available in
Trace and the DBC inspector.

Use the **★** action from Trace or Byte Matrix signal details to pin
important signals in the watch list. The watch list displays the selected
time's last recorded signal values; it is **offline replay**, not live
bus acquisition.

### Display settings

The chart's linear/hold choice, Y-axis mode, legend visibility and
cumulative replay mode are saved as local display preferences.
Search and result-hiding filters are deliberately not restored on
the next launch, so the app does not unexpectedly reopen with hidden
recorded signals.

### Signal analysis, charts and derived signals

These three tools sit beside the chart on **Signals & Plot**. They use only recorded samples.

- **Signal analysis…** opens a window for the selected signal with statistics, a time-weighted
  histogram (each value counts for the time it was held), constant runs and a dominant frequency.
  The frequency is an estimate from the recorded sample times.
- **Charts…** opens a window with three views:
  - **Histogram** — distribution of one signal, weighted by **Time held (seconds)** or
    **Per sample (count)**, with 1 to 500 bins. **Export CSV…** saves the bins.
  - **XY plot** — one signal against another, with the correlation and a regression fit.
    Correlation shows that two values move together, not that one causes the other.
  - **Spectrum** — frequency content with the top peaks. Choose the FFT window (Hann is the
    suggested default), the resampling mode (**Hold last value** or **Interpolate**) and
    **Remove average** to hide the constant offset. Frequencies above half the resampled rate
    cannot be resolved.
- **Derived signals…** computes a new signal from recorded ones with a formula. The window
  shows error positions while you type and a preview chart. **Open in Quick Plot** opens the result
  marked as not recorded, and **Export CSV…** saves it.

Expression syntax:

- Variables are signal names (letters, digits and underscores) or any name in backticks. A name
  containing a dot must be in backticks, for example `` `Engine.Speed` ``. A dot outside backticks
  is a syntax error.
- Operators: `+ - * / %`, comparisons `< <= > >= == !=`, logic `&& ||`, `!`, and parentheses.
  Comparisons and logic give 1 for true and 0 for false.
- Functions: `abs(x)`, `min(a, b)`, `max(a, b)`, `sqrt(x)`, `pow(a, b)`, `clamp(x, lo, hi)`,
  `if(condition, a, b)`.
- Time functions: `prev(x)` (previous value), `delta(x)` (change since the previous point),
  `rate(x)` (change per second between points) and `avg(x, seconds)` (average over a time window).

Examples (the window offers them as buttons; replace the placeholder names with your signals):

- `a - b` — difference of two signals.
- `rate(speed)` — rate of change of a signal.
- `avg(temp, 2)` — two-second average.
- `if(speed > 50, 1, 0)` — 1 while the speed is above 50, otherwise 0.

Evaluation: the result is sampled at the union of the timestamps of the signals used. Each signal
keeps its last recorded value between samples (no interpolation). The result is undefined (blank)
before the first sample of an input, and undefined values propagate. Division or modulo by zero
gives an undefined result. Time functions act on the points of the result, not on the raw frames.

## 5. Analyze the Change Matrix

Open **Change Matrix** to see every decoded signal classified as:

- Stable
- Variable
- Anomalous
- Missing

The matrix shows sample count, change count, first and last value, min/max, amplitude, largest step and first/last change time.

Select a row to inspect individual changes with from value, to value, delta, timestamp and source frame index.

Use the matrix filters to focus on changed, stable, anomalous or missing signals. Export the result as CSV or Excel.

## 5b. Byte Matrix (temporal)

The Byte Matrix shows, per bus/ID, the payload byte state **at the selected point of the
timeline** — drag the slider or press Play to watch bytes change over time, consistently
with the trace and plot views. Changed bytes are highlighted and the tooltip lists the
flipped bits.

Select a Byte Matrix row to see decoded signals for that bus/ID at the current replay
position. From there you can plot, watch or open a dedicated quick plot for each signal.

## 5c. Statistics

The Statistics workspace lists per-identifier figures computed from the loaded trace:
frame count, average rate, min/avg/max period, jitter and DLC range. Columns are sortable;
the search box filters by ID, message name or channel.

Use **Compare trace...** to load a second offline trace and compare frame counts and
message rates per bus/ID. The comparison table marks IDs as added, missing, changed or
similar. If CAN databases are loaded, the app also compares decoded signal timelines and
shows sample count, last value, value delta, range and activity status per signal.

### KWP2000 documents

If the loaded CDD or ODX describes a KWP2000 ECU (services such as ReadECUIdentification 0x1A,
ReadDataByLocalIdentifier 0x21, WriteDataByLocalIdentifier 0x3B), the UDS analysis pairs and names these
services with their one-byte local identifier. The protocol is chosen from the loaded documents only; with
no KWP2000 document loaded the analysis stays UDS. Local-identifier payloads are shown as hex, not decoded.
Verified with the service list of `kwp2000-bed.cdd` and synthetic frames; no real KWP2000-over-CAN capture was
available.

## 6. Correlate DID to CAN Signals

Open **DID-CAN Mapping** and load one or more CDD or ODX files.

Run **Auto-correlate** after DBC files are loaded. If a trace is also loaded, trace evidence is included in the score.
When the trace contains ISO-TP UDS positive ReadDataByIdentifier responses (`0x62 DID`),
RBG CAN Insight extracts observed DID values offline and uses them as additional mapping
evidence. The compact **In trace** numeric preview does not establish ECU
source identity; its label explicitly notes this uncertainty. Raw-only fields,
conflicting DID records, unexpected extra response bytes and wide integers
without exact double representation are never shown as physical scalar values.

Each row shows:

- DID
- diagnostic parameter name
- description
- unit and range
- candidate CAN signal
- CAN message
- conservative association indicator (automatic matches capped at 50%)
- raw candidate match score (ranking evidence, not signal identity probability)
- reason
- review status

You can confirm, reject or manually choose a candidate. **Confirm** records an
explicit analyst review, not an automatic scoring outcome. The raw **Match score**
remains visible even when the association indicator is capped at 50%.
Save the reviewed map as `.rbgmap.json`, reload it later, or export it to CSV/XLSX.
For backwards compatibility, the saved `Confidence` number is the raw candidate
ranking score; `Status` records the separate review decision.

Newly saved mappings also carry versioned SHA-256 **semantic definition fingerprints**
for the DID and DBC signal. Reloading a saved mapping can only retain a Confirmed or
Rejected decision if the current ECU/DID and unambiguous CAN signal match both
fingerprints. Changes to bit placement, scales, declared value meanings, multiplexing,
fields or diagnostic engineering hierarchies force **To verify**, with a visible
**Review warning**; notes and the candidate identity are preserved for inspection.
Legacy maps without fingerprints still load, but previously reviewed associations
must be reviewed again. When multiple files define the same ECU/DID or several
DBC signals match the saved identity, ambiguous entries are not silently assigned.
Fingerprints summarize the subset of definitions the parser understands; they are
not full file hashes, signatures or proof of OEM source authenticity.

Use **Plot candidate** to send the selected mapping candidate directly to Signals and Plot.

### Compare saved mappings

Choose **Compare saved maps…** in DID-CAN Mapping, then choose the
baseline `.rbgmap.json` and the newer/current `.rbgmap.json`. The comparison
does not require any trace, DBC or CDD/ODX to be loaded; the original maps
remain untouched. The comparison first appears as an **in-app read-only preview** below the
Mapping table; expand the preview to inspect each group without Excel.
Choose **Export comparison…** to save it as **XLSX** or **CSV ZIP**.
A summary worksheet and detailed table include DID/ECU, both CAN targets and review
states, notes, match scores, definition fingerprints, change category and
explicit review flags.

One DID+ECU repeated on either side is **Ambiguous**, not automatically
paired across different variants; missing historical fingerprint fields are
**Unverifiable**, not proof that nothing changed. Added and removed mappings
are distinguished from genuine source-definition drift. A matching semantic
fingerprint covers only fields understood by the parser, not opaque OEM
sections or file authenticity. This is a comparison aid, not independent
confirmation of DID/CAN identity.

### Compare DBC definitions

Open the **DBC Manager** and choose **Compare DBCs…**. Select first the
baseline DBC and then the updated DBC. A read-only, expandable table
compares messages by exact arbitration ID **and 11-/29-bit format**, then
signal names within the same frame. It distinguishes added, removed,
changed and unchanged definitions; renamed or moved signals are not
silently paired across different CAN IDs. Review fields cover DLC,
transmitter, cycle, bit placement, signedness, endianess, value type,
scale, unit, range, multiplexing, `VAL_` value meanings, attributes
and source comments. Decoding-related differences are separated from
metadata-only differences.

The **Export DBC diff…** action writes a multi-sheet XLSX or UTF-8 CSV
ZIP: summary, all definition differences with before/after values, and
parser warning lines. Neither input file is modified, and the
comparison does not replace the loaded decoder database.

Select one difference and use **Inspect baseline** or **Inspect current**
to open its exact message and signal in the active DBC Manager. Navigation
works only if the selected version is already loaded from the same full
source path and its parsed definitions still match the comparison snapshot.
A same-named database from another folder, wrong CAN format, ambiguous
signal, unloaded version, or modified source snapshot is explicitly
rejected. The action clears stale DBC search filters and immediately
opens the selected definition. A trace is still required for recorded
on-bus evidence. A matching
parsed definition does **not** prove unsupported vendor-specific
statements or other opaque data are identical. The comparison does not
establish compatibility with every OEM or Vector database, or every display
size and scaling setting.

### Compare CDD, ODX and PDX definition versions

In **UDS Analysis → Diagnostic Catalog**, choose **Compare CDD / ODX…**.
Select the baseline description, then the current description. The operation
does **not** replace currently loaded catalogs, and no trace or ECU connection
is required. An expandable, read-only table shows affected DID/ECU identifiers,
fields, Engineering PAR and nested Sub-PAR paths, DTC snapshot/extended fields,
routine input/output definitions, services, access prerequisites, variants
and transport profiles. **Changes only** hides unchanged definitions in the
preview; the full report still includes them. You can also independently
filter by ECU/variant, definition type (e.g. Engineering PAR, DTC, routine)
and multi-word search across paths, changed fields and before/after values.
The visible/total counter reflects preview filtering only; export retains
all source rows.

**Inspect baseline** and **Inspect current** (after selecting a row in the
comparison preview) open the exact already-loaded DID, DTC, routine or nested
field/Engineering PAR in the Catalog Explorer. The selected CDD/ODX/PDX
version must have been **loaded independently** into the active workspace.
The source path, ECU and identifier must resolve to exactly one definition;
a missing or ambiguous child is not silently replaced by another PAR.
An absent version, removed field or unsupported service/variant target
produces an explanatory status message without changing the inspector.

From the selected Catalog Explorer **DID**, use **Correlate DID ↔ CAN**
to focus only that DID's original source and ECU in Mapping. **Inspect selected
DID** in Mapping returns to exactly this source's definition; it does not select
the first same-number DID from another ECU. The mapping search field or
review-status filter can be changed to leave exact-source focus and return
to normal multi-catalog search. A numeric DID shared by different ECUs or
variants is never enough by itself to claim a matching CAN association.

**Recorded DID evidence** in Catalog Explorer focuses all recorded positive
UDS response samples for the selected DID, including any different endpoint
groups, without silently treating one as proof of catalog ECU identity.
The DID samples view offers **Source frame** (the last complete positive
reply frame for the selected sample group) and **Clear DID filter**.
No result means no complete positive sample was recovered in the loaded trace,
not that the ECU lacks that diagnostic identifier. Trace and catalog sources
are untouched.

**Export comparison…** saves an Excel workbook or UTF-8 CSV ZIP containing
a summary, detailed before/after definitions, and known extraction/definition
issues. The exporter stages the result before moving it into place so a failed
write cannot truncate an existing report.

Interpret the comparison conservatively: a DID or DTC marked **Changed** may
be affected by a nested field even when its scalar attributes are identical.
Repeated DID+ECU or routine identities across source variants are **Ambiguous**;
all their children remain unpaired rather than being matched arbitrarily.
PAR hierarchy paths may encode positional ordinal indices, so reordering
can require manual investigation. Source-declared bit positions in unresolved
Engineering PARs are **not** evidence of verified response-record offsets,
and unchanged parsed data does not certify opaque OEM sections or actual
ECU behavior.

Compatibility with representative OEM files and independent diagnostic tools
is not established by the comparison alone; review exported results before
relying on them.

### Engineering Parameters and Sub-PARs

> Availability depends on the version you are running. Validate interpretation
> against representative CDD/ODX files before relying on the results.

With one or more CDD/ODX/PDX descriptions loaded, open the **Diagnostic Catalog** tab under UDS Analysis. Use the **ECU/variant selector** beside catalog search to focus one ECU; **PARs XLSX** and full diagnostic definitions XLSX follow both the ECU selector and current search. The selected-DID export remains scoped to the selected definition. The compact **Fields/PARs** count shows how many field definitions and source-backed engineering parameter nodes the application recovered for a DID. The right inspector separates **Declared fields** (physical decoding evidence) from **Engineering PARs** (source-backed nested definitions); select a PAR row to inspect its source ID, explicitly declared PAR code, origin, type, path, description and unresolved reference status.

- **PARs XLSX:** export all Engineering PARs and nested Sub-PARs, their extraction coverage, and source-declared raw value meanings, matching the current catalog search and ECU selector; no CAN trace is required.
- **Export definitions XLSX:** export all diagnostic worksheets plus Engineering Parameters, PAR Extraction Coverage and Engineering PAR Values.
- **Export DID XLSX:** from the selected DID detail, export that DID only, its nested field/engineering definitions and extraction coverage; ECU, source file, DID and display name isolate equal numeric IDs.

The Engineering PARs inspector includes a **PAR meanings** table for the selected node. It shows raw lower/upper bounds and source descriptions, including declared ranges and missing limits. Source data comes from ODX `TEXTTABLE` and CANdela CDD `TEXTTBL/TEXTMAP`, when the datatype is linked to that PAR. For CDD the source English `TUV` text is preferred, falling back to another declared language. A missing text label is retained as an incomplete definition rather than silently discarded. The catalog and exports can be searched by meaning (e.g., `Reserved`) without dropping the other definitions of the matched DID.

The **Engineering Parameters** sheet includes source, ECU, DID, top-level PAR and its description, each sub-PAR, parameter ID, original names and descriptions, level, parent/path, data type, type reference, declared bit length, unit and every originating read/write service. The original CDD/ODX source byte, bit and bit-offset declarations are shown in separate columns marked **unverified**: they are not automatically UDS payload-relative positions. Every nested node receives its own row; equal names in different branches remain distinct. For write-only ODX DIDs (service `0x2E`), Engineering PARs are read from the actual write-request payload; protocol header constants are excluded.

For dynamic ODX arrays and end-of-PDU structures, the tool exports each **template** Sub-PAR once. Its number of runtime occurrences and bit positions remain unverified. The coverage worksheet separately counts **unresolved references**, **variable-length definitions**, and missing source descriptions/PAR codes.

**PAR Extraction Coverage** also counts source value meanings and flags unverifiable boundaries, contradictory ranges, unsupported wide/decimal codes or missing text as partial. Exact numeric lookup is restricted to unambiguous, representable values; a contradictory exact code overlapping a differently labelled range is not decoded. CDD fields with source-backed coded length/meaning but no verified record-relative bit offset remain raw-only.

**PAR Extraction Coverage** is essential before interpreting a workbook as complete: **SOURCE EXTRACTED** means only that the available structures were read, not that the OEM definition has been independently verified. If multiple services describe the same DID differently, **PARTIAL** warns about preserved parallel engineering structures and can disable unsafe automatic physical decoding. If identical Read/Write structures are deduplicated, both source services remain listed. **PARTIAL**, **FALLBACK** and **NO PAR TREE** require source review. Unresolved variant-sensitive ODX references remain marked rather than guessed. Never use raw-only descriptions as validated payload bit offsets or physical conversion rules.

Very large exports are automatically split into consecutively named Excel worksheets at the Excel row limit. This preserves the original row order and repeats headings on every part; large-file opening and OEM compatibility still need local Windows tests.

More information: [Engineering Parameters export engineering checkpoint](ENGINEERING_PARAMETERS_EXPORT_2026-11-05.md).

## 7. Presets and Reports

### Offline investigation views

The UDS Analysis workspace also provides four read-only investigation views:

- **Findings** combines diagnostic, transport, DTC, routine, timing, signal-range and catalog
  irregularities. Double-click a finding with source evidence to open the corresponding Trace frame.
- **Sequence** reconstructs the recorded order of session, security, fault-memory, routine, data,
  communication and transfer operations. It describes observed traffic; it does not replay it.
- **ECU snapshot** lists identification values reconstructed from recorded DID responses, including
  standard identification DIDs and catalog parameters whose names indicate hardware, software,
  calibration, variant, supplier or serial information.
- **Catalog coverage** compares observed services, DIDs, DTCs and routines with loaded CDD/ODX
  definitions. Unresolved identifiers remain explicit.

Use **Save preset** and **Load preset** to preserve the active workspace, replay speeds,
plot window and watch list.

Use **Export report** to create a ZIP analysis package with:

- `summary.md`
- `report.html`
- `statistics.csv`
- `top-movers.csv`
- `dbc-coverage.csv`
- `unmatched-ids.csv`
- `watch-list.csv`
- `mapping.csv`
- `diagnostic-analysis.json` — structured UDS evidence and source-frame references
- `offline-findings.csv`
- `diagnostic-sequence.csv`
- `ecu-snapshot.csv`
- `diagnostic-catalog-coverage.csv`
- `uds-transactions.csv`
- `did-samples.csv`
- `fault-memory.csv`
- `routine-analysis.csv`
- `uds-can-correlation.csv` — metrics, verdict and explanation
- `diagnostic-report.pdf` — portable read-only diagnostic summary
- `rule-bookmarks.csv`
- `compare-trace.csv` when a comparison has been run
- `compare-signals.csv` when a decoded signal comparison has been run

### Projects and notes

- **Save project…** and **Open project…** on the Dashboard store the loaded trace, DBC, ARXML and
  CDD/ODX files, the filters and the notes as one project. Paths are stored relative to the project
  file and each file's SHA-256 is recorded. **Open project** checks the files and lists in plain
  language anything that changed or is missing before it loads.
- Analyst notes are exported from the **Notes…** window as CSV or Markdown.

### Export formats

- **Trace → Export visible…** writes the currently filtered rows as ASC, CSV, TRC, BLF or MF4.
  MF4 frames that the format cannot represent are counted and reported, not silently dropped.
  MF4 output was checked by re-importing it with this application only, not in other MDF4 tools.
- **DID-CAN Mapping → Export ODX…** writes the loaded CDD/ODX definitions as an ODX 2.2 file. The
  file is checked for structure only; it is validated in our tests against a public community ODX 2.2 schema but **not** against the official (licensed) ASAM schema, so
  verify it with your diagnostic toolchain before use.

## 8. Command line (`rbgcan`)

`rbgcan` is a console program built from `tools/RbgCanInsight.Cli`. It reads the same trace formats
as the application and works offline. Exit codes: `0` success, `1` error (message only), `2` usage
error. Run `rbgcan --help` for the usage text:

```text
rbgcan convert <in> <out>                         convert any importable trace to .asc/.csv/.blf/.trc
rbgcan info <trace> [--dbc a.dbc ...]             frame counts per channel, id count, time range, DBC coverage
rbgcan merge <out> <in1> <in2> ...                time-merge traces (channels are offset per input)
rbgcan cut <in> <out> --from <sec> --to <sec>     keep frames with from <= t < to, relative to the first frame
rbgcan shift <in> <out> --seconds <x>             shift all timestamps by x seconds (result must stay >= 0)
rbgcan export-signals <trace> <dbc> <out.csv> [--signals A,B]   decoded physical values as CSV
rbgcan check-dbc <dbc> [--out report.csv] [--fail-on error|warning|review]   DBC consistency check
rbgcan check-cdd <cdd|odx|pdx> [--out report.csv] [--fail-on error|warning|review]   catalog consistency check
rbgcan compare <a> <b> [--out report.csv]         compare two DBCs or two CDD/ODX/PDX files
rbgcan export-odx <cdd|odx|pdx> <out.odx> [--ecu <name>]   write ODX 2.2 and run the structural self-check
rbgcan report timing <trace> <out.csv> [--p2-ms n --p2star-ms n --s3-ms n --nbs-ms n --ncr-ms n]
rbgcan report faults <trace> <out.csv>            fault-memory table with status bits and decoded records
```

Notes:

- `convert` writes only `.asc`, `.csv`, `.blf` and `.trc`; the output format is chosen by the
  extension of `<out>`. MF4 output is available in the application (Trace tools, Export visible).
- `info --dbc` (repeatable) reports the share of frames and of identifiers each DBC covers.
- `merge` keeps the input files unchanged and offsets channels per input so they do not collide.
- `export-signals` writes the columns `time_s,channel,message,signal,raw,physical,unit`.
  `--signals` takes a comma-separated list of signal names; without it, every decoded signal is written.

Examples:

```text
rbgcan info drive.blf --dbc powertrain.dbc
rbgcan convert drive.mf4 drive.asc
rbgcan merge combined.asc part1.blf part2.blf
rbgcan cut drive.blf window.blf --from 10 --to 25
rbgcan shift drive.asc shifted.asc --seconds 3.5
rbgcan export-signals drive.blf powertrain.dbc speed.csv --signals EngSpeed,VehicleSpeed
```

CI-oriented commands: `check-dbc` and `check-cdd` print one `PASS`/`FAIL` line and exit `0` when no finding
reaches the `--fail-on` level (default `error`), `1` otherwise, `2` for usage errors. `compare` exits `0`
when the two files are identical at the parsed-definition level and `1` when they differ (the CSV lists
only the differing rows; mixing a DBC with a CDD/ODX is a usage error). `export-odx` needs `--ecu` when
the file holds several ECU variants and exits `1` when its structural self-check (not an XSD validation)
reports problems. `report timing` exits `1` only for error-severity findings.

More report commands may be added in later releases. Check `rbgcan --help` for the commands in the
version you have installed.

## 9. Troubleshooting

**The trace shows no frames.**

- Check that the file extension is one of the supported formats and that the recording is complete.
- Open **Show or hide CAN trace import quality** on the Trace page. It lists the accepted channels
  and the rejected or skipped records. Some files contain only records the importer rejects (for example identifiers out of range),
so they import zero frames.
- Clear the search box with **Esc** and set **View** back to *All frames*. A text filter, *Latest per
  ID* or *Changes only* can make the list look empty.

**The DBC matches nothing (or only a few frames).**

- Open the DBC Manager and read the hint in the *Unmatched trace IDs* panel. A hint of "same J1939
  PGN" means you should enable **Match by J1939 PGN** for that database. "Excluded by channel
  assignment" means you should change its **Bus assignment**.
- Check the identifier format. 11-bit and 29-bit identifiers are different messages even when the
  numbers look alike.
- A ⚠ badge on a DBC means the parser skipped something; hover over the badge to read what.
- If no hint explains it, the database may be for another network. **Suggest best DBC…** shows which
  loaded database explains the trace best.

**A tool window says nothing was found.**

An empty result is not proof that the traffic is absent. Each tool needs specific evidence:

- **J1939 messages** needs multi-packet traffic (BAM or RTS/CTS) or DM1/DM2 frames.
- **OBD-II** needs diagnostic request and response frames in the trace.
- **Counters & checksums** needs identifiers whose payload changes over time. Detection covers
  Intel bit order and one-byte checksums only.
- **E2E check** needs the CAN ID to be in the trace, and the Data ID, CRC byte index and counter
  bit to match your configuration. Use **Apply detected settings** after a check, then compare.
- **Bus load timeline** needs frames on the channel and a bitrate typed for that channel in Statistics.
- **Signal analysis** and **Charts** need recorded samples for the selected signal.
- **Derived signals** are blank before the first sample of each input, and show an error position
  when the formula is incomplete or refers to a name that does not exist.

## Data Safety

RBG CAN Insight only reads offline files selected by the user. It does not connect to a vehicle,
configure hardware, transmit CAN frames or send diagnostic requests. In particular, the Fault
Memory and Routines views analyze recorded clear/start/stop/result traffic; they cannot perform
those operations.

## Portable Use

The portable release keeps the executable alone in the root folder:

```text
RBG-CAN-Insight_<version>_Portable/
├── RbgCanInsight.exe      ← launch this
├── README.txt
├── config/                recent files and user settings
├── logs/                  application logs (one file per day)
├── docs/                  this guide
└── runtime/               support files
```

Settings live in `config/` next to the executable, so the folder can be copied to another
Windows PC with preferences intact. If the folder is not writable (e.g. under Program
Files), the app falls back to `%LOCALAPPDATA%\RbgCanInsight`.

Build it with `pwsh tools/make-portable.ps1`.
