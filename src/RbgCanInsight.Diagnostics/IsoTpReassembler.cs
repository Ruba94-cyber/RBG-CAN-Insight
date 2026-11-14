using System.Threading;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Diagnostics;

/// <summary>Offline ISO-TP reconstruction keyed by channel and CAN identifier. It accepts
/// Classical CAN and CAN-FD single-frame escape/extended first-frame lengths and records every
/// source frame so the UI can navigate back to the raw trace.</summary>
public static class IsoTpReassembler
{
    // Refuse unrealistically large or corrupt transport lengths in an offline
    // capture; otherwise a single malformed first frame can retain state until EOF.
    private const int MaxReassemblyBytes = 16 * 1024 * 1024;

    private sealed class Assembly
    {
        public required int Channel { get; init; }
        public required uint Identifier { get; init; }
        public required bool IsExtended { get; init; }
        public required int ExpectedLength { get; init; }
        public required long StartTimestamp { get; init; }
        public required List<int> FrameIndices { get; init; }
        public required List<byte> Payload { get; init; }
        public byte NextSequence { get; set; } = 1;
        public string Issue { get; set; } = string.Empty;
        public long EndTimestamp { get; set; }
    }

    public static IReadOnlyList<IsoTpMessage> Reassemble(
        IReadOnlyList<CanFrame> frames,
        CancellationToken cancellationToken = default,
        IProgress<UdsAnalysisProgress>? progress = null,
        Func<int, int>? sourceAt = null,
        int addressingPrefixBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(frames);
        // ISO 15765-2 clause 10: normal/normal-fixed addressing has no address byte in the CAN data
        // field (prefix 0); extended addressing (N_TA) and mixed addressing (N_AE) put one address
        // byte in front of the N_PCI byte (prefix 1). The prefix is a per-capture configuration:
        // it cannot be detected reliably from the data.
        if (addressingPrefixBytes is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(addressingPrefixBytes));
        cancellationToken.ThrowIfCancellationRequested();
        var messages = new List<IsoTpMessage>();
        // Channel numbers may overlap between independent recordings in a
        // ByTime merge. The physical logger is part of the ISO-TP state key.
        // Without it, FF in logger A + CF in logger B fabricates a payload.
        var active = new Dictionary<(int Source, int Channel, uint Identifier, bool Extended), Assembly>();

        for (var index = 0; index < frames.Count; index++)
        {
            // A long capture must be cancellable *while* ISO-TP PDUs are
            // reconstructed, rather than only when the pairing phase starts.
            // Sample progress at a low frequency to avoid UI churn.
            if ((index & 4095) == 0 || index == frames.Count - 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new UdsAnalysisProgress(
                    "Reassembling ISO-TP messages", index + 1, frames.Count));
            }
            var frame = frames[index];
            if (frame.DataLength <= addressingPrefixBytes || frame.IsError ||
                frame.Flags.HasFlag(CanFrameFlags.Remote))
                continue;

            // CanFrame is an inline, allocation-free struct, but GetPayload()
            // creates a byte[]. On multi-million-frame captures most frames
            // are unrelated to ISO-TP. Inspect PCI via GetByte before
            // allocating; ignore FC and orphan CF exactly as before.
            var pciType = frame.GetByte(addressingPrefixBytes) >> 4;
            if (pciType > 0x2)
                continue;
            var source = sourceAt?.Invoke(index) ?? 0;
            if (source < 0)
                throw new ArgumentOutOfRangeException(nameof(sourceAt),
                    "A recorded frame cannot have a negative physical source index.");
            var key = (source, frame.Channel, frame.Identifier, frame.IsExtended);
            if (pciType == 0x2 && !active.ContainsKey(key))
                continue;
            var bytes = frame.GetPayload();
            if (addressingPrefixBytes > 0)
                bytes = bytes[addressingPrefixBytes..];
            switch (pciType)
            {
                case 0x0:
                    if (TryReadSingleFrame(bytes, out var singlePayload))
                    {
                        // A valid single-frame PDU replaces any unfinished multi-frame
                        // transfer on this exact transport key. Otherwise a later CF
                        // could fabricate a response from stale bytes across sessions.
                        if (active.Remove(key, out var superseded))
                        {
                            superseded.Issue = "new single frame before previous PDU completed";
                            messages.Add(ToMessage(superseded, false));
                        }
                        messages.Add(new IsoTpMessage(
                            frame.Channel,
                            frame.Identifier,
                            frame.IsExtended,
                            frame.TimestampNanoseconds,
                            frame.TimestampNanoseconds,
                            [index],
                            singlePayload,
                            true,
                            string.Empty));
                    }
                    break;

                case 0x1:
                    if (!TryReadFirstFrame(bytes, out var expectedLength, out var firstData))
                    {
                        // Even an invalid FF occupies the same transport
                        // identity: do not let a later CF complete an older
                        // PDU across this visible framing discontinuity.
                        if (active.Remove(key, out var invalidated))
                        {
                            invalidated.Issue = "invalid first frame interrupted previous PDU";
                            invalidated.EndTimestamp = frame.TimestampNanoseconds;
                            invalidated.FrameIndices.Add(index);
                            messages.Add(ToMessage(invalidated, false));
                        }
                        break;
                    }

                    if (active.Remove(key, out var interrupted))
                    {
                        interrupted.Issue = "new first frame before previous PDU completed";
                        messages.Add(ToMessage(interrupted, false));
                    }

                    var assembly = new Assembly
                    {
                        Channel = frame.Channel,
                        Identifier = frame.Identifier,
                        IsExtended = frame.IsExtended,
                        ExpectedLength = expectedLength,
                        StartTimestamp = frame.TimestampNanoseconds,
                        EndTimestamp = frame.TimestampNanoseconds,
                        FrameIndices = [index],
                        Payload = firstData.ToList()
                    };
                    if (assembly.Payload.Count >= assembly.ExpectedLength)
                    {
                        messages.Add(ToMessage(assembly, true));
                    }
                    else
                    {
                        active[key] = assembly;
                    }
                    break;

                case 0x2:
                    if (!active.TryGetValue(key, out var current))
                    {
                        break;
                    }

                    // A stale multi-frame transfer must not consume a later unrelated CF.
                    if (frame.TimestampNanoseconds < current.EndTimestamp ||
                        frame.TimestampNanoseconds - current.EndTimestamp > 5_000_000_000L)
                    {
                        current.Issue = "ISO-TP consecutive-frame timeout or reversed timestamp";
                        messages.Add(ToMessage(current, false));
                        active.Remove(key);
                        break;
                    }

                    var sequence = (byte)(bytes[0] & 0x0F);
                    if (sequence != current.NextSequence)
                    {
                        current.Issue = $"sequence error: expected {current.NextSequence:X1}, got {sequence:X1}";
                        current.EndTimestamp = frame.TimestampNanoseconds;
                        current.FrameIndices.Add(index);
                        active.Remove(key);
                        messages.Add(ToMessage(current, false));
                        break;
                    }

                    current.NextSequence = (byte)((current.NextSequence + 1) & 0x0F);
                    current.EndTimestamp = frame.TimestampNanoseconds;
                    current.FrameIndices.Add(index);
                    // No LINQ enumerator or transient array for every
                    // consecutive frame; the input payload is already local.
                    for (var payloadIndex = 1; payloadIndex < bytes.Length; payloadIndex++)
                        current.Payload.Add(bytes[payloadIndex]);
                    if (current.Payload.Count >= current.ExpectedLength)
                    {
                        active.Remove(key);
                        messages.Add(ToMessage(current, true));
                    }
                    break;

                // Flow-control frames are transport coordination, not UDS messages. They stay
                // visible in the raw trace and can be reached through the neighbouring PDU.
                case 0x3:
                    break;
            }
        }

        foreach (var incomplete in active.Values)
        {
            if (string.IsNullOrEmpty(incomplete.Issue))
            {
                incomplete.Issue = $"incomplete PDU: {incomplete.Payload.Count}/{incomplete.ExpectedLength} bytes";
            }
            messages.Add(ToMessage(incomplete, false));
        }

        return messages.OrderBy(message => message.StartTimestampNanoseconds).ToArray();
    }

    private static bool TryReadSingleFrame(byte[] bytes, out byte[] payload)
    {
        payload = [];
        var length = bytes[0] & 0x0F;
        var offset = 1;
        if (length == 0)
        {
            if (bytes.Length < 3)
            {
                return false;
            }
            length = bytes[1];
            offset = 2;
        }

        if (length <= 0 || offset + length > bytes.Length)
        {
            return false;
        }

        payload = bytes.AsSpan(offset, length).ToArray();
        return true;
    }

    private static bool TryReadFirstFrame(byte[] bytes, out int expectedLength, out byte[] payload)
    {
        expectedLength = 0;
        payload = [];
        if (bytes.Length < 3)
        {
            return false;
        }

        expectedLength = ((bytes[0] & 0x0F) << 8) | bytes[1];
        var offset = 2;
        if (expectedLength == 0)
        {
            if (bytes.Length < 7)
            {
                return false;
            }
            var longLength = ((uint)bytes[2] << 24) | ((uint)bytes[3] << 16) |
                             ((uint)bytes[4] << 8) | bytes[5];
            if (longLength is 0 or > int.MaxValue)
            {
                return false;
            }
            expectedLength = (int)longLength;
            offset = 6;
        }

        if (expectedLength <= bytes.Length - offset ||
            expectedLength > MaxReassemblyBytes)
        {
            return false;
        }

        payload = bytes.AsSpan(offset).ToArray();
        return true;
    }

    private static IsoTpMessage ToMessage(Assembly assembly, bool complete)
    {
        var length = Math.Min(assembly.ExpectedLength, assembly.Payload.Count);
        return new IsoTpMessage(
            assembly.Channel,
            assembly.Identifier,
            assembly.IsExtended,
            assembly.StartTimestamp,
            assembly.EndTimestamp,
            assembly.FrameIndices.ToArray(),
            assembly.Payload.Take(length).ToArray(),
            complete,
            complete ? string.Empty : assembly.Issue);
    }
}
