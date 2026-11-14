using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis;

/// <summary>
/// Reassembles J1939-21 transport-protocol messages (BAM and RTS/CTS) from a frame list.
/// Sessions are keyed by (channel, source address, destination address). Only observed
/// bytes are ever reported; missing packets yield Incomplete/TimedOut/Aborted results.
/// </summary>
public static class J1939TransportAnalyzer
{
    public const uint TpCmPgn = 0xEC00;
    public const uint TpDtPgn = 0xEB00;
    public const long BamTimeoutNanoseconds = 750L * 1_000_000;
    public const long RtsCtsTimeoutNanoseconds = 1250L * 1_000_000;
    public const long RtsCtsHoldTimeoutNanoseconds = 1050L * 1_000_000;

    private readonly record struct Key(int Channel, byte Sa, byte Da);

    private sealed class Session
    {
        public required Key Key;
        public required J1939TransportKind Kind;
        public required uint Pgn;
        public required int Size;
        public required int PacketCount;
        public readonly SortedDictionary<int, J1939TransportPacket> Received = [];
        public readonly List<int> Duplicates = [];
        public readonly List<int> FrameIndices = [];
        public readonly List<long> FrameSequences = [];
        public readonly List<string> Notes = [];
        public bool OutOfOrder;
        public int Highest;
        public long Start;
        public long LastActivity;
        public bool LastCtsHold;
    }

    public static IReadOnlyList<J1939TransportMessage> Reassemble(IReadOnlyList<CanFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var active = new Dictionary<Key, Session>();
        var results = new List<(int Order, J1939TransportMessage Message)>();
        var maxTs = long.MinValue;

        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (!f.IsExtended || f.Flags.HasFlag(CanFrameFlags.Remote) || f.IsError) continue;
            var ts = f.TimestampNanoseconds;
            maxTs = Math.Max(maxTs, ts);
            if (active.Count > 0) ExpireTimedOut(active, results, ts);

            var pgn = RbgCanInsight.Dbc.J1939.ExtractPgn(f.Identifier);
            var sa = (byte)(f.Identifier & 0xFF);
            var ps = (byte)((f.Identifier >> 8) & 0xFF);
            if (pgn == TpCmPgn) HandleCm(active, results, f, i, sa, ps);
            else if (pgn == TpDtPgn) HandleDt(active, results, f, i, sa, ps);
        }

        foreach (var s in active.Values.ToArray())
        {
            var timedOut = maxTs != long.MinValue && maxTs - s.LastActivity > Limit(s);
            Finish(active, results, s, timedOut ? J1939TransportStatus.TimedOut : J1939TransportStatus.Incomplete,
                null, timedOut ? "Timeout before end of trace." : "Trace ended before the session completed.");
        }

        return results.OrderBy(r => r.Order).Select(r => r.Message).ToArray();
    }

    private static long Limit(Session s) =>
        s.Kind == J1939TransportKind.Bam ? BamTimeoutNanoseconds
        : s.LastCtsHold ? RtsCtsHoldTimeoutNanoseconds : RtsCtsTimeoutNanoseconds;

    private static void ExpireTimedOut(Dictionary<Key, Session> active,
        List<(int, J1939TransportMessage)> results, long now)
    {
        List<Session>? expired = null;
        foreach (var s in active.Values)
            if (now - s.LastActivity > Limit(s)) (expired ??= []).Add(s);
        if (expired == null) return;
        foreach (var s in expired)
            Finish(active, results, s, J1939TransportStatus.TimedOut, null,
                $"No activity for more than {Limit(s) / 1_000_000} ms.");
    }

    private static void HandleCm(Dictionary<Key, Session> active, List<(int, J1939TransportMessage)> results,
        CanFrame f, int index, byte sa, byte ps)
    {
        if (f.DataLength < 8) return;
        var control = f.GetByte(0);
        switch (control)
        {
            case 32 or 16:
            {
                var key = new Key(f.Channel, sa, ps);
                if (active.TryGetValue(key, out var old))
                    Finish(active, results, old, J1939TransportStatus.Incomplete, null,
                        "Superseded by a new connection from the same source/destination.");
                var size = f.GetByte(1) | (f.GetByte(2) << 8);
                var declaredPackets = f.GetByte(3);
                var pgn = (uint)(f.GetByte(5) | (f.GetByte(6) << 8) | (f.GetByte(7) << 16));
                var expected = (size + 6) / 7;
                var s = new Session
                {
                    Key = key,
                    Kind = control == 32 ? J1939TransportKind.Bam : J1939TransportKind.RtsCts,
                    Pgn = pgn,
                    Size = size,
                    PacketCount = expected,
                    Start = f.TimestampNanoseconds,
                    LastActivity = f.TimestampNanoseconds
                };
                if (declaredPackets != expected)
                    s.Notes.Add($"Header declares {declaredPackets} packets but size {size} requires {expected}.");
                if (size < 9 || size > 1785)
                    s.Notes.Add($"Declared size {size} is outside the 9..1785 range.");
                Touch(s, f, index);
                active[key] = s;
                break;
            }
            case 17 or 19: // CTS / EndOfMsgACK are sent by the receiver
            {
                if (!active.TryGetValue(new Key(f.Channel, ps, sa), out var s)) return;
                Touch(s, f, index);
                if (control == 17) s.LastCtsHold = f.GetByte(1) == 0;
                break;
            }
            case 255:
            {
                if (!active.TryGetValue(new Key(f.Channel, sa, ps), out var s) &&
                    !active.TryGetValue(new Key(f.Channel, ps, sa), out s)) return;
                Touch(s, f, index);
                Finish(active, results, s, J1939TransportStatus.Aborted, f.GetByte(1), "Connection abort received.");
                break;
            }
        }
    }

    private static void HandleDt(Dictionary<Key, Session> active, List<(int, J1939TransportMessage)> results,
        CanFrame f, int index, byte sa, byte ps)
    {
        if (f.DataLength < 1 || !active.TryGetValue(new Key(f.Channel, sa, ps), out var s)) return;
        Touch(s, f, index);
        var seq = f.GetByte(0);
        if (f.DataLength < 8)
        {
            s.Notes.Add($"TP.DT #{seq} at frame {index} has only {f.DataLength} bytes and was ignored.");
            return;
        }
        if (seq < 1 || seq > s.PacketCount)
        {
            s.Notes.Add($"TP.DT sequence {seq} at frame {index} is outside 1..{s.PacketCount} and was ignored.");
            return;
        }
        var data = new byte[7];
        for (var b = 0; b < 7; b++) data[b] = f.GetByte(b + 1);
        if (s.Received.TryGetValue(seq, out var first))
        {
            s.Duplicates.Add(seq);
            if (!first.Data.AsSpan().SequenceEqual(data))
                s.Notes.Add($"Duplicate TP.DT #{seq} at frame {index} differs from the first copy; the first was kept.");
            return;
        }
        if (seq < s.Highest) s.OutOfOrder = true;
        s.Highest = Math.Max(s.Highest, seq);
        s.Received[seq] = new J1939TransportPacket(seq, index, f.TimestampNanoseconds, data);
        if (s.Received.Count == s.PacketCount)
            Finish(active, results, s, J1939TransportStatus.Complete, null, null);
    }

    private static void Touch(Session s, CanFrame f, int index)
    {
        s.LastActivity = f.TimestampNanoseconds;
        s.FrameIndices.Add(index);
        s.FrameSequences.Add(f.Sequence);
    }

    private static void Finish(Dictionary<Key, Session> active, List<(int, J1939TransportMessage)> results,
        Session s, J1939TransportStatus status, byte? abortReason, string? note)
    {
        active.Remove(s.Key);
        byte[]? payload = null;
        if (status == J1939TransportStatus.Complete)
        {
            payload = new byte[s.Size];
            var pos = 0;
            foreach (var p in s.Received.Values)
            {
                var n = Math.Min(7, s.Size - pos);
                Array.Copy(p.Data, 0, payload, pos, n);
                pos += n;
            }
        }
        var missing = Enumerable.Range(1, s.PacketCount).Where(n => !s.Received.ContainsKey(n)).ToArray();
        if (note != null) s.Notes.Add(note);
        var message = new J1939TransportMessage(
            s.Key.Channel, s.Key.Sa, s.Key.Da, s.Kind, s.Pgn, s.Size, s.PacketCount, status, payload,
            s.Received.Values.ToArray(), missing, s.Duplicates.ToArray(), s.OutOfOrder, abortReason,
            s.FrameIndices.ToArray(), s.FrameSequences.ToArray(), s.Start, s.LastActivity, s.Notes.ToArray());
        results.Add((s.FrameIndices.Count > 0 ? s.FrameIndices[0] : 0, message));
    }
}
