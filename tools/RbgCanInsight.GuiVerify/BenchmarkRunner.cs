using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RbgCanInsight.App;
using RbgCanInsight.App.ViewModels;
using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;

/// <summary>
/// Repeatable performance harness for large-trace offline analysis workflows, run via
/// `dotnet run --project tools\RbgCanInsight.GuiVerify -- --benchmark [frameCount]`.
///
/// This exists so a performance change reports a real before/after number instead of an
/// impression — the same discipline VCI DTS's own release notes use, and the same one that
/// caught them crediting the wrong change for a speedup they later had to retract (their 6.9.0
/// notes attributed a scrolling win to item-vs-pixel scrolling that turned out to be measuring a
/// stale cache, corrected in 6.9.1). Every metric here is measured against a synthetic trace
/// generated fresh for the run, using the exact same off-screen <see cref="MainWindow"/>
/// technique as the screenshot GUI-verification pass in <see cref="Program"/> — real layout,
/// real virtualization, real analysis pipeline, just positioned off the visible desktop.
///
/// Current scope: an in-memory trace up to a few million frames (today's architecture). Once a
/// disk-backed frame store lands for traces beyond ~10M frames, re-run this at that scale too —
/// the numbers below only describe what today's in-memory <c>CanFrame</c> list can hold
/// comfortably.
/// </summary>
internal static class BenchmarkRunner
{
    private const double FrameBudgetMs = 33.0; // ~30 fps, the budget VCI DTS's own notes use too.

    public static int Run(string[] args)
    {
        var frameCount = args.Length > 0 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 2_000_000;

        var root = Program.FindRepositoryRoot();
        var artifacts = Path.Combine(root, "artifacts", "benchmarks");
        Directory.CreateDirectory(artifacts);

        Console.WriteLine($"Benchmark: generating a {frameCount:N0}-frame synthetic trace…");
        var generationStopwatch = Stopwatch.StartNew();
        var tracePath = Path.Combine(artifacts, $"benchmark-{frameCount}.asc");
        var idCatalog = GenerateTrace(tracePath, frameCount);
        generationStopwatch.Stop();
        var dbcPath = Path.Combine(artifacts, "benchmark.dbc");
        File.WriteAllText(dbcPath, GenerateDbc(idCatalog));
        Console.WriteLine($"  generation: {generationStopwatch.Elapsed.TotalSeconds:F1}s");

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetBeforeMb = process.WorkingSet64 / 1024d / 1024d;

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
        Program.DrainUi();
        var model = (MainViewModel)window.DataContext;

        Console.WriteLine("Benchmark: importing + analyzing…");
        var importStopwatch = Stopwatch.StartNew();
        Program.PumpUntil(model.LoadTraceAsync(tracePath));
        Program.PumpUntil(model.LoadDbcAsync([dbcPath]));
        Program.DrainUi();
        importStopwatch.Stop();

        process.Refresh();
        var workingSetAfterMb = process.WorkingSet64 / 1024d / 1024d;
        var managedHeapAfterMb = GC.GetTotalMemory(false) / 1024d / 1024d;

        model.SelectedNavIndex = 1;
        Program.DrainUi();

        // Filtering runs first, before anything expands rows or scrolls the grid, so its timing
        // reflects only the filter/search logic itself rather than carrying over layout state
        // from a later phase.
        Console.WriteLine("Benchmark: filtering…");
        var probeId1 = model.Frames[frameCount / 2].Identifier.ToString("X3", CultureInfo.InvariantCulture);
        var idFilterMs = MeasureFilter(model, probeId1);
        var payloadFilterMs = MeasureFilter(model, "payload:AA");
        model.Trace.ClearFilterCommand.Execute(null);
        Program.PumpUntil(model.Trace.FilterCompletion);
        Program.DrainUi();

        Console.WriteLine("Benchmark: scrolling (20 rows expanded)…");
        var (scrollThroughExpandedMs, scrollPlainMs) = MeasureTraceScroll(window, model);

        Console.WriteLine("Benchmark: playback responsiveness (3s)…");
        var playbackLatenciesMs = MeasurePlaybackLatency(model);

        // Two high-impact repeated-cursor workloads. Query preparation is
        // already reported by MeasureFilter; these figures isolate replay's
        // cost after a query index exists, and overwrite/latest-ID updates.
        var (filteredScrubExecute, filteredScrubLayout) =
            MeasureIndexedReplayScrub(model, searchText: "payload:AA", viewMode: 0);
        var (latestScrubExecute, latestScrubLayout) =
            MeasureIndexedReplayScrub(model, searchText: "", viewMode: 1);

        // Standalone measured DBC engine work, intentionally LAST:
        // importing the whole trace and rendering WPF must not inherit
        // its extra memory allocations. A read-only prefix avoids copying
        // 250k large CanFrame structs or re-decoding 10m full frames.
        var dbcProbeFrameCount = Math.Min(250_000, model.Frames.Count);
        var dbcProbeFrames = new FramePrefix(model.Frames, dbcProbeFrameCount);
        var dbcProbeDecoder = new DbcDecoder(new DbcParser().ParseFile(dbcPath));
        var dbcProbeAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var dbcProbeTimer = Stopwatch.StartNew();
        var dbcProbeTimelines = SignalTimelineBuilder.Build(dbcProbeFrames, [dbcProbeDecoder]);
        dbcProbeTimer.Stop();
        var dbcProbeAllocatedMb =
            (GC.GetAllocatedBytesForCurrentThread() - dbcProbeAllocatedBefore) / (1024d * 1024d);
        var dbcProbeSamples = dbcProbeTimelines.Sum(timeline => (long)timeline.Samples.Count);
        var dbcProbeDecodeMs = dbcProbeTimer.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  [DBC] {dbcProbeFrameCount:N0} source frames · {dbcProbeTimelines.Count:N0} timelines · " +
            $"{dbcProbeSamples:N0} samples · {dbcProbeDecodeMs:F1} ms · {dbcProbeAllocatedMb:F1} MB allocated");

        var report = new
        {
            FrameCount = frameCount,
            GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
            GenerationSeconds = generationStopwatch.Elapsed.TotalSeconds,
            ImportAndAnalysisSeconds = importStopwatch.Elapsed.TotalSeconds,
            WorkingSetBeforeMb = workingSetBeforeMb,
            WorkingSetAfterMb = workingSetAfterMb,
            ManagedHeapAfterMb = managedHeapAfterMb,
            DbcStandaloneProbeFrameCount = dbcProbeFrameCount,
            DbcStandaloneTimelineCount = dbcProbeTimelines.Count,
            DbcStandaloneSampleCount = dbcProbeSamples,
            DbcStandaloneDecodeMilliseconds = dbcProbeDecodeMs,
            DbcStandaloneAllocatedMb = dbcProbeAllocatedMb,
            ScrollThroughExpandedRows = Summarize(scrollThroughExpandedMs),
            ScrollPlainRows = Summarize(scrollPlainMs),
            IdFilterMs = idFilterMs,
            PayloadFilterMs = payloadFilterMs,
            Playback = Summarize(playbackLatenciesMs),
            FilteredReplayCursorExecute = Summarize(filteredScrubExecute),
            FilteredReplayCursorLayout = Summarize(filteredScrubLayout),
            LatestByIdReplayCursorExecute = Summarize(latestScrubExecute),
            LatestByIdReplayCursorLayout = Summarize(latestScrubLayout)
        };

        var reportPath = Path.Combine(artifacts, $"benchmark-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine();
        Console.WriteLine("=== Benchmark report ===");
        Console.WriteLine($"Frames:                  {frameCount:N0}");
        Console.WriteLine($"Import + analysis:       {importStopwatch.Elapsed.TotalSeconds:F2} s");
        Console.WriteLine($"Working set before/after:{workingSetBeforeMb:F0} MB / {workingSetAfterMb:F0} MB (+{workingSetAfterMb - workingSetBeforeMb:F0} MB)");
        Console.WriteLine($"Managed heap after load: {managedHeapAfterMb:F0} MB");
        Console.WriteLine($"Standalone DBC probe:   {dbcProbeFrameCount:N0} frames, {dbcProbeDecodeMs:F1} ms, " +
            $"{dbcProbeAllocatedMb:F1} MB allocated, {dbcProbeSamples:N0} samples");
        PrintSummary("Scroll step (through expanded rows)", scrollThroughExpandedMs);
        PrintSummary("Scroll step (plain rows)", scrollPlainMs);
        Console.WriteLine($"ID filter (exact ID):    {idFilterMs:F1} ms");
        Console.WriteLine($"Payload filter (scan):   {payloadFilterMs:F1} ms");
        PrintSummary("Playback tick latency", playbackLatenciesMs);
        PrintSummary("Filtered replay cursor update (index reused)", filteredScrubExecute);
        PrintSummary("Filtered replay layout (WPF)", filteredScrubLayout);
        PrintSummary("Latest-per-ID cursor update (incremental)", latestScrubExecute);
        PrintSummary("Latest-per-ID layout (WPF)", latestScrubLayout);
        Console.WriteLine($"Report written to {reportPath}");

        window.Close();
        return 0;
    }

    /// <summary>Read-only view of up to 250k original source frames,
    /// avoiding duplicate payload allocations in the isolated DBC
    /// benchmark. Frame indices still refer to the original capture.</summary>
    private sealed class FramePrefix(IReadOnlyList<CanFrame> source, int count)
        : IReadOnlyList<CanFrame>
    {
        public int Count { get; } = Math.Clamp(count, 0, source.Count);
        public CanFrame this[int index] =>
            index >= 0 && index < Count ? source[index]
                : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<CanFrame> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
                yield return source[index];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Returns (time per step scrolling through the 20 expanded rows, time per step
    /// scrolling further on through plain collapsed rows) so a change that only helps one of the
    /// two cases is visible instead of averaged away.</summary>
    private static (List<double> ThroughExpanded, List<double> Plain) MeasureTraceScroll(MainWindow window, MainViewModel model)
    {
        var expandCount = Math.Min(20, model.Trace.Rows.Count);
        for (var i = 0; i < expandCount; i++)
        {
            model.Trace.Rows[i].IsExpanded = true;
        }

        Program.DrainUi();
        var traceGrid = Program.FindVisualChild<DataGrid>(window, "TraceGrid")!;
        var scrollViewer = Program.FindVisualChildren<ScrollViewer>(traceGrid).FirstOrDefault();
        var throughExpanded = new List<double>();
        var plain = new List<double>();
        if (scrollViewer is null)
        {
            return (throughExpanded, plain);
        }

        // With VirtualizingPanel.ScrollUnit="Item" (set on TraceGrid), the ScrollViewer's offset
        // unit is rows, not pixels — advancing by 1 per step is exactly one mouse-wheel notch, not
        // the fraction-of-whole-trace jump a naive pixel-height-based step would produce (which
        // mostly measures "re-materialize a viewport far away", a different and always-slow
        // operation). Starting at the top walks through the just-expanded rows first, then
        // continues into plain collapsed rows for direct comparison.
        scrollViewer.ScrollToVerticalOffset(0);
        window.UpdateLayout();
        var throughExpandedSteps = expandCount + 20;
        const int plainSteps = 300;
        var stopwatch = new Stopwatch();
        for (var step = 1; step <= throughExpandedSteps + plainSteps; step++)
        {
            var offset = Math.Min(scrollViewer.ScrollableHeight, step);
            stopwatch.Restart();
            scrollViewer.ScrollToVerticalOffset(offset);
            window.UpdateLayout();
            stopwatch.Stop();
            (step <= throughExpandedSteps ? throughExpanded : plain).Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        return (throughExpanded, plain);
    }

    private static double MeasureFilter(MainViewModel model, string searchText)
    {
        model.Trace.SearchText = searchText;
        var stopwatch = Stopwatch.StartNew();
        model.Trace.ApplyFilterCommand.Execute(null);
        var executeMs = stopwatch.Elapsed.TotalMilliseconds;
        // Large traces build the filter on a worker; the UI thread is free during this wait.
        Program.PumpUntil(model.Trace.FilterCompletion);
        Program.DrainUi();
        var drain1 = stopwatch.Elapsed.TotalMilliseconds - executeMs;
        var t2 = Stopwatch.StartNew();
        Program.DrainUi();
        var drain2 = t2.Elapsed.TotalMilliseconds;
        var t3 = Stopwatch.StartNew();
        Program.DrainUi();
        var drain3 = t3.Elapsed.TotalMilliseconds;
        stopwatch.Stop();
        Console.WriteLine($"  [perf] Execute(): {executeMs:F1} ms, DrainUi #1: {drain1:F1} ms, #2: {drain2:F1} ms, #3: {drain3:F1} ms");
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Measure repeated seeks on an already prepared query, separately
    /// from WPF DataGrid render. Never mix O(N) initial filter construction with
    /// steady-state scrub or claim low CPU times prove smooth WPF frame rates.</summary>
    private static (List<double> ExecuteMs, List<double> LayoutMs) MeasureIndexedReplayScrub(
        MainViewModel model, string searchText, int viewMode)
    {
        model.Trace.ViewModeIndex = viewMode;
        model.Trace.SearchText = searchText;
        model.Trace.ApplyFilterCommand.Execute(null);
        Program.PumpUntil(model.Trace.FilterCompletion);
        Program.DrainUi();
        model.Trace.TimelinePercent = 0;
        Program.DrainUi();

        var execute = new List<double>();
        var layout = new List<double>();
        for (var percent = 5; percent <= 100; percent += 5)
        {
            var timer = Stopwatch.StartNew();
            model.Trace.TimelinePercent = percent;
            timer.Stop();
            execute.Add(timer.Elapsed.TotalMilliseconds);
            timer.Restart();
            Program.DrainUi();
            timer.Stop();
            layout.Add(timer.Elapsed.TotalMilliseconds);
        }

        model.Trace.ClearFilterCommand.Execute(null);
        model.Trace.ViewModeIndex = 0;
        Program.DrainUi();
        return (execute, layout);
    }

    /// <summary>Proxy for UI-thread responsiveness while offline playback runs: the delay before a
    /// Background-priority dispatch actually executes reflects how backed up the UI thread is,
    /// the same symptom VCI DTS's own "UI input latency" measurement targeted.</summary>
    private static List<double> MeasurePlaybackLatency(MainViewModel model)
    {
        var latencies = new List<double>();
        model.Trace.TogglePlaybackCommand.Execute(null);
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < end)
        {
            var stopwatch = Stopwatch.StartNew();
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                stopwatch.Stop();
                frame.Continue = false;
            }));
            Dispatcher.PushFrame(frame);
            latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
            Thread.Sleep(5);
        }

        if (model.Trace.PlaybackButtonText == "Stop")
        {
            model.Trace.TogglePlaybackCommand.Execute(null);
        }

        return latencies;
    }

    private static object Summarize(List<double> valuesMs) => new
    {
        Count = valuesMs.Count,
        MedianMs = Percentile(valuesMs, 50),
        P95Ms = Percentile(valuesMs, 95),
        MaxMs = valuesMs.Count == 0 ? 0 : valuesMs.Max(),
        OverBudgetPercent = valuesMs.Count == 0 ? 0 : 100.0 * valuesMs.Count(v => v > FrameBudgetMs) / valuesMs.Count
    };

    private static void PrintSummary(string label, List<double> valuesMs)
    {
        if (valuesMs.Count == 0)
        {
            Console.WriteLine($"{label}: no samples");
            return;
        }

        var median = Percentile(valuesMs, 50);
        var p95 = Percentile(valuesMs, 95);
        var max = valuesMs.Max();
        var overBudget = 100.0 * valuesMs.Count(v => v > FrameBudgetMs) / valuesMs.Count;
        Console.WriteLine($"{label}: median {median:F1} ms · p95 {p95:F1} ms · max {max:F1} ms · " +
                          $"{overBudget:F0}% over {FrameBudgetMs:F0} ms budget ({valuesMs.Count} samples)");
    }

    private static double Percentile(List<double> valuesMs, double percentile)
    {
        if (valuesMs.Count == 0)
        {
            return 0;
        }

        var sorted = valuesMs.OrderBy(value => value).ToArray();
        var rank = (percentile / 100.0) * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var fraction = rank - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }

    /// <summary>Writes a Vector ASC-format synthetic trace: <paramref name="frameCount"/> frames
    /// spread across 40 identifiers with mixed periods (10/20/100 ms) and an incrementing payload
    /// counter, so byte-diff/change-highlight logic has real work to do. Returns the identifiers
    /// used so a matching DBC can be generated.</summary>
    private static uint[] GenerateTrace(string path, int frameCount)
    {
        const int idCount = 40;
        var ids = Enumerable.Range(0, idCount).Select(index => (uint)(0x100 + index * 8)).ToArray();
        var periodsMs = new double[] { 10, 20, 50, 100 };

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20);
        using var writer = new StreamWriter(stream, Encoding.ASCII, bufferSize: 1 << 20);
        writer.WriteLine("date Tue Jul 07 10:00:00.000 2026");
        writer.WriteLine("base hex  timestamps absolute");
        writer.WriteLine("internal events logged");

        var nextDueMs = new double[idCount];
        var counters = new byte[idCount];
        var lineBuilder = new StringBuilder(64);
        for (var written = 0; written < frameCount; written++)
        {
            var idIndex = written % idCount;
            var period = periodsMs[idIndex % periodsMs.Length];
            var timestamp = nextDueMs[idIndex];
            nextDueMs[idIndex] += period;
            counters[idIndex]++;

            lineBuilder.Clear();
            lineBuilder.Append((timestamp / 1000.0).ToString("F6", CultureInfo.InvariantCulture));
            lineBuilder.Append(" 1 ");
            lineBuilder.Append(ids[idIndex].ToString("X3", CultureInfo.InvariantCulture));
            lineBuilder.Append(" Rx d 8 ");
            lineBuilder.Append(counters[idIndex].ToString("X2", CultureInfo.InvariantCulture));
            lineBuilder.Append(" 20 00 00 00 00 00 00");
            writer.WriteLine(lineBuilder.ToString());
        }

        return ids;
    }

    private static string GenerateDbc(uint[] ids)
    {
        var builder = new StringBuilder();
        builder.AppendLine("VERSION \"\"");
        builder.AppendLine();
        foreach (var id in ids)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"BO_ {id} Msg{id:X3}: 8 ECU");
            builder.AppendLine(CultureInfo.InvariantCulture, $" SG_ Counter{id:X3} : 0|8@1+ (1,0) [0|255] \"\" Vector__XXX");
            builder.AppendLine(CultureInfo.InvariantCulture, $" SG_ Fixed{id:X3} : 8|8@1+ (1,0) [0|255] \"\" Vector__XXX");
        }

        return builder.ToString();
    }
}
