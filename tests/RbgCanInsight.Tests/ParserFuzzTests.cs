using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml;
using RbgCanInsight.Analysis.Projects;
using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;
using Xunit.Abstractions;

namespace RbgCanInsight.Tests;

/// <summary>
/// Deterministic, seeded mutation fuzzing of every file parser plus property-style exporter -> importer
/// round trips. Allowed outcomes for hostile input: success or a documented "bad input" exception
/// (InvalidData / Format / NotSupported / Argument-family / Xml / IO / Cancellation / ProjectFormat).
/// Anything else (NullReference, IndexOutOfRange, InvalidCast, Overflow, OutOfMemory, hang > 5 s,
/// > 300 MB allocated for one small input) is a robustness defect. Real files under
/// artifacts/online-samples are used as extra seeds when present (silently skipped otherwise).
/// </summary>
public sealed class ParserFuzzTests : IDisposable
{
    private const int Iterations = 500;
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromSeconds(5);
    private const long MaxAllocatedBytes = 300L * 1024 * 1024;

    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rbg-fuzz-" + Guid.NewGuid().ToString("N"));

    public ParserFuzzTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------ harness

    private sealed record Seed(string Extension, byte[] Data, string Kind);

    private sealed record Failure(string Key, string Parser, string Detail, byte[] Input, string Extension);

    private static bool IsAllowed(Exception ex) => ex is
        InvalidDataException or FormatException or NotSupportedException or ArgumentException
        or XmlException or IOException or OperationCanceledException or ProjectFormatException
        or DbcParseException;

    private static string Frame(Exception ex)
    {
        var line = (ex.StackTrace ?? "").Split('\n')
            .FirstOrDefault(l => l.Contains("RbgCanInsight", StringComparison.Ordinal) && !l.Contains("ParserFuzzTests", StringComparison.Ordinal))
            ?? (ex.StackTrace ?? "?").Split('\n').FirstOrDefault() ?? "?";
        return line.Trim();
    }

    private int RunFuzz(
        string parser, int seedBase, IReadOnlyList<Seed> seeds,
        Func<string, CancellationToken, Task> run, int iterations = Iterations)
    {
        var failures = new ConcurrentDictionary<string, Failure>();
        var outcomes = new ConcurrentDictionary<string, int>();
        var small = seeds.Where(s => s.Data.Length <= 100_000).ToList();
        var big = seeds.Where(s => s.Data.Length > 100_000).ToList();
        Assert.NotEmpty(small);
        var total = Stopwatch.StartNew(); long tM = 0, tW = 0, tR = 0; // tM/tW/tR: summed thread-ms

        Parallel.For(0, iterations, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) }, i =>
        {
            var rng = new Random(unchecked(seedBase * 100_003 + i));
            // Large real-world files are expensive; use them for every 40th iteration only.
            var pool = big.Count > 0 && i % 50 == 7 ? big : small;
            var seed = pool[rng.Next(pool.Count)];
            var swm = Stopwatch.StartNew(); var input = Mutate(seed.Data, rng, seed.Kind); Interlocked.Add(ref tM, swm.ElapsedMilliseconds); swm.Restart();
            var path = Path.Combine(_dir, $"{parser}-{i}{seed.Extension}");
            File.WriteAllBytes(path, input); Interlocked.Add(ref tW, swm.ElapsedMilliseconds); swm.Restart();

            var allocBefore = GC.GetTotalAllocatedBytes(false);
            using var cts = new CancellationTokenSource(ParseTimeout);
            var task = Task.Run(() => run(path, cts.Token));
            Exception? error = null;
            var finished = false;
            try { finished = task.Wait(ParseTimeout + TimeSpan.FromSeconds(2)); }
            catch (AggregateException ae) { finished = true; error = ae.InnerException; }

            outcomes.AddOrUpdate(error is null ? "ok" : error.GetType().Name, 1, (_, n) => n + 1);
            string? key = null, detail = null;
            if (!finished)
            {
                key = "HANG (> 5 s, token ignored)";
                detail = "parser did not return within the timeout";
            }
            else if (error is not null)
            {
                if (error is OperationCanceledException && cts.IsCancellationRequested)
                {
                    key = "HANG (> 5 s, cancelled by timeout)";
                    detail = error.Message;
                }
                else if (!IsAllowed(error))
                {
                    key = error.GetType().Name + " @ " + Frame(error);
                    detail = error.Message;
                }
            }

            Interlocked.Add(ref tR, swm.ElapsedMilliseconds);
            var allocated = GC.GetTotalAllocatedBytes(false) - allocBefore;
            if (key is null && allocated > MaxAllocatedBytes)
            {
                // GC.GetTotalAllocatedBytes is process-wide, so other concurrently running tests (and sibling
                // fuzz iterations) can inflate it. A genuine runaway allocation is deterministic: re-run the same
                // input and keep the minimum of three measurements.
                for (var retry = 0; retry < 2 && allocated > MaxAllocatedBytes; retry++)
                {
                    var before = GC.GetTotalAllocatedBytes(false);
                    using var retryCts = new CancellationTokenSource(ParseTimeout);
                    try { Task.Run(() => run(path, retryCts.Token)).Wait(ParseTimeout); } catch (AggregateException) { }
                    allocated = Math.Min(allocated, GC.GetTotalAllocatedBytes(false) - before);
                }
            }
            if (key is null && allocated > MaxAllocatedBytes)
            {
                key = "RUNAWAY ALLOCATION";
                detail = $"{allocated / (1024 * 1024)} MB allocated for a {input.Length} byte input";
            }

            if (key is not null)
            {
                failures.TryAdd($"{parser}: {key}", new Failure($"{parser}: {key}", parser, detail!, input, seed.Extension));
            }
            File.Delete(path);
        });

        _output.WriteLine($"{parser}: {iterations} iterations in {total.ElapsedMilliseconds} ms, {failures.Count} distinct failure class(es) [thread-ms: mutate {tM}, write {tW}, run {tR}] outcomes: {string.Join(", ", outcomes.OrderBy(o => o.Key).Select(o => o.Key + "=" + o.Value))}");
        foreach (var f in failures.Values)
        {
            var hex = Convert.ToHexString(f.Input.AsSpan(0, Math.Min(f.Input.Length, 96)));
            _output.WriteLine($"  FAIL {f.Key} | {f.Detail} | input({f.Input.Length}B, {f.Extension}) head={hex}");
            DumpFailure(f);
        }

        Assert.True(failures.IsEmpty,
            "Fuzz failures:\n" + string.Join("\n", failures.Values.Select(f => $"  {f.Key} - {f.Detail}")));
        return iterations;
    }

    private static void DumpFailure(Failure f)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "rbg-fuzz-failures");
            Directory.CreateDirectory(dir);
            var name = new string((f.Key.Replace(' ', '_').Select(c => char.IsLetterOrDigit(c) ? c : '_')).Take(60).ToArray());
            File.WriteAllBytes(Path.Combine(dir, name + f.Extension), f.Input);
        }
        catch (IOException) { /* diagnostics only */ }
    }

    private static async Task Drain(IAsyncEnumerable<CanFrame> frames, CancellationToken ct)
    {
        var count = 0L;
        await foreach (var frame in frames.WithCancellation(ct))
        {
            // Invariants every parser must keep even for hostile input.
            Assert.InRange((int)frame.DataLength, 0, 64);
            Assert.True(frame.Channel >= 0);
            if (++count > 2_000_000) break;
        }
    }

    // ------------------------------------------------------------------ mutation engine

    private static readonly string[] ExtremeTokens =
    [
        "99999999999999999999999999", "-1", "-9223372036854775808", "9223372036854775807", "4294967296",
        "4294967295", "2147483648", "1e400", "-1e400", "NaN", "Infinity", "0x", "0xFFFFFFFFFF", "1.7976931348623157E+308",
        ".", "-", "+", "\"", "'", ";", "#", "##", "\\", "%s%n", new string('9', 5000), new string('A', 3000),
        "\u0000", "\uFFFF", "\uD800", "1/0", "00000000000000000000000000000000000000000000000001"
    ];

    private static readonly byte[][] InvalidUtf8 =
    [
        [0xC3, 0x28], [0xFF, 0xFE, 0xFD], [0xED, 0xA0, 0x80], [0xF8, 0x88, 0x80, 0x80, 0x80], [0xC0, 0xAF], [0xE2, 0x82], [0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF]
    ];

    private static readonly uint[] HugeValues =
        [0xFFFF_FFFF, 0x7FFF_FFFF, 0x8000_0000, 0x1000_0000, 0xFFFF_FF00, 0x0400_0000, 0x0000_0000, 0x0000_0001, 0x4000_0001];

    internal static byte[] Mutate(byte[] seed, Random rng, string kind)
    {
        var data = seed.ToList();
        var ops = rng.Next(1, 5);
        for (var op = 0; op < ops; op++)
        {
            if (data.Count == 0) data.Add((byte)rng.Next(256));
            switch (rng.Next(13))
            {
                case 0: // bit flips
                    for (var n = rng.Next(1, 9); n > 0; n--)
                        data[rng.Next(data.Count)] ^= (byte)(1 << rng.Next(8));
                    break;
                case 1: // delete range
                {
                    var at = rng.Next(data.Count);
                    data.RemoveRange(at, Math.Min(rng.Next(1, 64), data.Count - at));
                    break;
                }
                case 2: // insert random bytes
                {
                    var bytes = new byte[rng.Next(1, 33)];
                    rng.NextBytes(bytes);
                    data.InsertRange(rng.Next(data.Count + 1), bytes);
                    break;
                }
                case 3: // truncate at random offset
                {
                    var cut = rng.Next(data.Count + 1);
                    data.RemoveRange(cut, data.Count - cut);
                    break;
                }
                case 4: // duplicate a chunk
                {
                    var at = rng.Next(data.Count);
                    var len = Math.Min(rng.Next(1, 4096), data.Count - at);
                    data.InsertRange(rng.Next(data.Count + 1), data.GetRange(at, len));
                    break;
                }
                case 5: // huge declared 32-bit length / count (prefer aligned header positions)
                {
                    if (data.Count < 4) break;
                    var at = rng.Next(3) == 0 ? rng.Next(data.Count - 3) : rng.Next((Math.Min(data.Count, 512) - 3) / 4) * 4;
                    var v = HugeValues[rng.Next(HugeValues.Length)];
                    for (var b = 0; b < 4; b++) data[at + b] = (byte)(v >> (8 * b));
                    if (rng.Next(2) == 0) { for (var b = 0; b < 4; b++) data[at + b] = (byte)(v >> (8 * (3 - b))); }
                    break;
                }
                case 6: // huge declared 64-bit length
                {
                    if (data.Count < 8) break;
                    var at = rng.Next(3) == 0 ? rng.Next(data.Count - 7) : rng.Next((Math.Min(data.Count, 512) - 7) / 8) * 8;
                    var v = rng.Next(3) switch { 0 => ulong.MaxValue, 1 => (ulong)long.MaxValue, _ => 0x0000_0001_0000_0000UL };
                    for (var b = 0; b < 8; b++) data[at + b] = (byte)(v >> (8 * b));
                    break;
                }
                case 7: // NUL bytes
                    data.InsertRange(rng.Next(data.Count + 1), new byte[rng.Next(1, 40)]);
                    break;
                case 8: // invalid UTF-8
                    data.InsertRange(rng.Next(data.Count + 1), InvalidUtf8[rng.Next(InvalidUtf8.Length)]);
                    break;
                case 9: // extreme numbers / strings at a token boundary
                {
                    var at = NextBoundary(data, rng.Next(data.Count + 1));
                    var token = Encoding.UTF8.GetBytes(ExtremeTokens[rng.Next(ExtremeTokens.Length)]);
                    // Often replace the token that follows, otherwise plain insertion.
                    if (rng.Next(2) == 0) { var end = NextBoundary(data, Math.Min(at + 1, data.Count)); data.RemoveRange(at, Math.Max(0, end - at)); }
                    data.InsertRange(at, token);
                    break;
                }
                case 10: // very long line (100k characters)
                {
                    if (kind is "binary") break;
                    var at = NextLineStart(data, rng.Next(data.Count + 1));
                    var fill = (byte)"A0 ,;"[rng.Next(5)];
                    var line = Enumerable.Repeat(fill, 100_000).ToList();
                    line.Add((byte)'\n');
                    data.InsertRange(at, line);
                    break;
                }
                case 11: // deep nesting
                {
                    if (kind is "xml" or "json")
                    {
                        var open = kind == "xml" ? "<a>" : "[";
                        var depth = rng.Next(0, 2) == 0 ? 500 : 5_000;
                        var bytes = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(open, depth)));
                        data.InsertRange(kind == "xml" ? NextXmlContentPosition(data, rng) : NextBoundary(data, rng.Next(data.Count + 1)), bytes);
                    }
                    break;
                }
                default: // overwrite a byte with an interesting value
                    data[rng.Next(data.Count)] = new byte[] { 0x00, 0xFF, 0x7F, 0x80, 0x0A, 0x0D, 0x20, 0x22 }[rng.Next(8)];
                    break;
            }
        }
        return data.ToArray();
    }

    private static int NextBoundary(List<byte> data, int from)
    {
        for (var i = from; i < data.Count; i++)
            if (data[i] is (byte)' ' or (byte)',' or (byte)'\n' or (byte)'\t' or (byte)'>' or (byte)'"' or (byte)';')
                return i;
        return data.Count;
    }

    private static int NextLineStart(List<byte> data, int from)
    {
        for (var i = from; i < data.Count; i++) if (data[i] == '\n') return i + 1;
        return data.Count;
    }

    private static int NextXmlContentPosition(List<byte> data, Random rng)
    {
        var from = rng.Next(data.Count + 1);
        for (var i = from; i < data.Count; i++) if (data[i] == '>') return i + 1;
        return data.Count;
    }

    // ------------------------------------------------------------------ seed builders

    private static List<CanFrame> RandomFrames(Random rng, int count, bool allowAnything)
    {
        var frames = new List<CanFrame>();
        long ts = rng.Next(0, 1000) * 1000L;
        for (var i = 0; i < count; i++)
        {
            var flags = CanFrameFlags.None;
            if (rng.Next(3) == 0) flags |= CanFrameFlags.Extended;
            if (allowAnything && rng.Next(8) == 0) flags |= CanFrameFlags.Remote;
            else if (allowAnything && rng.Next(10) == 0) flags |= CanFrameFlags.Error;
            if (!flags.HasFlag(CanFrameFlags.Remote) && !flags.HasFlag(CanFrameFlags.Error) && rng.Next(4) == 0)
            {
                flags |= CanFrameFlags.FlexibleDataRate;
                if (rng.Next(2) == 0) flags |= CanFrameFlags.BitRateSwitch;
            }
            var id = (uint)rng.NextInt64(0, flags.HasFlag(CanFrameFlags.Extended) ? 0x2000_0000L : 0x800L);
            byte[] payload;
            byte? declared = null;
            if (flags.HasFlag(CanFrameFlags.Remote))
            {
                payload = [];
                declared = (byte)rng.Next(0, 9);
            }
            else
            {
                int[] fdLengths = [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64];
                var len = flags.HasFlag(CanFrameFlags.FlexibleDataRate) ? fdLengths[rng.Next(fdLengths.Length)] : rng.Next(0, 9);
                payload = new byte[len];
                rng.NextBytes(payload);
            }
            ts += rng.Next(0, 50_000) * 1000L; // whole microseconds: the finest unit every text format keeps
            frames.Add(new CanFrame(rng.Next(0, 8), id, payload, ts, flags, i + 1, declared));
        }
        return frames;
    }

    private async Task<byte[]> Export(string ext, IEnumerable<CanFrame> frames)
    {
        var path = Path.Combine(_dir, "seed-" + Guid.NewGuid().ToString("N") + ext);
        switch (ext)
        {
            case ".asc": await TraceExporter.WriteAscAsync(path, frames); break;
            case ".csv": await TraceExporter.WriteCsvAsync(path, frames); break;
            case ".trc": await TraceExporter.WriteTrcAsync(path, frames); break;
            case ".blf": await TraceExporter.WriteBlfAsync(path, frames); break;
            case ".mf4": await Mdf4Writer.WriteAsync(path, frames); break;
            default: throw new NotSupportedException(ext);
        }
        var bytes = await File.ReadAllBytesAsync(path);
        File.Delete(path);
        return bytes;
    }

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    private static IEnumerable<Seed> OnlineSeeds(string folder, string pattern, string kind, long maxBytes = 1_000_000)
    {
        var root = FindOnlineSamples();
        if (root is null) yield break;
        var dir = Path.Combine(root, folder);
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, pattern).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (new FileInfo(file).Length > maxBytes) continue;
            yield return new Seed(Path.GetExtension(file), File.ReadAllBytes(file), kind);
        }
    }

    private static string? FindOnlineSamples()
    {
        var env = Environment.GetEnvironmentVariable("RBG_ONLINE_SAMPLES");
        if (env is not null && Directory.Exists(env)) return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "artifacts", "online-samples");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private async Task<List<Seed>> TraceSeeds(string ext, string kind)
    {
        var seeds = new List<Seed>();
        foreach (var n in new[] { 1, 6, 25 })
            seeds.Add(new Seed(ext, await Export(ext, RandomFrames(new Random(n), n * 4, ext is ".blf" or ".trc" ? true : false)), kind));
        seeds.AddRange(OnlineSeeds("python-can", "*" + ext, kind));
        return seeds;
    }

    private const string CandumpSeed =
        "(1612345678.123456) can0 123#DEADBEEF\n(1612345678.223456) can1 18DAF110#0210030000000000\n" +
        "(1612345679.000000) can0 7FF##1001122334455667788\n(1612345679.100000) can0 123#R\n(1612345679.2) vcan0 1FFFFFFF#\n" +
        "can0  123   [3]  AA BB CC\n";

    private const string DbcSeed = """
        VERSION "1.0"

        NS_ :
            NS_DESC_
            CM_
            BA_DEF_
            BA_
            VAL_

        BS_:

        BU_: ECU1 ECU2

        BO_ 256 Engine: 8 ECU1
         SG_ Rpm : 0|16@1+ (0.25,0) [0|16383.75] "rpm" ECU2
         SG_ Temp : 16|8@1+ (1,-40) [-40|215] "degC" ECU2
         SG_ Mux M : 24|4@1+ (1,0) [0|15] "" ECU2
         SG_ MuxA m0 : 28|12@1- (0.1,0) [0|0] "" ECU2
        BO_ 2566844416 Ext: 8 ECU2
         SG_ Big : 63|64@0+ (1,0) [0|0] "" ECU1
        BO_ 3221225472 VECTOR__INDEPENDENT_SIG_MSG: 0 Vector__XXX
         SG_ Orphan : 0|1@1+ (1,0) [0|1] "" Vector__XXX

        CM_ SG_ 256 Rpm "Engine speed
        multi line";
        BA_DEF_ BO_ "GenMsgCycleTime" INT 0 65535;
        BA_ "GenMsgCycleTime" BO_ 256 10;
        VAL_ 256 Mux 0 "Off" 1 "On" ;
        SG_MUL_VAL_ 256 MuxA Mux 0-0;
        SIG_VALTYPE_ 256 Rpm : 1;
        """;

    private const string CddSeed = """
        <?xml version="1.0" encoding="utf-8"?>
        <CANDELA>
          <ECU qual="DemoEcu">
            <DIAGCLASS>
              <SERVICE>
                <NAME><TUV>Engine coolant temperature</TUV></NAME>
                <SHORTCUTQUAL>EngineCoolantTemperature_Read</SHORTCUTQUAL>
                <SEMANTIC>CURRENTDATA</SEMANTIC>
              </SERVICE>
              <STATICVALUE v="62469" />
              <SERVICE>
                <NAME><TUV>Write configuration</TUV></NAME>
                <SHORTCUTQUAL>Configuration_Write</SHORTCUTQUAL>
                <SEMANTIC>VARIANTCODINGWRITE</SEMANTIC>
              </SERVICE>
              <STATICVALUE v="62470" />
            </DIAGCLASS>
          </ECU>
        </CANDELA>
        """;

    private const string OdxSeed = """
        <?xml version="1.0" encoding="utf-8"?>
        <ODX MODEL-VERSION="2.2.0">
          <DIAG-LAYER-CONTAINER ID="dlc.1">
            <SHORT-NAME>DemoEcu</SHORT-NAME>
            <BASE-VARIANT ID="bv.1">
              <SHORT-NAME>DemoEcu</SHORT-NAME>
              <DIAG-DATA-DICTIONARY-SPEC>
                <UNITS><UNIT ID="unit.degc"><SHORT-NAME>DegreeCelsius</SHORT-NAME><DISPLAY-NAME>degC</DISPLAY-NAME></UNIT></UNITS>
                <DATA-OBJECT-PROPS>
                  <DATA-OBJECT-PROP ID="dop.temp">
                    <SHORT-NAME>Temperature</SHORT-NAME>
                    <COMPU-METHOD><CATEGORY>LINEAR</CATEGORY><COMPU-INTERNAL-TO-PHYS><COMPU-SCALES><COMPU-SCALE>
                      <COMPU-RATIONAL-COEFFS>
                        <COMPU-NUMERATOR><V>-40</V><V>1</V></COMPU-NUMERATOR>
                        <COMPU-DENOMINATOR><V>1</V></COMPU-DENOMINATOR>
                      </COMPU-RATIONAL-COEFFS></COMPU-SCALE></COMPU-SCALES></COMPU-INTERNAL-TO-PHYS></COMPU-METHOD>
                    <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32" xsi:type="STANDARD-LENGTH-TYPE" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
                    <UNIT-REF ID-REF="unit.degc" />
                  </DATA-OBJECT-PROP>
                </DATA-OBJECT-PROPS>
              </DIAG-DATA-DICTIONARY-SPEC>
              <DIAG-COMMS>
                <DIAG-SERVICE ID="svc.1" SEMANTIC="CURRENTDATA">
                  <SHORT-NAME>EngineCoolantTemperature_Read</SHORT-NAME>
                  <LONG-NAME>Engine coolant temperature</LONG-NAME>
                  <REQUEST-REF ID-REF="req.1" />
                  <POS-RESPONSE-REFS><POS-RESPONSE-REF ID-REF="resp.1" /></POS-RESPONSE-REFS>
                </DIAG-SERVICE>
              </DIAG-COMMS>
              <REQUESTS>
                <REQUEST ID="req.1"><PARAMS>
                  <PARAM SEMANTIC="SERVICE-ID" xsi:type="CODED-CONST" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                    <SHORT-NAME>SID</SHORT-NAME><CODED-VALUE>34</CODED-VALUE>
                    <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32" xsi:type="STANDARD-LENGTH-TYPE"><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
                  </PARAM>
                  <PARAM SEMANTIC="ID" xsi:type="CODED-CONST" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                    <SHORT-NAME>DID</SHORT-NAME><CODED-VALUE>62469</CODED-VALUE>
                    <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32" xsi:type="STANDARD-LENGTH-TYPE"><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE>
                  </PARAM>
                </PARAMS></REQUEST>
              </REQUESTS>
              <POS-RESPONSES>
                <POS-RESPONSE ID="resp.1"><PARAMS>
                  <PARAM SEMANTIC="DATA" xsi:type="VALUE" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                    <SHORT-NAME>Value</SHORT-NAME><DOP-REF ID-REF="dop.temp" />
                  </PARAM>
                </PARAMS></POS-RESPONSE>
              </POS-RESPONSES>
            </BASE-VARIANT>
          </DIAG-LAYER-CONTAINER>
        </ODX>
        """;

    private static byte[] PcapngSeed(bool bigEndian = false)
    {
        using var ms = new MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(b, v); else BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
        void U16(ushort v) { Span<byte> b = stackalloc byte[2]; if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(b, v); else BinaryPrimitives.WriteUInt16LittleEndian(b, v); ms.Write(b); }
        // Section Header Block
        U32(0x0A0D0D0A); U32(28); U32(0x1A2B3C4D); U16(1); U16(0); U32(0xFFFFFFFF); U32(0xFFFFFFFF); U32(28);
        // Interface Description Block (SocketCAN, no options)
        U32(1); U32(20); U16(227); U16(0); U32(0); U32(20);
        for (var n = 0; n < 4; n++)
        {
            var fd = n == 3;
            var payload = fd ? 72 : 16;
            var padded = payload;
            var total = (uint)(32 + padded);
            U32(6); U32(total); U32(0); U32(0); U32((uint)(1000 + n));
            U32((uint)payload); U32((uint)payload);
            var frame = new byte[payload];
            var canId = (uint)(0x123 + n) | (n == 1 ? 0x8000_0000u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(frame, bigEndian ? BinaryPrimitives.ReverseEndianness(canId) : canId);
            // SocketCAN's can_id is host-endian on the capture machine; the importer is expected to cope with LE.
            if (!bigEndian) BinaryPrimitives.WriteUInt32LittleEndian(frame, canId);
            frame[4] = fd ? (byte)12 : (byte)(n + 2);
            if (fd) frame[5] = 1;
            for (var b = 0; b < frame[4]; b++) frame[8 + b] = (byte)(0x10 * n + b);
            ms.Write(frame);
            U32(total);
        }
        return ms.ToArray();
    }

    private static byte[] ClassicPcapSeed()
    {
        using var ms = new MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
        void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); ms.Write(b); }
        U32(0xA1B2C3D4); U16(2); U16(4); U32(0); U32(0); U32(65535); U32(227);
        for (var n = 0; n < 3; n++)
        {
            U32((uint)(1_600_000_000 + n)); U32((uint)(n * 1000)); U32(16); U32(16);
            var frame = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)(0x100 + n));
            frame[4] = 8;
            for (var b = 0; b < 8; b++) frame[8 + b] = (byte)(n * 16 + b);
            ms.Write(frame);
        }
        return ms.ToArray();
    }

    private static byte[] PdxSeed()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("demo.odx-d");
            using var s = entry.Open();
            s.Write(Utf8(OdxSeed));
        }
        return ms.ToArray();
    }

    private static byte[] ProjectSeed(string dir)
    {
        var path = Path.Combine(dir, "seed.rbgproj");
        var p = new AnalysisProject { Name = "Demo" };
        p.TraceFiles.Add(new TraceFileRef { Path = "a.asc", Sha256 = new string('0', 64), Length = 12 });
        p.TraceFiles.Add(new TraceFileRef { Path = "C:/abs/b.blf", External = true });
        p.DbcFiles.Add(new DbcFileRef { Path = "../x.dbc", BusChannel = 1, J1939Pgn = true });
        p.CatalogFiles.Add(new CatalogFileRef { Path = "sub/c.cdd" });
        p.Filters.Add(new SavedFilter { Name = "f", SearchText = "123", FilterExpression = "id == 0x123" });
        p.Markers.Add(new ProjectMarker { FrameIndex = 5, TimestampSeconds = 1.5, Text = "mark", Category = "c" });
        p.Annotations.Add(new ProjectAnnotation { Id = "1", StartSeconds = 0.5, EndSeconds = 1, Title = "t", Note = "n" });
        p.PlottedSignals.Add(new PlottedSignal { Message = "Engine", Signal = "Rpm", Channel = 0, Identifier = 0x100 });
        ProjectStore.Save(path, p);
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return bytes;
    }

    // ------------------------------------------------------------------ fuzz tests

    [Fact]
    public async Task Fuzz_Asc()
    {
        var seeds = await TraceSeeds(".asc", "text");
        RunFuzz("asc", 11, seeds, (p, ct) => Drain(TraceImporter.ReadAsync(p, ct, new TraceImportHealth()), ct));
    }

    [Fact]
    public async Task Fuzz_Csv()
    {
        var seeds = await TraceSeeds(".csv", "text");
        seeds.Add(new Seed(".csv", Utf8("time_s,channel,id_hex,dlc,data\n1.0,0,123,3,AABBCC\n"), "text"));
        RunFuzz("csv", 12, seeds, (p, ct) => Drain(TraceImporter.ReadAsync(p, ct), ct));
    }

    [Fact]
    public async Task Fuzz_Trc()
    {
        var seeds = await TraceSeeds(".trc", "text");
        RunFuzz("trc", 13, seeds, (p, ct) => Drain(TraceImporter.ReadAsync(p, ct, new TraceImportHealth()), ct));
    }

    [Fact]
    public void Fuzz_CandumpLog()
    {
        var seeds = new List<Seed> { new(".log", Utf8(CandumpSeed), "text"), new(".txt", Utf8(CandumpSeed), "text") };
        RunFuzz("candump", 14, seeds, (p, ct) => Drain(TraceImporter.ReadAsync(p, ct), ct));
    }

    [Fact]
    public async Task Fuzz_Blf()
    {
        var seeds = await TraceSeeds(".blf", "binary");
        RunFuzz("blf", 15, seeds, (p, ct) => Drain(TraceImporter.ReadAsync(p, ct, new TraceImportHealth()), ct));
    }

    [Fact]
    public async Task Fuzz_Mf4()
    {
        var seeds = await TraceSeeds(".mf4", "binary");
        RunFuzz("mf4", 16, seeds, (p, ct) => Drain(TraceImporter.ReadAsync(p, ct, new TraceImportHealth()), ct));
    }

    [Fact]
    public void Fuzz_PcapAndPcapng()
    {
        var seeds = new List<Seed>
        {
            new(".pcapng", PcapngSeed(), "binary"),
            new(".pcapng", PcapngSeed(bigEndian: true), "binary"),
            new(".pcap", ClassicPcapSeed(), "binary"),
        };
        RunFuzz("pcap", 17, seeds, (p, ct) => Drain(TraceImporter.ReadAsync(p, ct, new TraceImportHealth()), ct));
    }

    [Fact]
    public void Fuzz_DbcParser()
    {
        var seeds = new List<Seed> { new(".dbc", Utf8(DbcSeed), "text") };
        seeds.AddRange(OnlineSeeds("opendbc", "*.dbc", "text"));
        RunFuzz("dbc", 18, seeds, (p, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            new DbcParser().ParseFile(p);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Fuzz_CddCatalogReader()
    {
        var seeds = new List<Seed> { new(".cdd", Utf8(CddSeed), "xml") };
        seeds.AddRange(OnlineSeeds("cdd-real", "*.cdd", "xml", 2_000_000));
        RunFuzz("cdd", 19, seeds, (p, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            CddCatalogReader.Load(p);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Fuzz_OdxAndPdxCatalogReader()
    {
        var seeds = new List<Seed>
        {
            new(".odx", Utf8(OdxSeed), "xml"),
            new(".pdx", PdxSeed(), "binary"),
        };
        seeds.AddRange(OnlineSeeds("odx", "*.pdx", "binary"));
        seeds.AddRange(OnlineSeeds(Path.Combine("odx", "extracted"), "*.odx-d", "xml").Select(s => s with { Extension = ".odx" }));
        RunFuzz("odx", 20, seeds, (p, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            OdxCatalogReader.LoadAll(p);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Fuzz_ProjectStore()
    {
        var seeds = new List<Seed> { new(".rbgproj", ProjectSeed(_dir), "json") };
        RunFuzz("project", 21, seeds, (p, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            ProjectStore.Load(p);
            return Task.CompletedTask;
        });
    }

    private static string NestedXml(string root, int depth) =>
        $"<?xml version=\"1.0\"?><{root}><ECU qual=\"x\">" + string.Concat(Enumerable.Repeat("<a>", depth)) +
        string.Concat(Enumerable.Repeat("</a>", depth)) + $"</ECU></{root}>";

    [Theory]
    [InlineData(1_000)]
    [InlineData(10_000)]
    public void DeeplyNestedXml_IsRejectedOrParsedQuickly(int depth)
    {
        foreach (var (root, ext, load) in new (string, string, Action<string>)[]
        {
            ("CANDELA", ".cdd", p => CddCatalogReader.Load(p)),
            ("ODX", ".odx", p => OdxCatalogReader.LoadAll(p)),
        })
        {
            var path = Path.Combine(_dir, $"nest{depth}{ext}");
            File.WriteAllText(path, NestedXml(root, depth));
            var sw = Stopwatch.StartNew();
            try { load(path); }
            catch (Exception ex) when (IsAllowed(ex)) { }
            Assert.True(sw.Elapsed < ParseTimeout, $"{ext} depth {depth} took {sw.ElapsedMilliseconds} ms");
        }
    }

    /// <summary>
    /// KNOWN DEFECT (reported, not fixed here): XDocument.Load is super-linear in nesting depth, and neither
    /// CddCatalogReader.Load (CddCatalogReader.cs, XDocument.Load after XmlReader.Create) nor
    /// OdxCatalogReader.ReadSecureDocument bounds depth or accepts a CancellationToken. A ~600 KB file made of
    /// 100 000 nested elements takes 20-60 s of CPU (10 000 deep: ~0.1 s). Un-skip after adding a MaxDepth guard.
    /// </summary>
    [Fact]
    public void DeeplyNestedXml_100kDepth_MustFailFast()
    {
        var path = Path.Combine(_dir, "nest100k.cdd");
        File.WriteAllText(path, NestedXml("CANDELA", 100_000));
        var sw = Stopwatch.StartNew();
        try { CddCatalogReader.Load(path); }
        catch (Exception ex) when (IsAllowed(ex)) { }
        Assert.True(sw.Elapsed < ParseTimeout, $"took {sw.ElapsedMilliseconds} ms");
    }

    // ------------------------------------------------------------------ round trips

        /// <summary>KNOWN LOSSY (minor): the text importers convert seconds/milliseconds doubles with a truncating
    /// <c>(long)(value * 1e9)</c> cast (TraceImporter.cs lines 233/277/288/372, TrcImporter.cs 132/248), so a
    /// timestamp such as 64.161000 s comes back as 64160999 ns (1 ns low). Text formats are therefore compared
    /// with a 1 ns tolerance; BLF and MF4 store integers/binary doubles and are compared exactly.</summary>
    private static long Tolerance(string ext) => ext is ".asc" or ".csv" or ".trc" ? 1 : 0;

    private static bool SameFrame(CanFrame a, CanFrame b, long timestampToleranceNs = 0) =>
        a.Channel == b.Channel && a.Identifier == b.Identifier && a.Flags == b.Flags &&
        a.DataLength == b.DataLength && a.DeclaredDataLength == b.DeclaredDataLength &&
        a.GetPayload().AsSpan().SequenceEqual(b.GetPayload()) &&
        Math.Abs(a.TimestampNanoseconds - b.TimestampNanoseconds) <= timestampToleranceNs;

    private static string Describe(CanFrame f) =>
        $"ch{f.Channel} id={f.Identifier:X} fl={f.Flags} dlen={f.DataLength}/{f.DeclaredDataLength} ts={f.TimestampNanoseconds} data={Convert.ToHexString(f.GetPayload())}";

    private async Task<List<CanFrame>> ReadBack(string path)
    {
        var list = new List<CanFrame>();
        await foreach (var f in TraceImporter.ReadAsync(path)) list.Add(f);
        return list;
    }

    /// <summary>Documented lossy cases per format; frames for which this returns null must round-trip exactly,
    /// otherwise the returned string is the reason the format cannot represent the frame.</summary>
    private static string? LossReason(string ext, CanFrame f) => ext switch
    {
        // ASC writer has no CAN FD syntax and no error-frame syntax: it emits classic "d"/"r" lines.
        ".asc" => f.Flags.HasFlag(CanFrameFlags.Error) ? "ASC writer emits no error-frame lines"
            : f.Flags.HasFlag(CanFrameFlags.FlexibleDataRate) ? "ASC writer has no CAN FD syntax"
            : null,
        // CSV carries time_s, channel, id_hex, dlc, data: no flags at all (no extended/remote/error/FD marker).
        ".csv" => f.Flags != CanFrameFlags.None ? "CSV has no flag column (extended/remote/error/FD are not stored)" : null,
        // TRC 2.1 as written has no FD or error rows and no extended marker beyond the 8-digit identifier.
        ".trc" => f.Flags.HasFlag(CanFrameFlags.Error) ? "TRC writer emits no error rows"
            : f.Flags.HasFlag(CanFrameFlags.FlexibleDataRate) ? "TRC 2.1 writer has no FD rows"
            : null,
        ".blf" => null,
        // MDF4 writer skips remote and error frames by design (reported in Mdf4WriteResult).
        ".mf4" => f.Flags.HasFlag(CanFrameFlags.Remote) ? "MDF4 writer skips remote frames"
            : f.Flags.HasFlag(CanFrameFlags.Error) ? "MDF4 writer skips error frames"
            : null,
        _ => "unknown"
    };

    [Theory]
    [InlineData(".asc", 101)]
    [InlineData(".trc", 103)]
    [InlineData(".blf", 104)]
    [InlineData(".mf4", 105)]
    public async Task RoundTrip_RandomFrames_ExportThenImport(string ext, int seed)
    {
        var mismatches = new List<string>();
        var checkedFrames = 0;
        for (var round = 0; round < 25; round++)
        {
            var rng = new Random(seed * 1000 + round);
            var frames = RandomFrames(rng, rng.Next(1, 60), allowAnything: true);
            var path = Path.Combine(_dir, $"rt{round}{ext}");
            await Export(path, ext, frames);

            var back = await ReadBack(path);
            var expected = frames.Where(f => LossReason(ext, f) is null).ToList();
            var lossy = frames.Where(f => LossReason(ext, f) is not null).ToList();

            // Frames the format cannot hold may be dropped or degraded, but never crash the reader and
            // never exceed the number written; representable frames must survive exactly and in order.
            Assert.True(back.Count <= frames.Count, $"{ext}: re-imported more frames than were written");

            foreach (var e in expected)
            {
                checkedFrames++;
                if (!back.Any(b => SameFrame(e, b, Tolerance(ext))))
                    mismatches.Add($"round {round}: lost/changed {Describe(e)}  (nearest: {Describe(NearestById(back, e))})");
            }
            // Order of representable frames is preserved.
            var positions = expected.Select(e => back.FindIndex(b => SameFrame(e, b, Tolerance(ext)))).Where(i => i >= 0).ToList();
            Assert.True(positions.SequenceEqual(positions.OrderBy(i => i)), $"{ext}: round {round} reordered frames");
        }
        _output.WriteLine($"{ext}: {checkedFrames} representable frames round-tripped; {mismatches.Count} mismatches");
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(8)));
    }

    /// <summary>
    /// KNOWN DEFECT (reported, not fixed here): TraceExporter.WriteCsvAsync writes "time_s,channel,id_hex,dlc,data"
    /// but TraceImporter.ParseCsv (TraceImporter.cs ~line 347) treats EVERY cell from index 3 on as payload bytes,
    /// so the dlc column becomes a spurious first data byte and the contiguous hex blob "AABBCC" is rejected
    /// (byte.Parse overflow -> line dropped). Exporter output therefore does not re-import. Un-skip after fixing.
    /// </summary>
    [Fact]
    public async Task RoundTrip_Csv_ShouldPreservePayload()
    {
        var path = Path.Combine(_dir, "rt.csv");
        var frame = new CanFrame(0, 0x123, new byte[] { 0xAA, 0xBB, 0xCC }, 1_000_000_000);
        var empty = new CanFrame(0, 0x124, ReadOnlySpan<byte>.Empty, 2_000_000_000);
        await TraceExporter.WriteCsvAsync(path, [frame, empty]);
        var back = await ReadBack(path);
        Assert.Equal(2, back.Count);                        // 1st line is currently dropped
        Assert.True(SameFrame(frame, back[0], 1));
        Assert.True(SameFrame(empty, back[1], 1));          // currently comes back with a 1-byte payload { 0x00 }
    }

    private static CanFrame NearestById(List<CanFrame> back, CanFrame e) =>
        back.FirstOrDefault(b => b.Identifier == e.Identifier);

    private Task Export(string path, string ext, List<CanFrame> frames) => ext switch
    {
        ".asc" => TraceExporter.WriteAscAsync(path, frames),
        ".csv" => TraceExporter.WriteCsvAsync(path, frames),
        ".trc" => TraceExporter.WriteTrcAsync(path, frames),
        ".blf" => TraceExporter.WriteBlfAsync(path, frames),
        ".mf4" => Mdf4Writer.WriteAsync(path, frames),
        _ => throw new NotSupportedException(ext)
    };
}
