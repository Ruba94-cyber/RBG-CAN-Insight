using RbgCanInsight.DiagData;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Correlation;

public sealed record DidObservedValue(int Did, double Value, long TimestampNanoseconds, int FrameIndex);

public sealed record DidTraceSummary(
    int Did,
    int SampleCount,
    double MinValue,
    double MaxValue,
    double LastValue,
    long FirstTimestampNanoseconds,
    long LastTimestampNanoseconds);

/// <summary>Best-effort offline ISO-TP parser for UDS positive ReadDataByIdentifier responses.</summary>
public static class IsoTpDidExtractor
{
    private sealed class Reassembly
    {
        public int ExpectedLength;
        public List<byte> Payload = [];
        public long FirstTimestampNs;
        public long LastTimestampNs;
        public int FirstFrameIndex;
        public byte NextSequenceNumber = 1;
    }

    public static IReadOnlyDictionary<int, DidTraceSummary> Summarize(
        IReadOnlyList<CanFrame> frames,
        IReadOnlyList<DidParameter> dids,
        Func<int, int>? sourceAt = null)
    {
        // A DID number alone is not an ECU identity. Without a known request/response
        // endpoint mapping, do not label a response using an arbitrary ECU definition.
        var definitions = dids
            .GroupBy(did => did.Did)
            .Where(group => group.Select(did => did.Ecu)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            .ToDictionary(group => group.Key, group => group.First());
        if (definitions.Count == 0 || frames.Count == 0)
        {
            return new Dictionary<int, DidTraceSummary>();
        }

        var values = Extract(frames, definitions, sourceAt);
        return values
            .GroupBy(value => value.Did)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var ordered = group.OrderBy(item => item.TimestampNanoseconds).ToArray();
                    return new DidTraceSummary(
                        group.Key,
                        ordered.Length,
                        ordered.Min(item => item.Value),
                        ordered.Max(item => item.Value),
                        ordered[^1].Value,
                        ordered[0].TimestampNanoseconds,
                        ordered[^1].TimestampNanoseconds);
                });
    }

    private static IReadOnlyList<DidObservedValue> Extract(
        IReadOnlyList<CanFrame> frames,
        IReadOnlyDictionary<int, DidParameter> definitions,
        Func<int, int>? sourceAt)
    {
        var result = new List<DidObservedValue>();
        var reassemblyByKey = new Dictionary<(int Source, int Channel, uint Id, bool Extended), Reassembly>();
        int? confirmedSource = null;

        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            // A DID summary is keyed by DID alone, so it cannot safely combine
            // independent physical recordings even if each transaction is complete.
            // Fail closed until a caller can select one source explicitly.
            var source = sourceAt?.Invoke(index) ?? 0;
            if (source < 0 || (confirmedSource is { } priorSource && priorSource != source))
                return [];
            confirmedSource = source;
            var payload = frame.GetPayload();
            if (payload.Length == 0 || frame.IsError || frame.Flags.HasFlag(CanFrameFlags.Remote))
            {
                continue;
            }

            var protocolControl = payload[0] >> 4;
            var key = (source, frame.Channel, frame.Identifier, frame.IsExtended);
            if (protocolControl == 0)
            {
                var length = payload[0] & 0x0F;
                if (length == 0 && frame.Flags.HasFlag(CanFrameFlags.FlexibleDataRate) &&
                    payload.Length > 2 && payload[1] > 0 && payload[1] <= payload.Length - 2)
                {
                    // A fresh complete single-frame response invalidates any
                    // interrupted multi-frame response on the same transport key.
                    reassemblyByKey.Remove(key);
                    // CAN FD escape: SF_DL in the second byte.
                    ConsumeUdsPayload(payload.AsSpan(2, payload[1]), frame.TimestampNanoseconds, index, definitions, result);
                }
                else if (length > 0 && payload.Length >= 1 + length)
                {
                    reassemblyByKey.Remove(key);
                    ConsumeUdsPayload(payload.AsSpan(1, length), frame.TimestampNanoseconds, index, definitions, result);
                }
            }
            else if (protocolControl == 1)
            {
                // Even a corrupt replacement FF is evidence that an older
                // transfer cannot safely be continued on this transport ID.
                // The canonical UDS reassembler makes the same decision.
                reassemblyByKey.Remove(key);
                if (payload.Length < 2)
                {
                    continue;
                }

                var expected = ((payload[0] & 0x0F) << 8) | payload[1];
                var offset = 2;
                if (expected == 0 && payload.Length >= 6)
                {
                    // CAN-FD first-frame 32-bit length encoding.
                    var longLength = ((uint)payload[2] << 24) | ((uint)payload[3] << 16) |
                                     ((uint)payload[4] << 8) | payload[5];
                    if (longLength > int.MaxValue) continue;
                    expected = (int)longLength;
                    offset = 6;
                }
                if (expected <= payload.Length - offset || expected > 16 * 1024 * 1024)
                    continue;
                var reassembly = new Reassembly
                {
                    ExpectedLength = expected,
                    Payload = payload.Skip(offset).ToList(),
                    FirstTimestampNs = frame.TimestampNanoseconds,
                    LastTimestampNs = frame.TimestampNanoseconds,
                    FirstFrameIndex = index
                };
                reassemblyByKey[key] = reassembly;
                TryFlush(key, reassemblyByKey, definitions, result);
            }
            else if (protocolControl == 2 && reassemblyByKey.TryGetValue(key, out var reassembly))
            {
                var sequenceNumber = (byte)(payload[0] & 0x0F);
                // Use the same 5-second *inter-frame* inactivity bound
                // as the authoritative UDS transport view. A stale CF
                // must not produce a decoded DID that the UDS trace rejects.
                if (frame.TimestampNanoseconds < reassembly.LastTimestampNs ||
                    frame.TimestampNanoseconds - reassembly.LastTimestampNs > 5_000_000_000L ||
                    sequenceNumber != reassembly.NextSequenceNumber)
                {
                    reassemblyByKey.Remove(key);
                    continue;
                }

                reassembly.Payload.AddRange(payload.Skip(1));
                reassembly.LastTimestampNs = frame.TimestampNanoseconds;
                reassembly.NextSequenceNumber = (byte)((reassembly.NextSequenceNumber + 1) & 0x0F);
                TryFlush(key, reassemblyByKey, definitions, result);
            }
        }

        return result;
    }

    private static void TryFlush(
        (int Source, int Channel, uint Id, bool Extended) key,
        Dictionary<(int Source, int Channel, uint Id, bool Extended), Reassembly> reassemblyByKey,
        IReadOnlyDictionary<int, DidParameter> definitions,
        List<DidObservedValue> result)
    {
        var reassembly = reassemblyByKey[key];
        if (reassembly.Payload.Count < reassembly.ExpectedLength)
        {
            return;
        }

        ConsumeUdsPayload(
            reassembly.Payload.Take(reassembly.ExpectedLength).ToArray(),
            reassembly.FirstTimestampNs,
            reassembly.FirstFrameIndex,
            definitions,
            result);
        reassemblyByKey.Remove(key);
    }

    private static void ConsumeUdsPayload(
        ReadOnlySpan<byte> payload,
        long timestampNs,
        int frameIndex,
        IReadOnlyDictionary<int, DidParameter> definitions,
        List<DidObservedValue> result)
    {
        if (payload.Length < 4 || payload[0] != 0x62)
        {
            return;
        }

        var did = (payload[1] << 8) | payload[2];
        if (!definitions.TryGetValue(did, out var definition))
        {
            return;
        }

        var data = payload[3..];
        if (TryDecodeValue(data, definition, out var value))
        {
            result.Add(new DidObservedValue(did, value, timestampNs, frameIndex));
        }
    }

    private static bool TryDecodeValue(ReadOnlySpan<byte> data, DidParameter definition, out double value)
    {
        value = 0;
        // A quick scalar readout is not a replacement for the complete
        // schema-guided UDS decoder. Conflict or explicit raw-only fields
        // cannot become fabricated physical measurements in the mapping pane.
        if (definition.EngineeringIssues.Count > 0 || definition.Fields.Count > 1)
            return false;
        var field = definition.Fields.Count == 1 ? definition.Fields[0] : null;
        if (field is { SupportsPhysicalConversion: false })
            return false;
        var bitLength = field?.BitLength ?? definition.BitLength ?? 0;
        var bitOffset = field?.BitOffset ?? 0;
        if (bitLength is <= 0 or > 64 || bitOffset < 0 ||
            bitLength % 8 != 0 || bitOffset % 8 != 0)
            return false; // unsupported packed / ambiguous field encoding

        var byteLength = bitLength / 8;
        var start = bitOffset / 8;
        // An extra unexplained byte may represent another DID or a missing
        // source field. Do not silently treat the first bytes as a whole-DID
        // scalar measurement; preserve the original bytes for UDS investigation.
        if (start > data.Length || byteLength != data.Length - start) return false;
        // A double cannot represent arbitrary 54..64-bit raw integers exactly.
        if (bitLength > 53) return false;

        var bytes = data.Slice(start, byteLength);
        var littleEndian = field is not null &&
            (field.ByteOrder.Contains("LITTLE", StringComparison.OrdinalIgnoreCase) ||
             field.ByteOrder.Contains("LOWHIGH", StringComparison.OrdinalIgnoreCase));
        ulong raw = 0;
        if (littleEndian)
        {
            for (var i = bytes.Length - 1; i >= 0; i--)
                raw = (raw << 8) | bytes[i];
        }
        else
        {
            for (var i = 0; i < bytes.Length; i++)
                raw = (raw << 8) | bytes[i];
        }

        var signed = field?.IsSigned ?? (definition.DataType.Contains("INT", StringComparison.OrdinalIgnoreCase) &&
                                       !definition.DataType.Contains("UINT", StringComparison.OrdinalIgnoreCase));
        double decoded;
        if (signed)
        {
            var signedValue = bitLength == 64
                ? unchecked((long)raw)
                : (raw & (1UL << (bitLength - 1))) != 0
                    ? unchecked((long)(raw | (~0UL << bitLength)))
                    : (long)raw;
            decoded = signedValue;
        }
        else
        {
            decoded = raw;
        }
        value = decoded * (field?.Factor ?? definition.Factor ?? 1d) +
                (field?.Offset ?? definition.Offset ?? 0d);
        return double.IsFinite(value);
    }
}
