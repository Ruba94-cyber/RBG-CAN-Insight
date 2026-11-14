using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RbgCanInsight.Analysis;
using RbgCanInsight.Dbc;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;

namespace RbgCanInsight.PerfBench;

/// <summary>Samples this process (working set, private bytes) and the machine's free RAM.
/// Aborts the run with exit code 9 when free physical RAM drops below the guard.</summary>
public sealed class Sampler : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemStatus s);

    public static long FreePhysicalBytes()
    {
        var s = new MemStatus { Length = (uint)Marshal.SizeOf<MemStatus>() };
        GlobalMemoryStatusEx(ref s);
        return (long)s.AvailPhys;
    }

    private readonly Process _self = Process.GetCurrentProcess();
    private readonly Thread _thread;
    private readonly long _guardBytes;
    private volatile bool _stop;
    private long _maxWs, _maxPriv, _minFree = long.MaxValue;
    public string Stage { get; set; } = "start";
    public Action<string>? OnAbort { get; set; }

    public Sampler(long guardBytes)
    {
        _guardBytes = guardBytes;
        _thread = new Thread(Loop) { IsBackground = true, Name = "sampler" };
        _thread.Start();
    }

    public void BeginStage(string name)
    {
        Stage = name;
        Interlocked.Exchange(ref _maxWs, 0);
        Interlocked.Exchange(ref _maxPriv, 0);
        Interlocked.Exchange(ref _minFree, long.MaxValue);
    }

    public (long Ws, long Private, long MinFree) EndStage() =>
        (Interlocked.Read(ref _maxWs), Interlocked.Read(ref _maxPriv), Interlocked.Read(ref _minFree));

    private void Loop()
    {
        while (!_stop)
        {
            try
            {
                _self.Refresh();
                Max(ref _maxWs, _self.WorkingSet64);
                Max(ref _maxPriv, _self.PrivateMemorySize64);
                var free = FreePhysicalBytes();
                Min(ref _minFree, free);
                if (free < _guardBytes)
                {
                    OnAbort?.Invoke($"free RAM {free / 1048576} MB < guard {_guardBytes / 1048576} MB during stage '{Stage}'");
                    Environment.Exit(9);
                }
            }
            catch (InvalidOperationException) { }
            Thread.Sleep(25);
        }
    }

    private static void Max(ref long target, long v)
    {
        long cur;
        while (v > (cur = Interlocked.Read(ref target)) && Interlocked.CompareExchange(ref target, v, cur) != cur) { }
    }

    private static void Min(ref long target, long v)
    {
        long cur;
        while (v < (cur = Interlocked.Read(ref target)) && Interlocked.CompareExchange(ref target, v, cur) != cur) { }
    }

    public void Dispose() => _stop = true;
}

public sealed class Expected
{
    public long Frames { get; set; }
    public long FirstTimestampNs { get; set; }
    public long LastTimestampNs { get; set; }
    public long Channel0 { get; set; }
    public long Channel1 { get; set; }
    public long CanFd { get; set; }
    public long BitRateSwitch { get; set; }
    public long Errors { get; set; }
    public long Extended { get; set; }
    public long Transmit { get; set; }
    public int UniqueChannelIds { get; set; }
    public string CanonicalSha256 { get; set; } = "";
    public long DbcDecodedFrames { get; set; }
    public long DbcSignalValues { get; set; }
    public long DbcRawSum { get; set; }
    public double DbcPhysicalSum { get; set; }
    public long UdsTransactions { get; set; }
    public long UdsVinReads { get; set; }
    public long UdsSessionControls { get; set; }
    public long UdsNegativeResponses { get; set; }
    public long UdsRequestByteSum { get; set; }
    public long UdsResponseByteSum { get; set; }
}

public static class RunStages
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static async Task<int> Run(string trace, string expectedPath, string dbcPath, string outPath, string[] stages, long guardBytes)
    {
        var expected = JsonSerializer.Deserialize<Expected>(File.ReadAllText(expectedPath))!;
        var result = new JsonObject
        {
            ["trace"] = Path.GetFileName(trace),
            ["format"] = Path.GetExtension(trace).ToLowerInvariant(),
            ["traceBytes"] = new FileInfo(trace).Length,
            ["frames"] = expected.Frames,
            ["startFreeRamMB"] = Sampler.FreePhysicalBytes() / 1048576,
            ["stages"] = new JsonObject()
        };
        var stagesNode = (JsonObject)result["stages"]!;
        void Save() => File.WriteAllText(outPath, result.ToJsonString(Pretty));

        using var sampler = new Sampler(guardBytes);
        sampler.OnAbort = reason =>
        {
            result["aborted"] = reason;
            Save();
        };
        var self = Process.GetCurrentProcess();

        CanFrameCollection? collection = null;
        var problems = new List<string>();
        void Check(string what, bool ok, object? got, object? want)
        {
            if (!ok) problems.Add($"{what}: got {got}, expected {want}");
        }

        JsonObject Begin(string name)
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            sampler.BeginStage(name);
            return new JsonObject();
        }

        void End(string name, JsonObject node, double ms)
        {
            var (ws, priv, minFree) = sampler.EndStage();
            self.Refresh();
            node["elapsedMs"] = Math.Round(ms, 1);
            node["stageMaxWorkingSetMB"] = ws / 1048576;
            node["stageMaxPrivateMB"] = priv / 1048576;
            node["stageMinFreeRamMB"] = minFree / 1048576;
            node["processPeakWorkingSetMB"] = self.PeakWorkingSet64 / 1048576;
            node["processPeakCommitMB"] = self.PeakPagedMemorySize64 / 1048576;
            node["workingSetNowMB"] = self.WorkingSet64 / 1048576;
            node["privateNowMB"] = self.PrivateMemorySize64 / 1048576;
            node["managedHeapMB"] = GC.GetTotalMemory(false) / 1048576;
            stagesNode[name] = node;
            Save();
        }

        try
        {
            // ---- import (same path as MainViewModel.LoadTraceCoreAsync: adaptive collector + import health)
            if (stages.Contains("import"))
            {
                var node = Begin("import");
                var sw = Stopwatch.StartNew();
                var health = new TraceImportHealth();
                var capacity = (int)Math.Clamp(new FileInfo(trace).Length / (trace.EndsWith(".blf") ? 48L : 52L), 4096L, 1 << 20);
                using var collector = new AdaptiveCanFrameCollector(capacity);
                await foreach (var frame in TraceImporter.ReadAsync(trace, diagnostics: health))
                    collector.Add(frame);
                var snap = health.Snapshot();
                collection = collector.Complete(sortByTime: snap.OutOfOrderTimestamps > 0);
                sw.Stop();
                node["importedFrames"] = collection.Frames.Count;
                node["diskBacked"] = collection.IsDiskBacked;
                node["healthFrames"] = snap.Frames;
                node["healthRejected"] = snap.RejectedRecords;
                node["healthOutOfOrder"] = snap.OutOfOrderTimestamps;
                node["framesPerSecond"] = Math.Round(collection.Frames.Count / sw.Elapsed.TotalSeconds);
                Check("import frame count", collection.Frames.Count == expected.Frames, collection.Frames.Count, expected.Frames);
                Check("import health frames", snap.Frames == expected.Frames, snap.Frames, expected.Frames);
                Check("import rejected records", snap.RejectedRecords == 0, snap.RejectedRecords, 0);
                End("import", node, sw.Elapsed.TotalMilliseconds);
            }
            var frames = collection?.Frames ?? throw new InvalidOperationException("import stage required");

            // ---- correctness: counts, timestamps, canonical digest (same definition as TraceVerify)
            if (stages.Contains("verify"))
            {
                var node = Begin("verify");
                var sw = Stopwatch.StartNew();
                long fd = 0, brs = 0, err = 0, ext = 0, tx = 0, ooo = 0, c0 = 0, c1 = 0;
                long first = long.MaxValue, last = long.MinValue;
                var header = new byte[18];
                var buf = new byte[64];
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long prev = long.MinValue;
                for (var i = 0; i < frames.Count; i++)
                {
                    var f = frames[i];
                    if (f.TimestampNanoseconds < prev) ooo++;
                    prev = f.TimestampNanoseconds;
                    first = Math.Min(first, f.TimestampNanoseconds);
                    last = Math.Max(last, f.TimestampNanoseconds);
                    if (f.Flags.HasFlag(CanFrameFlags.FlexibleDataRate)) fd++;
                    if (f.Flags.HasFlag(CanFrameFlags.BitRateSwitch)) brs++;
                    if (f.IsError) err++;
                    if (f.IsExtended) ext++;
                    if (f.IsTransmit) tx++;
                    if (f.Channel == 0) c0++; else if (f.Channel == 1) c1++;
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, f.Channel);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), f.Identifier);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), f.TimestampNanoseconds);
                    header[16] = (byte)f.Flags;
                    header[17] = f.DeclaredDataLength;
                    digest.AppendData(header);
                    f.CopyPayloadTo(buf);
                    digest.AppendData(buf.AsSpan(0, f.DataLength));
                }
                var sha = Convert.ToHexString(digest.GetHashAndReset());
                sw.Stop();
                node["firstTimestampNs"] = first;
                node["lastTimestampNs"] = last;
                node["canonicalSha256"] = sha;
                node["outOfOrder"] = ooo;
                Check("first timestamp", first == expected.FirstTimestampNs, first, expected.FirstTimestampNs);
                Check("last timestamp", last == expected.LastTimestampNs, last, expected.LastTimestampNs);
                Check("out-of-order", ooo == 0, ooo, 0);
                Check("CAN FD", fd == expected.CanFd, fd, expected.CanFd);
                Check("BRS", brs == expected.BitRateSwitch, brs, expected.BitRateSwitch);
                Check("errors", err == expected.Errors, err, expected.Errors);
                Check("extended", ext == expected.Extended, ext, expected.Extended);
                Check("transmit", tx == expected.Transmit, tx, expected.Transmit);
                Check("channel0", c0 == expected.Channel0, c0, expected.Channel0);
                Check("channel1", c1 == expected.Channel1, c1, expected.Channel1);
                Check("sha256", sha == expected.CanonicalSha256, sha, expected.CanonicalSha256);
                End("verify", node, sw.Elapsed.TotalMilliseconds);
            }

            if (stages.Contains("stats"))
            {
                var node = Begin("stats");
                var sw = Stopwatch.StartNew();
                var rows = TraceStatistics.Compute(frames);
                sw.Stop();
                var total = rows.Sum(r => r.FrameCount);
                node["rows"] = rows.Count;
                Check("stats total", total == expected.Frames, total, expected.Frames);
                Check("stats unique ids", rows.Count == expected.UniqueChannelIds, rows.Count, expected.UniqueChannelIds);
                End("stats", node, sw.Elapsed.TotalMilliseconds);
            }

            if (stages.Contains("dbc"))
            {
                var db = new DbcParser().ParseFile(dbcPath);
                var decoder = new DbcDecoder(db);
                var node = Begin("dbc");
                var sw = Stopwatch.StartNew();
                long decodedFrames = 0, values = 0, raw = 0;
                double phys = 0;
                for (var i = 0; i < frames.Count; i++)
                {
                    var f = frames[i];
                    if (f.IsError) continue;
                    var decoded = decoder.Decode(f);
                    if (decoded.Count == 0) continue;
                    decodedFrames++;
                    foreach (var v in decoded)
                    {
                        values++;
                        raw += (long)v.RawValue;
                        phys += v.PhysicalValue;
                    }
                }
                sw.Stop();
                node["decodedFrames"] = decodedFrames;
                node["signalValues"] = values;
                node["rawSum"] = raw;
                node["physicalSum"] = phys;
                node["framesPerSecond"] = Math.Round(frames.Count / sw.Elapsed.TotalSeconds);
                Check("dbc decoded frames", decodedFrames == expected.DbcDecodedFrames, decodedFrames, expected.DbcDecodedFrames);
                Check("dbc signal values", values == expected.DbcSignalValues, values, expected.DbcSignalValues);
                Check("dbc raw checksum", raw == expected.DbcRawSum, raw, expected.DbcRawSum);
                Check("dbc physical checksum", phys == expected.DbcPhysicalSum, phys, expected.DbcPhysicalSum);
                End("dbc", node, sw.Elapsed.TotalMilliseconds);
            }

            if (stages.Contains("uds"))
            {
                var node = Begin("uds");
                var sw = Stopwatch.StartNew();
                var tx = UdsTransactionAnalyzer.Analyze(frames, null);
                sw.Stop();
                long vin = 0, session = 0, neg = 0, reqSum = 0, respSum = 0, positive = 0;
                foreach (var t in tx)
                {
                    if (t.Request is { } rq) { foreach (var b in rq.Payload) reqSum += b; }
                    foreach (var rs in t.Responses) foreach (var b in rs.Payload) respSum += b;
                    if (t.Outcome == DiagnosticOutcome.Positive) positive++;
                    if (t.Outcome == DiagnosticOutcome.Negative) neg++;
                    var sid = t.Request?.Payload.FirstOrDefault() ?? 0;
                    if (sid == 0x22 && t.Responses.Count > 0 && t.Responses[0].Payload[0] == 0x62) vin++;
                    if (sid == 0x10) session++;
                }
                node["transactions"] = tx.Count;
                node["vinReads"] = vin;
                node["sessionControls"] = session;
                node["negative"] = neg;
                node["positive"] = positive;
                Check("uds transactions", tx.Count == expected.UdsTransactions, tx.Count, expected.UdsTransactions);
                Check("uds VIN reads", vin == expected.UdsVinReads, vin, expected.UdsVinReads);
                Check("uds session controls", session == expected.UdsSessionControls, session, expected.UdsSessionControls);
                Check("uds negative", neg == expected.UdsNegativeResponses, neg, expected.UdsNegativeResponses);
                Check("uds request byte checksum", reqSum == expected.UdsRequestByteSum, reqSum, expected.UdsRequestByteSum);
                Check("uds response byte checksum", respSum == expected.UdsResponseByteSum, respSum, expected.UdsResponseByteSum);
                End("uds", node, sw.Elapsed.TotalMilliseconds);
            }

            if (stages.Contains("seek"))
            {
                var node = Begin("seek");
                var swAll = Stopwatch.StartNew();
                var rng = new Random(20261111);
                var n = frames.Count;
                var payload = new byte[64];
                var got = new byte[64];

                // (1) random frame access, each verified against the independent generator
                const int accessCount = 200_000;
                var lat = new double[accessCount];
                var picks = new int[accessCount];
                for (var k = 0; k < accessCount; k++) picks[k] = rng.Next(n);
                var fetched = new CanFrame[accessCount];
                for (var k = 0; k < accessCount; k++)
                {
                    var t0 = Stopwatch.GetTimestamp();
                    fetched[k] = frames[picks[k]];
                    lat[k] = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
                }
                long badAccess = 0;
                for (var k = 0; k < accessCount; k++)
                {
                    payload.AsSpan().Clear();
                    var r = Model.Describe(picks[k], payload);
                    var f = fetched[k];
                    f.CopyPayloadTo(got);
                    if (f.TimestampNanoseconds != r.Ts || f.Identifier != r.Id || f.Channel != r.Channel ||
                        f.DataLength != r.Len || f.IsError != r.Err ||
                        !got.AsSpan(0, r.Len).SequenceEqual(payload.AsSpan(0, r.Len)))
                        badAccess++;
                }
                node["randomAccess"] = Percentiles(lat, "us");
                Check("random access content", badAccess == 0, badAccess, 0);

                // (2) time seek: binary search of the first frame at/after a timestamp
                const int timeSeeks = 5_000;
                var tlat = new double[timeSeeks];
                long badTime = 0;
                for (var k = 0; k < timeSeeks; k++)
                {
                    var target = rng.Next(n);
                    var ts = Model.Timestamp(target);
                    var t0 = Stopwatch.GetTimestamp();
                    int lo = 0, hi = n - 1;
                    while (lo < hi)
                    {
                        var mid = lo + (hi - lo) / 2;
                        if (frames[mid].TimestampNanoseconds < ts) lo = mid + 1; else hi = mid;
                    }
                    tlat[k] = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
                    if (lo != target) badTime++;
                }
                node["timeSeek"] = Percentiles(tlat, "us");
                Check("time seek result", badTime == 0, badTime, 0);

                // (3) latest-per-ID replay index: forward playback ticks, then random seeks
                var index = new TraceFrameSelectionIndex(frames);
                var tick = new double[200];
                var through = 0;
                for (var k = 0; k < tick.Length; k++)
                {
                    through += n / 4000;
                    var t0 = Stopwatch.GetTimestamp();
                    var latest = index.LatestIndicesThrough(through);
                    tick[k] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    if (latest.Any(x => x > through)) problems.Add("latest-ID returned a future frame");
                }
                node["latestIdForwardTick"] = Percentiles(tick, "ms");
                var seeksLat = new List<double>();
                for (var k = 0; k < 12; k++)
                {
                    var target = rng.Next(n);
                    var t0 = Stopwatch.GetTimestamp();
                    var latest = index.LatestIndicesThrough(target);
                    seeksLat.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                    if (latest.Any(x => x > target)) problems.Add("latest-ID seek returned a future frame");
                }
                node["latestIdRandomSeek"] = Percentiles(seeksLat.ToArray(), "ms");
                var endLatest = index.LatestIndicesThrough(n - 1);
                Check("latest-ID distinct keys at end", endLatest.Length == expected.UniqueChannelIds, endLatest.Length, expected.UniqueChannelIds);

                // (4) exact-ID filter: whole-trace pass, count verified against generator
                var sf = Stopwatch.StartNew();
                var hits = index.BuildMatchingIndex(f => f.Identifier == 0x100u && !f.IsError);
                sf.Stop();
                long expectHits = 0;
                for (long i = 0; i < n; i++)
                {
                    var j = (int)(i % 1000);
                    if (i % 40 == 0 && !Model.IsDiagSlot(j)) expectHits++;
                }
                node["exactIdFilterMs"] = Math.Round(sf.Elapsed.TotalMilliseconds, 1);
                node["exactIdFilterHits"] = hits.Length;
                Check("exact-ID filter hits", hits.Length == expectHits, hits.Length, expectHits);

                swAll.Stop();
                End("seek", node, swAll.Elapsed.TotalMilliseconds);
            }

            if (stages.Contains("timeline"))
            {
                var db = new DbcParser().ParseFile(dbcPath);
                var decoder = new DbcDecoder(db);
                var node = Begin("timeline");
                var sw = Stopwatch.StartNew();
                var timelines = SignalTimelineBuilder.Build(frames, new[] { decoder });
                sw.Stop();
                long samples = 0;
                double phys = 0;
                foreach (var t in timelines)
                {
                    samples += t.Samples.Count;
                    foreach (var s in t.Samples) phys += s.Value;
                }
                node["timelines"] = timelines.Count;
                node["samples"] = samples;
                Check("timeline samples", samples == expected.DbcSignalValues, samples, expected.DbcSignalValues);
                Check("timeline physical checksum", phys == expected.DbcPhysicalSum, phys, expected.DbcPhysicalSum);
                End("timeline", node, sw.Elapsed.TotalMilliseconds);
                GC.KeepAlive(timelines);
            }
        }
        finally
        {
            result["correct"] = problems.Count == 0;
            result["problems"] = new JsonArray(problems.Select(p => (JsonNode)p).ToArray());
            Save();
            collection?.Dispose();
        }
        Console.WriteLine(problems.Count == 0 ? "OK" : "MISMATCH: " + string.Join("; ", problems));
        return problems.Count == 0 ? 0 : 3;
    }

    private static JsonObject Percentiles(double[] values, string unit)
    {
        var s = (double[])values.Clone();
        Array.Sort(s);
        double P(double q) => s[Math.Min(s.Length - 1, (int)(q * s.Length))];
        return new JsonObject
        {
            ["unit"] = unit,
            ["samples"] = s.Length,
            ["p50"] = Math.Round(P(0.50), 3),
            ["p95"] = Math.Round(P(0.95), 3),
            ["p99"] = Math.Round(P(0.99), 3),
            ["max"] = Math.Round(s[^1], 3)
        };
    }
}
