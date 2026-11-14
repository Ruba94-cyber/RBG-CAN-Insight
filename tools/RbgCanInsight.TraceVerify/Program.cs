using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using RbgCanInsight.Dbc;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.DiagData;
using RbgCanInsight.TraceVerify;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;

// Read-only, incremental integrity check for sensitive offline traces.
// Prints no raw payloads or full local paths, only summary statistics and a digest.
if (args.Length == 0 || args.Any(arg => arg is "--help" or "-h"))
{
    Console.WriteLine("Usage: dotnet run --project tools/RbgCanInsight.TraceVerify -- <trace> [--expect-count=N] [--dump=<frames.csv>] [--dbc=<network.dbc> --signal-dump=<signals.csv> [--dbc-channel=N]] [--catalog=<definition.cdd|odx|pdx> --uds-field-dump=<fields.csv> [--strict-uds-source]] [--benchmark]");
    return args.Length == 0 ? 2 : 0;
}

var input = args[0];
// Optional local-only outputs for comparison against an independent reader (e.g. python-can):
//   --dump=<file.csv>        one line per decoded frame (includes payload bytes; keep it local)
//   --export-blf=<file.blf>  re-export all decoded frames with the application's BLF writer
string? ArgValue(string name) => args.Skip(1)
    .FirstOrDefault(value => value.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
var dumpPath = ArgValue("--dump");
var exportBlfPath = ArgValue("--export-blf");
var dbcPath = ArgValue("--dbc");
var signalDumpPath = ArgValue("--signal-dump");
var dbcChannelText = ArgValue("--dbc-channel");
var catalogPath = ArgValue("--catalog");
var udsFieldDumpPath = ArgValue("--uds-field-dump");
var strictUdsSource = args.Skip(1).Contains("--strict-uds-source", StringComparer.Ordinal);
var benchmark = args.Skip(1).Contains("--benchmark", StringComparer.Ordinal);
var supportedAssignments = new[]
{
    "--dump=", "--export-blf=", "--dbc=", "--signal-dump=",
    "--dbc-channel=", "--catalog=", "--uds-field-dump=", "--expect-count="
};
if (args.Skip(1).Any(arg =>
    arg is not ("--benchmark" or "--strict-uds-source") &&
    !supportedAssignments.Any(prefix => arg.StartsWith(prefix, StringComparison.Ordinal))))
{
    Console.Error.WriteLine("Unknown command-line argument. See --help.");
    return 2;
}
if (strictUdsSource && udsFieldDumpPath is null)
{
    Console.Error.WriteLine("--strict-uds-source requires --uds-field-dump and --catalog.");
    return 2;
}
if ((catalogPath is null) != (udsFieldDumpPath is null))
{
    Console.Error.WriteLine("UDS field evidence requires both --catalog and --uds-field-dump.");
    return 2;
}
if ((dbcPath is null) != (signalDumpPath is null) ||
    (dbcChannelText is not null && dbcPath is null))
{
    Console.Error.WriteLine("Signal evidence requires both --dbc and --signal-dump; --dbc-channel requires --dbc.");
    return 2;
}
int? dbcChannel = null;
if (dbcChannelText is not null)
{
    if (!int.TryParse(dbcChannelText, NumberStyles.None, CultureInfo.InvariantCulture,
        out var selectedChannel) || selectedChannel < 0)
    {
        Console.Error.WriteLine("--dbc-channel must be a nonnegative zero-based physical bus number.");
        return 2;
    }
    dbcChannel = selectedChannel;
}
// Never allow an audit output path to truncate its original trace, DBC or
// another output, including through alternate relative path spelling.
var inputs = new[] { input, dbcPath, catalogPath }.Where(p => p is not null)
    .Select(p => Path.GetFullPath(p!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
var outputs = new[] { dumpPath, exportBlfPath, signalDumpPath, udsFieldDumpPath }.Where(p => p is not null)
    .Select(p => Path.GetFullPath(p!)).ToArray();
if (outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Length ||
    outputs.Any(inputs.Contains))
{
    Console.Error.WriteLine("Audit output path must not overwrite any input or another output.");
    return 2;
}
DbcSignalEvidenceDump? signalEvidence = null;
if (dbcPath is not null)
{
    try
    {
        using var dbcStream = File.OpenRead(dbcPath);
        var dbcHash = Convert.ToHexString(SHA256.HashData(dbcStream));
        dbcStream.Position = 0;
        using var dbcText = new StreamReader(dbcStream);
        var database = new DbcParser().Parse(Path.GetFileName(dbcPath), dbcText);
        signalEvidence = new DbcSignalEvidenceDump(
            new DbcDecoder(database, new DbcDecoderOptions(Channel: dbcChannel)), dbcHash);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
        DbcParseException or ArgumentException)
    {
        Console.Error.WriteLine($"Cannot load DBC evidence source: {exception.GetType().Name}.");
        return 2;
    }
}
using var signalDump = signalDumpPath is null ? null :
    new StreamWriter(signalDumpPath, false, new System.Text.UTF8Encoding(false));
signalDump?.WriteLine(DbcSignalEvidenceDump.CsvHeader);
var exportFrames = exportBlfPath is null ? null : new List<CanFrame>();
// Full transaction analysis requires the raw source index. Keep this
// opt-in: the normal integrity and DBC frame oracles stay streaming.
var udsFrames = udsFieldDumpPath is null ? null : new List<CanFrame>();
using var dump = dumpPath is null ? null : new StreamWriter(dumpPath, false, new System.Text.UTF8Encoding(false));
dump?.WriteLine("index,timestamp_ns,channel,id,extended,fd,brs,remote,error,dlc,data");
var expectedArg = args.Skip(1).FirstOrDefault(value => value.StartsWith("--expect-count=", StringComparison.Ordinal));
long? expected = null;
if (expectedArg is not null)
{
    if (!long.TryParse(expectedArg["--expect-count=".Length..], NumberStyles.None,
        CultureInfo.InvariantCulture, out var value) || value < 0)
    {
        Console.Error.WriteLine("Invalid --expect-count; use a nonnegative integer.");
        return 2;
    }
    expected = value;
}
if (!TraceImporter.IsSupported(input))
{
    Console.Error.WriteLine("Unsupported trace extension.");
    return 2;
}

long count = 0;
long signalEvidenceRows = 0;
long first = long.MaxValue;
long last = long.MinValue;
long outOfOrder = 0;
long? prior = null;
long fd = 0;
long errors = 0;
long extended = 0;
var channels = new SortedDictionary<int, long>();
var ids = new HashSet<(int Channel, uint Identifier, bool Extended)>();
var header = new byte[18];
var payload = new byte[64];
using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
var importTimer = Stopwatch.StartNew();

try
{
    await foreach (var frame in TraceImporter.ReadAsync(input))
    {
        exportFrames?.Add(frame);
        udsFrames?.Add(frame);
        if (signalEvidence is not null && signalDump is not null)
            signalEvidenceRows += signalEvidence.WriteFrame(signalDump, count, frame);
        if (dump is not null)
        {
            dump.WriteLine(string.Join(',',
                count.ToString(CultureInfo.InvariantCulture),
                frame.TimestampNanoseconds.ToString(CultureInfo.InvariantCulture),
                frame.Channel.ToString(CultureInfo.InvariantCulture),
                frame.Identifier.ToString(CultureInfo.InvariantCulture),
                frame.IsExtended ? 1 : 0,
                frame.Flags.HasFlag(CanFrameFlags.FlexibleDataRate) ? 1 : 0,
                frame.Flags.HasFlag(CanFrameFlags.BitRateSwitch) ? 1 : 0,
                frame.Flags.HasFlag(CanFrameFlags.Remote) ? 1 : 0,
                frame.IsError ? 1 : 0,
                frame.DeclaredDataLength.ToString(CultureInfo.InvariantCulture),
                Convert.ToHexString(frame.GetPayload())));
        }
        count++;
        if (prior is { } previous && frame.TimestampNanoseconds < previous) outOfOrder++;
        prior = frame.TimestampNanoseconds;
        first = Math.Min(first, frame.TimestampNanoseconds);
        last = Math.Max(last, frame.TimestampNanoseconds);
        if (frame.Flags.HasFlag(CanFrameFlags.FlexibleDataRate)) fd++;
        if (frame.IsError) errors++;
        if (frame.IsExtended) extended++;
        channels[frame.Channel] = channels.GetValueOrDefault(frame.Channel) + 1;
        ids.Add((frame.Channel, frame.Identifier, frame.IsExtended));

        // Canonical digest: channel(u32), ID(u32), time(ns i64), flags(u8),
        // declared data-byte length(u8), actual payload. Remote DLC is NOT payload.
        BinaryPrimitives.WriteInt32LittleEndian(header, frame.Channel);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), frame.Identifier);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), frame.TimestampNanoseconds);
        header[16] = (byte)frame.Flags;
        header[17] = frame.DeclaredDataLength;
        digest.AppendData(header);
        frame.CopyPayloadTo(payload);
        digest.AppendData(payload.AsSpan(0, frame.DataLength));
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Import failed: {exception.GetType().Name}.");
    return 2;
}
importTimer.Stop();

dump?.Flush();
signalDump?.Flush();

UdsFieldEvidenceDump.Result? udsFieldResult = null;
long? udsAuditElapsedMs = null;
if (udsFieldDumpPath is not null && catalogPath is not null && udsFrames is not null)
{
    var udsTimer = Stopwatch.StartNew();
    try
    {
        string catalogSha;
        using (var catalogStream = File.OpenRead(catalogPath))
            catalogSha = Convert.ToHexString(SHA256.HashData(catalogStream));
        var catalogs = DidCatalog.LoadAll(catalogPath);
        // The independently recorded identity must still match after
        // the catalog was parsed; never compare a changed on-disk version.
        using (var verifiedStream = File.OpenRead(catalogPath))
        {
            if (!string.Equals(catalogSha,
                    Convert.ToHexString(SHA256.HashData(verifiedStream)),
                    StringComparison.Ordinal))
                throw new InvalidDataException("Catalog changed while building UDS evidence.");
        }
        var transactions = UdsTransactionAnalyzer.Analyze(udsFrames, catalogs);
        using var udsWriter = new StreamWriter(udsFieldDumpPath, false,
            new System.Text.UTF8Encoding(false));
        udsFieldResult = UdsFieldEvidenceDump.Write(udsWriter, transactions, catalogSha);
        udsTimer.Stop();
        udsAuditElapsedMs = udsTimer.ElapsedMilliseconds;
    }
    catch (Exception exception)
    {
        // Never print paths, ECU names or other potentially private
        // diagnostic data in CLI error summaries.
        Console.Error.WriteLine($"UDS field audit failed: {exception.GetType().Name}.");
        return 2;
    }
}

if (exportBlfPath is not null && exportFrames is not null)
{
    await TraceExporter.WriteBlfAsync(exportBlfPath, exportFrames);
}

var report = new
{
    Format = Path.GetExtension(input).ToLowerInvariant(),
    FrameCount = count,
    ChannelCounts = channels,
    UniqueChannelIdentifiers = ids.Count,
    CanFdFrames = fd,
    ExtendedIdFrames = extended,
    ErrorFrames = errors,
    OutOfOrderTimestamps = outOfOrder,
    FirstTimestampNanoseconds = count > 0 ? first : (long?)null,
    LastTimestampNanoseconds = count > 0 ? last : (long?)null,
    CanonicalSha256 = Convert.ToHexString(digest.GetHashAndReset()),
    SourceBackedDbcSignalRows = signalEvidence is null ? (long?)null : signalEvidenceRows,
    UdsTransactions = udsFieldResult?.Transactions,
    SourceBackedUdsFieldRows = udsFieldResult?.SourceBackedRows,
    UdsFieldsWithoutVerifiedSource = udsFieldResult?.UnverifiedRows,
    UdsResponsesWithUnresolvedDidLayouts = udsFieldResult?.UnresolvedDidResponses,
    ImportElapsedMilliseconds = benchmark ? importTimer.ElapsedMilliseconds : (long?)null,
    UdsAuditElapsedMilliseconds = benchmark ? udsAuditElapsedMs : null,
    ManagedHeapAfterAuditBytes = benchmark ? GC.GetTotalMemory(false) : (long?)null,
    PeakProcessWorkingSetBytes = benchmark
        ? Process.GetCurrentProcess().PeakWorkingSet64 : (long?)null
};
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
if (strictUdsSource && !UdsFieldEvidenceDump.HasCompleteVerifiedCoverage(udsFieldResult))
{
    Console.Error.WriteLine(
        $"Strict UDS source gate failed: {udsFieldResult?.UnverifiedRows ?? 0} field(s) without verified CAN source; " +
        $"{udsFieldResult?.UnresolvedDidResponses ?? 0} response(s) with unresolved DID layout; " +
        $"{udsFieldResult?.SourceBackedRows ?? 0} verified field(s). At least one verified field is required.");
    return 3;
}
if (expected is { } target && count != target)
{
    Console.Error.WriteLine($"Frame count mismatch: expected {target}, decoded {count}.");
    return 3;
}
return count > 0 ? 0 : 3;
