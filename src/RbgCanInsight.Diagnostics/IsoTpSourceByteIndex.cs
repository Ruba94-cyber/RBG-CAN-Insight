using RbgCanInsight.Domain;

namespace RbgCanInsight.Diagnostics;

/// <summary>A contiguous byte range copied from an actual original CAN data frame.
/// The PDU offset is zero-based from the UDS service-ID byte, not from ISO-TP PCI.</summary>
public sealed record DiagnosticSourceByteSpan(
    int FrameIndex,
    int CanPayloadByteOffset,
    int ResponsePayloadByteOffset,
    int ByteCount)
{
    public string DisplayText => ByteCount == 1
        ? $"CAN frame #{FrameIndex + 1}, byte {CanPayloadByteOffset}"
        : $"CAN frame #{FrameIndex + 1}, bytes {CanPayloadByteOffset}..{CanPayloadByteOffset + ByteCount - 1}";
}

/// <summary>
/// Verifies the entire ISO-TP payload against the immutable source frames,
/// then maps any UDS bit interval to the exact CAN payload bytes which carried
/// it. No addressing mode, missing frame, bit position or data byte is guessed.
/// </summary>
public sealed class IsoTpSourceByteIndex
{
    // The transport parser itself supports much larger PDUs. Source-level
    // evidence maps must stay memory-bounded when reviewing multi-million-
    // frame logs, so an oversized source chain remains explicitly unverified.
    private const int MaxAuditableSourceFrames = 65_536;
    private readonly IReadOnlyList<DiagnosticSourceByteSpan> _segments;
    public int PayloadLength { get; }

    private IsoTpSourceByteIndex(
        int payloadLength, IReadOnlyList<DiagnosticSourceByteSpan> segments)
    {
        PayloadLength = payloadLength;
        _segments = segments;
    }

    public static bool TryBuild(
        IReadOnlyList<CanFrame> frames,
        IsoTpMessage message,
        out IsoTpSourceByteIndex? index)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(message);
        index = null;
        if (!message.IsComplete || !string.IsNullOrEmpty(message.TransportIssue) ||
            message.Payload.Length == 0 ||
            message.FrameIndices.Count == 0 ||
            message.FrameIndices.Count > MaxAuditableSourceFrames)
            return false;

        var consumed = 0;
        var previousFrameIndex = -1;
        var previousTimestamp = long.MinValue;
        var nextSequence = 1;
        var segments = new List<DiagnosticSourceByteSpan>(message.FrameIndices.Count);
        for (var n = 0; n < message.FrameIndices.Count; n++)
        {
            var frameIndex = message.FrameIndices[n];
            if (frameIndex <= previousFrameIndex ||
                frameIndex < 0 || frameIndex >= frames.Count)
                return false;
            previousFrameIndex = frameIndex;
            var frame = frames[frameIndex];
            if (frame.Channel != message.Channel ||
                frame.Identifier != message.Identifier ||
                frame.IsExtended != message.IsExtended ||
                frame.IsError || frame.Flags.HasFlag(CanFrameFlags.Remote) ||
                frame.DataLength == 0)
                return false;
            var isFd = frame.Flags.HasFlag(CanFrameFlags.FlexibleDataRate);
            // Do not confer verified transport provenance on an impossible
            // CAN-frame payload length or BRS without CAN FD.
            if ((!isFd && frame.DataLength > 8) ||
                (isFd && !CanFdLength.IsValidFdLength(frame.DataLength)) ||
                (frame.Flags.HasFlag(CanFrameFlags.BitRateSwitch) && !isFd))
                return false;
            if (n == 0 && frame.TimestampNanoseconds != message.StartTimestampNanoseconds ||
                n == message.FrameIndices.Count - 1 &&
                    frame.TimestampNanoseconds != message.EndTimestampNanoseconds ||
                n > 0 && (frame.TimestampNanoseconds < previousTimestamp ||
                    frame.TimestampNanoseconds - previousTimestamp > 5_000_000_000L))
                return false;
            previousTimestamp = frame.TimestampNanoseconds;

            var pci = frame.GetByte(0);
            int dataStart;
            if (n == 0 && pci >> 4 == 0x0)
            {
                // A single-frame transfer must be represented by exactly one
                // source frame. CAN FD escape places SF_DL at byte 1.
                if (message.FrameIndices.Count != 1)
                    return false;
                var encoded = pci & 0x0F;
                // Classic CAN cannot use the CAN FD SF_DL escape header.
                // Even if a permissive reassembler decodes it, the source
                // byte map must not certify it as a normal ISO-TP frame.
                if (encoded == 0 && (!isFd || frame.DataLength <= 8))
                    return false;
                // With 12..64 CAN FD payload bytes, ISO-TP SF_DL uses
                // the explicit escape byte (PCI low nibble == 0), even
                // when the application payload itself is short. A normal
                // SF length nibble here is not spec-valid source proof.
                if (isFd && frame.DataLength > 8 && encoded != 0)
                    return false;
                dataStart = encoded == 0 ? 2 : 1;
                if (dataStart > frame.DataLength)
                    return false;
                var length = encoded == 0 ? frame.GetByte(1) : encoded;
                if (length <= 0 || length != message.Payload.Length ||
                    length > frame.DataLength - dataStart)
                    return false;
            }
            else if (n == 0 && pci >> 4 == 0x1)
            {
                if (frame.DataLength < 3 || message.FrameIndices.Count < 2)
                    return false;
                var length = ((pci & 0x0F) << 8) | frame.GetByte(1);
                dataStart = length == 0 ? 6 : 2;
                if (dataStart > frame.DataLength)
                    return false;
                if (length == 0)
                {
                    if (frame.DataLength < 7)
                        return false;
                    var extended = ((uint)frame.GetByte(2) << 24) |
                        ((uint)frame.GetByte(3) << 16) |
                        ((uint)frame.GetByte(4) << 8) | frame.GetByte(5);
                    if (extended > int.MaxValue)
                        return false;
                    length = (int)extended;
                }
                if (length != message.Payload.Length ||
                    length <= frame.DataLength - dataStart)
                    return false;
            }
            else if (n > 0 && pci >> 4 == 0x2)
            {
                if ((pci & 0x0F) != nextSequence)
                    return false;
                nextSequence = (nextSequence + 1) & 0x0F;
                dataStart = 1;
            }
            else
            {
                // Unsupported transport addressing variants never acquire a
                // falsely precise byte map from PCI-byte assumptions.
                return false;
            }

            var remaining = message.Payload.Length - consumed;
            var amount = Math.Min(remaining, frame.DataLength - dataStart);
            if (amount <= 0)
                return false;
            for (var i = 0; i < amount; i++)
            {
                if (frame.GetByte(dataStart + i) != message.Payload[consumed + i])
                    return false;
            }
            segments.Add(new DiagnosticSourceByteSpan(
                frameIndex, dataStart, consumed, amount));
            consumed += amount;
            if (consumed == message.Payload.Length &&
                n != message.FrameIndices.Count - 1)
                return false;
        }

        if (consumed != message.Payload.Length)
            return false;
        index = new IsoTpSourceByteIndex(consumed, segments);
        return true;
    }

    /// <summary>
    /// The returned slices include all bytes intersecting the bit range.
    /// ResponseBitOffset and BitLength on the value retain the precise
    /// bit interval within the first and last bytes.
    /// </summary>
    public bool TryMap(
        int responseBitOffset, int bitLength,
        out IReadOnlyList<DiagnosticSourceByteSpan> source)
    {
        source = [];
        if (responseBitOffset < 0 || bitLength <= 0 ||
            (long)responseBitOffset + bitLength > (long)PayloadLength * 8)
            return false;

        var byteStart = responseBitOffset / 8;
        var byteEnd = ((long)responseBitOffset + bitLength + 7) / 8;
        var result = new List<DiagnosticSourceByteSpan>();
        var mapped = 0L;
        foreach (var segment in _segments)
        {
            var start = Math.Max((long)byteStart, segment.ResponsePayloadByteOffset);
            var end = Math.Min(byteEnd, (long)segment.ResponsePayloadByteOffset + segment.ByteCount);
            if (end <= start)
                continue;
            var delta = (int)(start - segment.ResponsePayloadByteOffset);
            var bytes = (int)(end - start);
            result.Add(new DiagnosticSourceByteSpan(
                segment.FrameIndex, segment.CanPayloadByteOffset + delta,
                (int)start, bytes));
            mapped += bytes;
        }

        if (mapped != byteEnd - byteStart)
            return false;
        source = result;
        return true;
    }
}
