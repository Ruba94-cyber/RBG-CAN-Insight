using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Analysis.Heuristics;

/// <summary>A database offered to <see cref="DbcSuggester"/>. <paramref name="MatchByPgn"/> enables
/// the existing J1939 PGN matching of <see cref="DbcDecoder"/> (29-bit identifiers only).
/// <paramref name="AssignedChannel"/>, when set, restricts the database to that bus.</summary>
public sealed record DbcSuggestionCandidate(
    string Name, DbcDatabase Database, bool MatchByPgn = false, int? AssignedChannel = null);

/// <summary>Transparent score parts. Every share is matched/total with the counts exposed;
/// <see cref="CompositeScore"/> is only the documented weighted sum of the four shares.</summary>
public sealed record DbcSuggestionBreakdown(
    int DistinctIdentifiers, int MatchedIdentifiers,
    long Frames, long MatchedFrames,
    long DlcCheckedFrames, long DlcAgreeingFrames,
    long SignalSamplesChecked, long SignalSamplesInRange,
    double IdentifierShare, double FrameShare, double DlcShare, double SignalRangeShare,
    double CompositeScore)
{
    public const double IdentifierWeight = 0.40;
    public const double FrameWeight = 0.30;
    public const double DlcWeight = 0.15;
    public const double SignalRangeWeight = 0.15;
}

public readonly record struct DbcUnmatchedIdentifier(
    int Channel, uint Identifier, bool IsExtended, long FrameCount);

public sealed record DbcSuggestionScore(
    string DatabaseName, int CandidateIndex, DbcSuggestionBreakdown Breakdown,
    IReadOnlyList<DbcUnmatchedIdentifier> UnmatchedIdentifiers);

public sealed record ChannelDbcSuggestion(
    int Channel, int DistinctIdentifiers, long Frames,
    IReadOnlyList<DbcSuggestionScore> Ranking);

/// <summary>
/// Ranks candidate databases per channel by how well they explain the recorded traffic.
/// Identity is (channel, identifier, frame format): an 11-bit and a 29-bit frame with the same
/// number are different identifiers. Matching reuses <see cref="DbcDecoder.TryGetMessage(in CanFrame, out DbcMessage)"/>
/// (including its PGN logic) and <see cref="DbcDecoder.Decode"/>; nothing is re-implemented here.
/// Error and remote frames are ignored.
/// Composite = 0.40*identifier share + 0.30*frame share + 0.15*DLC agreement + 0.15*signal-range share.
/// A share with an empty denominator is 0. Signals with Minimum == Maximum are treated as having
/// no declared range and are not counted. DLC agreement = frame.DataLength >= message.Length.
/// Ordering: composite desc, matched frames desc, matched identifiers desc, database name (ordinal),
/// then the candidate's input position. Channels ascending; unmatched ids by id, then 11-bit first.
/// </summary>
public static class DbcSuggester
{
    public static IReadOnlyList<ChannelDbcSuggestion> Rank(
        IEnumerable<CanFrame> frames, IReadOnlyList<DbcSuggestionCandidate> candidates)
    {
        var data = frames.Where(f => !f.IsError && !f.Flags.HasFlag(CanFrameFlags.Remote)).ToList();
        var byChannel = data.GroupBy(f => f.Channel).OrderBy(g => g.Key).ToList();
        var decoders = candidates.Select(c => new DbcDecoder(
            c.Database, new DbcDecoderOptions(c.MatchByPgn, c.AssignedChannel))).ToList();
        var result = new List<ChannelDbcSuggestion>();
        foreach (var group in byChannel)
        {
            var list = group.ToList();
            var ids = new Dictionary<(uint, bool), long>();
            foreach (var f in list)
            {
                ids.TryGetValue((f.Identifier, f.IsExtended), out var n);
                ids[(f.Identifier, f.IsExtended)] = n + 1;
            }
            var scores = new List<DbcSuggestionScore>();
            for (var i = 0; i < candidates.Count; i++)
                scores.Add(Score(candidates[i], i, decoders[i], group.Key, list, ids));
            var ranked = scores
                .OrderByDescending(s => s.Breakdown.CompositeScore)
                .ThenByDescending(s => s.Breakdown.MatchedFrames)
                .ThenByDescending(s => s.Breakdown.MatchedIdentifiers)
                .ThenBy(s => s.DatabaseName, StringComparer.Ordinal)
                .ThenBy(s => s.CandidateIndex)
                .ToList();
            result.Add(new ChannelDbcSuggestion(group.Key, ids.Count, list.Count, ranked));
        }
        return result;
    }

    private static DbcSuggestionScore Score(
        DbcSuggestionCandidate candidate, int index, DbcDecoder decoder, int channel,
        List<CanFrame> frames, Dictionary<(uint, bool), long> ids)
    {
        var matchedIds = new HashSet<(uint, bool)>();
        long matchedFrames = 0, dlcChecked = 0, dlcOk = 0, sigChecked = 0, sigOk = 0;
        foreach (var frame in frames)
        {
            if (!decoder.TryGetMessage(frame, out var message)) continue;
            matchedFrames++;
            matchedIds.Add((frame.Identifier, frame.IsExtended));
            dlcChecked++;
            if (frame.DataLength >= message.Length) dlcOk++;
            foreach (var value in decoder.Decode(frame))
            {
                var signal = message.Signals.FirstOrDefault(s => s.Name == value.Signal);
                if (signal is null || signal.Minimum == signal.Maximum) continue;
                sigChecked++;
                var lo = Math.Min(signal.Minimum, signal.Maximum);
                var hi = Math.Max(signal.Minimum, signal.Maximum);
                var eps = 1e-9 * Math.Max(1.0, hi - lo);
                if (double.IsFinite(value.PhysicalValue) &&
                    value.PhysicalValue >= lo - eps && value.PhysicalValue <= hi + eps) sigOk++;
            }
        }
        static double Share(long a, long b) => b == 0 ? 0 : (double)a / b;
        var idShare = Share(matchedIds.Count, ids.Count);
        var frameShare = Share(matchedFrames, frames.Count);
        var dlcShare = Share(dlcOk, dlcChecked);
        var rangeShare = Share(sigOk, sigChecked);
        var composite = DbcSuggestionBreakdown.IdentifierWeight * idShare +
                        DbcSuggestionBreakdown.FrameWeight * frameShare +
                        DbcSuggestionBreakdown.DlcWeight * dlcShare +
                        DbcSuggestionBreakdown.SignalRangeWeight * rangeShare;
        var unmatched = ids.Where(kv => !matchedIds.Contains(kv.Key))
            .OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2)
            .Select(kv => new DbcUnmatchedIdentifier(channel, kv.Key.Item1, kv.Key.Item2, kv.Value))
            .ToList();
        return new DbcSuggestionScore(candidate.Name, index,
            new DbcSuggestionBreakdown(ids.Count, matchedIds.Count, frames.Count, matchedFrames,
                dlcChecked, dlcOk, sigChecked, sigOk, idShare, frameShare, dlcShare, rangeShare, composite),
            unmatched);
    }
}
