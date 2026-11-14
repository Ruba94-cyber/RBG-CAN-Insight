using System.Threading;
using RbgCanInsight.DiagData;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Diagnostics;

/// <summary>A phase/progress report emitted while <see cref="UdsTransactionAnalyzer"/> works
/// through a trace, so a caller can show percent-complete, elapsed time and an ETA instead of a
/// static "working" indicator.</summary>
public readonly record struct UdsAnalysisProgress(string Phase, int Completed, int Total);

/// <summary>Builds request/response transactions from reconstructed ISO-TP PDUs. Pairing uses
/// explicit ISO addressing where possible and falls back to channel/SID/time evidence with the
/// endpoint visibly marked as inferred.</summary>
public static class UdsTransactionAnalyzer
{
    private sealed record FlowControlIndexEntry(
        long[] Timestamps, int[] Indices, uint[] Identifiers, bool[] IsExtended);

    private sealed class PendingTransaction
    {
        public required IsoTpMessage Request { get; init; }
        public required byte ServiceId { get; init; }
        public required DiagnosticEndpoint Endpoint { get; init; }
        public List<IsoTpMessage> Responses { get; } = [];
        public bool Completed { get; set; }

        /// <summary>A functionally addressed request is answered by every ECU that supports the
        /// service. Responses are kept per responding identifier so each ECU gets its own
        /// transaction instead of the first answer closing the request for all the others.</summary>
        public Dictionary<uint, List<IsoTpMessage>>? FunctionalResponders { get; set; }
        public bool AnsweredFunctionally { get; set; }
    }

    public static IReadOnlyList<DiagnosticTransaction> Analyze(
        IReadOnlyList<CanFrame> frames,
        IReadOnlyList<DidCatalog>? catalogs = null) =>
        Analyze(frames, catalogs, progress: null, cancellationToken: default);

    /// <summary>Source-aware convenience overload for ordinary offline tests
    /// and callers that need physical logger isolation without progress callbacks.</summary>
    public static IReadOnlyList<DiagnosticTransaction> Analyze(
        IReadOnlyList<CanFrame> frames,
        IReadOnlyList<DidCatalog>? catalogs,
        Func<int, int> sourceAt) =>
        Analyze(frames, catalogs, progress: null, cancellationToken: default,
            sourceAt: sourceAt);

    /// <summary>Same analysis, additionally reporting phase/percent progress and honoring
    /// cancellation — the pairing and transaction-building phases are the only ones costly enough
    /// on a large capture to be worth checking in on.</summary>
    public static IReadOnlyList<DiagnosticTransaction> Analyze(
        IReadOnlyList<CanFrame> frames,
        IReadOnlyList<DidCatalog>? catalogs,
        IProgress<UdsAnalysisProgress>? progress,
        CancellationToken cancellationToken,
        Func<int, int>? sourceAt = null)
    {
        catalogs ??= [];
        var kwp = KwpServiceDecoder.IsKwpCatalogSet(catalogs);
        var messages = IsoTpReassembler.Reassemble(
                frames, cancellationToken, progress, sourceAt)
            .Where(message => IsPlausibleUds(message, kwp))
            .OrderBy(message => message.StartTimestampNanoseconds)
            .ToArray();
        // Outstanding requests on unrelated buses, identifier widths or
        // services cannot match the same response. Index by that
        // immutable transport discriminator rather than scanning the
        // entire capture's outstanding requests on every UDS reply.
        var pending = new Dictionary<
            (byte Service, int Source, int Channel, bool Extended), List<PendingTransaction>>();
        var completed = new List<(PendingTransaction Pending, DiagnosticOutcome Outcome)>();
        var orphans = new List<(IsoTpMessage Message, string? Reason)>();

        var messageIndex = 0;
        foreach (var message in messages)
        {
            messageIndex++;
            if (messageIndex % 256 == 0 || messageIndex == messages.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new UdsAnalysisProgress("Pairing requests and responses", messageIndex, messages.Length));
            }

            if (message.Payload.Length == 0)
            {
                continue;
            }

            var source = sourceAt?.Invoke(message.FrameIndices[0]) ?? 0;
            if (source < 0)
                throw new ArgumentOutOfRangeException(nameof(sourceAt));
            var sid = message.Payload[0];
            if (UdsServiceDecoder.IsKnownRequest(sid, kwp))
            {
                var routeKey = (sid, source, message.Channel, message.IsExtended);
                if (!pending.TryGetValue(routeKey, out var routePending))
                {
                    routePending = [];
                    pending[routeKey] = routePending;
                }
                routePending.Add(new PendingTransaction
                {
                    Request = message,
                    ServiceId = sid,
                    Endpoint = EndpointForRequest(message, catalogs)
                });
                continue;
            }

            byte? requestedService = sid switch
            {
                0x7F when message.Payload.Length >= 3 => message.Payload[1],
                _ when UdsServiceDecoder.IsPositiveResponse(sid, kwp) => (byte)(sid - 0x40),
                _ => null
            };
            if (requestedService is null)
            {
                continue;
            }

            // A discriminator narrows plausible requests but does not
            // uniquely identify a request if two identical/overlapping
            // requests are still outstanding. Never choose the newest
            // request merely by its timestamp: that invents a causal link.
            PendingTransaction? match = null;
            var ambiguousResponse = false;
            var responseKey = (requestedService.Value, source, message.Channel, message.IsExtended);
            var matchesThisRoute = pending.TryGetValue(responseKey, out var eligible)
                ? (IReadOnlyList<PendingTransaction>)eligible
                : Array.Empty<PendingTransaction>();
            // A request sent with suppressPosRspMsgIndicationBit expects no positive response
            // (ISO 14229-1 8.2.2) unless the ECU first answered responsePending. Such a request
            // is therefore only considered for a positive response when no ordinary request fits.
            var positiveResponse = sid != 0x7F;
            for (var pass = 0; pass < (positiveResponse ? 2 : 1) && match is null && !ambiguousResponse; pass++)
            {
                foreach (var candidate in matchesThisRoute)
                {
                    if (candidate.Completed || candidate.ServiceId != requestedService ||
                        !IsWithinPairingWindow(candidate, message) ||
                        !EndpointsCompatible(candidate.Request, message, catalogs) ||
                        !ResponsePayloadMatchesRequest(candidate.Request.Payload, message.Payload))
                        continue;
                    if (positiveResponse)
                    {
                        var expectsNoPositiveResponse =
                            HasSuppressPositiveResponseBit(candidate.Request.Payload) &&
                            !HasPendingResponse(candidate, message.Identifier);
                        if (expectsNoPositiveResponse != (pass == 1))
                            continue;
                    }

                    if (match is not null)
                    {
                        // Both an NRC without DID and a positive response whose
                        // discriminator matches multiple open requests must
                        // remain unassigned. Do not suppress this warning even
                        // for OEM CAN IDs outside the conventional UDS range.
                        ambiguousResponse = true;
                        break;
                    }

                    match = candidate;
                }
            }
            if (ambiguousResponse)
                match = null;
            if (match is null)
            {
                if (IsConventionalDiagnosticIdentifier(message) || ambiguousResponse)
                {
                    orphans.Add((message, ambiguousResponse
                        ? sid == 0x7F
                            ? "Negative UDS response cannot be uniquely paired: multiple open requests share the same SID and compatible endpoint."
                            : "Positive UDS response cannot be uniquely paired: multiple open requests match its service, discriminator and physical endpoint."
                        : null));
                }
                continue;
            }

            var responsePending = sid == 0x7F && message.Payload.Length >= 3 && message.Payload[2] == 0x78;
            if (IsFunctionalRequest(match.Request))
            {
                // One transaction per responding ECU; the request stays open for the others.
                match.FunctionalResponders ??= [];
                if (!match.FunctionalResponders.TryGetValue(message.Identifier, out var fromThisEcu))
                {
                    fromThisEcu = [];
                    match.FunctionalResponders[message.Identifier] = fromThisEcu;
                }

                fromThisEcu.Add(message);
                if (responsePending)
                    continue;
                var perEcu = new PendingTransaction
                {
                    Request = match.Request,
                    ServiceId = match.ServiceId,
                    Endpoint = match.Endpoint,
                    Completed = true
                };
                perEcu.Responses.AddRange(fromThisEcu);
                match.FunctionalResponders.Remove(message.Identifier);
                match.AnsweredFunctionally = true;
                completed.Add((perEcu, sid == 0x7F ? DiagnosticOutcome.Negative : DiagnosticOutcome.Positive));
                continue;
            }

            match.Responses.Add(message);
            if (responsePending)
            {
                // responsePending belongs to the same transaction; wait for its terminal reply.
                continue;
            }

            match.Completed = true;
            completed.Add((match, sid == 0x7F ? DiagnosticOutcome.Negative : DiagnosticOutcome.Positive));
            // Keep only outstanding requests in the pairing scan: traces may contain
            // hundreds of thousands of completed UDS transactions.
            eligible!.Remove(match);
        }

        foreach (var item in pending.Values.SelectMany(route => route)
                     .Where(item => !item.Completed && !item.AnsweredFunctionally))
        {
            if (!IsConventionalDiagnosticIdentifier(item.Request) && !HasCatalogEvidence(item.Request, catalogs))
            {
                continue;
            }
            var suppress = HasSuppressPositiveResponseBit(item.Request.Payload);
            completed.Add((item, suppress ? DiagnosticOutcome.SuppressedResponse :
                item.Request.IsComplete ? DiagnosticOutcome.NoResponse : DiagnosticOutcome.Malformed));
        }

        var flowControlIndex = BuildFlowControlIndex(frames, sourceAt);
        var transactions = new List<DiagnosticTransaction>();
        var buildTotal = completed.Count + orphans.Count;
        var buildIndex = 0;
        foreach (var item in completed.OrderBy(item => item.Pending.Request.StartTimestampNanoseconds))
        {
            buildIndex++;
            if (buildIndex % 256 == 0 || buildIndex == buildTotal)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new UdsAnalysisProgress("Building transactions", buildIndex, buildTotal));
            }

            transactions.Add(Build(
                transactions.Count + 1,
                item.Pending.Request,
                item.Pending.Responses,
                ResolveObservedEndpoint(
                    item.Pending.Request, item.Pending.Responses,
                    item.Pending.Endpoint, catalogs),
                item.Outcome,
                catalogs,
                flowControlIndex,
                frames,
                sourceAt));
        }

        foreach (var (orphan, orphanReason) in orphans)
        {
            buildIndex++;
            if (buildIndex % 256 == 0 || buildIndex == buildTotal)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new UdsAnalysisProgress("Building transactions", buildIndex, buildTotal));
            }

            var orphanTransaction = Build(
                transactions.Count + 1,
                null,
                [orphan],
                EndpointForResponse(orphan, catalogs),
                DiagnosticOutcome.OrphanResponse,
                catalogs,
                flowControlIndex,
                frames,
                sourceAt);
            if (orphanReason is not null)
                orphanTransaction = orphanTransaction with { Quality = "Ambiguous · " + orphanReason };
            transactions.Add(orphanTransaction);
        }

        return transactions
            .OrderBy(transaction => transaction.StartTimestampNanoseconds)
            .Select((transaction, index) => transaction with { Sequence = index + 1 })
            .ToArray();
    }

    /// <summary>Per-channel, timestamp-sorted flow-control frame indices, built once per analysis
    /// so each transaction can binary-search its own time span instead of scanning every frame in
    /// the trace. On a capture with hundreds of thousands of frames and hundreds of transactions,
    /// the previous full scan per transaction was O(frames × transactions); this is O(frames log
    /// frames) to build plus O(log frames + hits) per transaction.</summary>
    private static Dictionary<(int Source, int Channel), FlowControlIndexEntry> BuildFlowControlIndex(
        IReadOnlyList<CanFrame> frames, Func<int, int>? sourceAt)
    {
        var byChannel = new Dictionary<(int Source, int Channel),
            List<(long Timestamp, int Index, uint Id, bool Extended)>>();
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            // Raw data with a 0x3? first byte is NOT automatically ISO-TP.
            // Require a legal FC control nibble, at least PCI/BS/STmin,
            // and a real data frame before considering it as transport evidence.
            if (frame.IsError || frame.Flags.HasFlag(CanFrameFlags.Remote) ||
                frame.DataLength < 3 || frame.GetByte(0) >> 4 != 0x3 ||
                (frame.GetByte(0) & 0x0F) > 2)
            {
                continue;
            }
            var source = sourceAt?.Invoke(index) ?? 0;
            if (source < 0)
                throw new ArgumentOutOfRangeException(nameof(sourceAt));
            var key = (source, frame.Channel);
            if (!byChannel.TryGetValue(key, out var list))
            {
                list = [];
                byChannel[key] = list;
            }
            list.Add((frame.TimestampNanoseconds, index, frame.Identifier, frame.IsExtended));
        }

        var result = new Dictionary<(int Source, int Channel), FlowControlIndexEntry>();
        foreach (var (key, list) in byChannel)
        {
            list.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            result[key] = new FlowControlIndexEntry(
                list.Select(entry => entry.Timestamp).ToArray(),
                list.Select(entry => entry.Index).ToArray(),
                list.Select(entry => entry.Id).ToArray(),
                list.Select(entry => entry.Extended).ToArray());
        }

        return result;
    }

    private static IEnumerable<int> FlowControlFramesInSpan(
        IReadOnlyDictionary<(int Source, int Channel), FlowControlIndexEntry> index,
        int source, int channel,
        long start,
        long end,
        IReadOnlySet<(uint Id, bool Extended)> endpointIds)
    {
        if (!index.TryGetValue((source, channel), out var entry))
        {
            yield break;
        }

        var lower = Array.BinarySearch(entry.Timestamps, start);
        if (lower < 0)
        {
            lower = ~lower;
        }
        else
        {
            while (lower > 0 && entry.Timestamps[lower - 1] == start)
            {
                lower--;
            }
        }

        for (var i = lower; i < entry.Timestamps.Length && entry.Timestamps[i] <= end; i++)
        {
            // A same-channel signal from an unrelated ECU cannot become
            // an apparent transport frame in another UDS transaction.
            if (endpointIds.Contains((entry.Identifiers[i], entry.IsExtended[i])))
                yield return entry.Indices[i];
        }
    }

    private static DiagnosticTransaction Build(
        int sequence,
        IsoTpMessage? request,
        IReadOnlyList<IsoTpMessage> responses,
        DiagnosticEndpoint endpoint,
        DiagnosticOutcome outcome,
        IReadOnlyList<DidCatalog> catalogs,
        IReadOnlyDictionary<(int Source, int Channel), FlowControlIndexEntry> flowControlIndex,
        IReadOnlyList<CanFrame> frames,
        Func<int, int>? sourceAt)
    {
        // Only a unique catalog-confirmed endpoint constrains ECU-specific
        // DID and DTC layouts. Inferred CAN addressing is not a verified
        // OEM identity; retain the conservative globally-unique fallback.
        var decoded = UdsServiceDecoder.Decode(
            request, responses, catalogs,
            endpoint.Evidence == DiagnosticEvidence.Confirmed ? endpoint.Ecu : null);
        if (decoded.Values.Count > 0 || decoded.DtcRecords.Count > 0)
        {
            // Both DID and DTC snapshot/extended-data fields have precise
            // offsets in the reassembled UDS response. Verify the entire
            // ISO-TP response against original frames before trusting either.
            // Decoded CDD/ODX fields contain a bit interval in the UDS PDU.
            // Prove the *whole* PDU against original immutable CAN frame
            // data first: a plausible bit offset is never enough to claim
            // a source-backed DID/CAN numerical observation.
            var response = responses.LastOrDefault(message =>
                message.Payload.Length > 0 && message.Payload[0] != 0x7F)
                ?? responses.LastOrDefault();
            IsoTpSourceByteIndex? sourceIndex = null;
            if (response is { IsComplete: true })
                IsoTpSourceByteIndex.TryBuild(frames, response, out sourceIndex);

            var verified = new List<DecodedDiagnosticValue>(decoded.Values.Count);
            foreach (var value in decoded.Values)
            {
                if (sourceIndex is not null &&
                    value.ResponseBitOffset is { } bitOffset &&
                    value.ResponseBitLength is { } bitLength &&
                    sourceIndex.TryMap(bitOffset, bitLength, out var sourceSpans))
                {
                    verified.Add(value with { SourceSpans = sourceSpans });
                }
                else
                {
                    // Keep the exact recorded raw representation readable,
                    // but do not make an unproven physical value eligible
                    // for later statistical DID/CAN identity inferences.
                    verified.Add(value with
                    {
                        NumericValue = null,
                        SourceSpans = null,
                        Physical = value.Raw,
                        Meaning = (string.IsNullOrWhiteSpace(value.Meaning)
                            ? string.Empty : value.Meaning + " · ") +
                            "Source CAN bytes not verified; physical measurement unavailable."
                    });
                }
            }
            if (decoded.DtcRecords.Count > 0)
            {
                var records = new List<DecodedDtcRecord>(decoded.DtcRecords.Count);
                foreach (var record in decoded.DtcRecords)
                {
                    IReadOnlyList<DiagnosticSourceByteSpan>? recordSpans = null;
                    if (sourceIndex is not null &&
                        record.ResponseByteOffset is { } recordStart &&
                        record.ResponseByteCount is > 0 &&
                        sourceIndex.TryMap(recordStart * 8, record.ResponseByteCount.Value * 8,
                            out var mappedRecord))
                        recordSpans = mappedRecord;

                    var fields = record.Values.Select(value =>
                    {
                        if (sourceIndex is not null &&
                            value.ResponseBitOffset is { } bitOffset &&
                            value.ResponseBitLength is { } bitLength &&
                            sourceIndex.TryMap(bitOffset, bitLength, out var spans))
                            return value with { SourceSpans = spans };

                        return value with
                        {
                            SourceSpans = null,
                            NumericValue = null,
                            Physical = value.Raw,
                            Meaning = (string.IsNullOrWhiteSpace(value.Meaning)
                                ? "" : value.Meaning + " · ") +
                                "Original CAN source bytes unverified; physical value not established."
                        };
                    }).ToArray();
                    records.Add(record with
                    {
                        Values = fields,
                        SourceSpans = recordSpans,
                        Note = recordSpans is null
                            ? (string.IsNullOrEmpty(record.Note) ? "" : record.Note + "; ") +
                              "Original CAN frames do not verify this DTC record."
                            : record.Note
                    });
                }
                var byCode = records.GroupBy(record => record.Code)
                    .ToDictionary(group => group.Key,
                        group => string.Join(" | ", group.Select(record => record.SummaryText)));
                decoded = decoded with
                {
                    DtcRecordList = records,
                    Dtcs = decoded.Dtcs.Select(dtc => byCode.TryGetValue(dtc.Code, out var detail)
                        ? dtc with { ExtraData = detail }
                        : dtc).ToArray()
                };
            }
            decoded = decoded with { Values = verified };
        }
        var start = request?.StartTimestampNanoseconds ?? responses.First().StartTimestampNanoseconds;
        var end = responses.Count > 0 ? responses[^1].EndTimestampNanoseconds : request?.EndTimestampNanoseconds ?? start;
        var frameIndices = (request?.FrameIndices ?? [])
            .Concat(responses.SelectMany(response => response.FrameIndices))
            .ToHashSet();

        // FC records can be included only when the *actual* frame matches a
        // source endpoint of this transaction, not merely the bus and time.
        // This conservative boundary is not proof of an ISO-TP FC causal edge:
        // a missing direction/FF or OEM transport ambiguity stays unconfirmed.
        var channel = request?.Channel ?? responses[0].Channel;
        var endpointIds = responses.Select(response => (response.Identifier, response.IsExtended))
            .ToHashSet();
        if (request is not null)
            endpointIds.Add((request.Identifier, request.IsExtended));
        var fcCandidateCount = 0;
        var physicalSource = sourceAt?.Invoke((request?.FrameIndices ?? responses[0].FrameIndices)[0]) ?? 0;
        foreach (var index in FlowControlFramesInSpan(
            flowControlIndex, physicalSource, channel, start, end, endpointIds))
        {
            if (frameIndices.Add(index))
                fcCandidateCount++;
        }

        var transportIssues = new[] { request }.Where(message => message is not null)
            .Cast<IsoTpMessage>()
            .Concat(responses)
            .Where(message => !message.IsComplete || !string.IsNullOrWhiteSpace(message.TransportIssue))
            .Select(message => message.TransportIssue)
            .Where(issue => !string.IsNullOrWhiteSpace(issue))
            .Distinct()
            .ToArray();
        var quality = transportIssues.Length > 0
            ? "Malformed · " + string.Join("; ", transportIssues)
            : endpoint.Evidence switch
            {
                DiagnosticEvidence.Confirmed => "Confirmed addressing",
                DiagnosticEvidence.Inferred => "Inferred addressing",
                _ => "Unknown endpoints"
            };
        if (fcCandidateCount > 0)
            quality += $" · {fcCandidateCount} FC candidate(s), same endpoint/time (causal link unverified)";
        if (decoded.ServiceId == 0x22 &&
            !decoded.DidPayloadFullyDecoded &&
            (outcome is DiagnosticOutcome.Positive or DiagnosticOutcome.Malformed))
        {
            quality += " · Incomplete DID layout: one or more response fields cannot be proven from CDD/ODX.";
        }

        return new DiagnosticTransaction(
            sequence,
            request,
            responses,
            endpoint,
            transportIssues.Length > 0 ? DiagnosticOutcome.Malformed : outcome,
            decoded,
            start,
            end,
            request is not null && responses.Count > 0
                ? Math.Max(0, responses[^1].StartTimestampNanoseconds - request.EndTimestampNanoseconds) / 1_000_000d
                : null,
            frameIndices.OrderBy(index => index).ToArray(),
            quality);
    }

    private static bool IsPlausibleUds(IsoTpMessage message, bool kwp = false)
    {
        if (message.Payload.Length == 0) return false;
        if (!message.IsComplete && !IsConventionalDiagnosticIdentifier(message)) return false;
        var sid = message.Payload[0];
        if (sid == 0x7F)
        {
            return message.Payload.Length >= 3 && UdsServiceDecoder.IsKnownRequest(message.Payload[1], kwp);
        }
        // One-byte PDUs are legal for a few services, but a lone byte on an arbitrary CAN
        // identifier is not evidence of UDS: accept them only on conventional diagnostic IDs.
        var conventional = IsConventionalDiagnosticIdentifier(message);
        if (UdsServiceDecoder.IsPositiveResponse(sid, kwp))
        {
            // Positive ClearDiagnosticInformation is only SID 0x54; RequestTransferExit (0x77)
            // and ReadDataByPeriodicIdentifier (0x6A) may also carry no parameter.
            var sidOnly = sid == 0x54 || (!kwp && conventional && sid is 0x77 or 0x6A);
            return message.Payload.Length >= (sidOnly ? 1 : 2);
        }
        if (!UdsServiceDecoder.IsKnownRequest(sid, kwp)) return false;
        return sid switch
        {
            0x22 or 0x2E or 0x2F or 0x24 => message.Payload.Length >= 3,
            0x31 => message.Payload.Length >= 4,
            // RequestTransferExit without a parameter record is the normal end of a download.
            0x37 when !kwp && conventional => message.Payload.Length >= 1,
            _ => message.Payload.Length >= 2
        };
    }

    /// <summary>
    /// Match the discriminator in positive responses before choosing the nearest
    /// request by time. Otherwise two overlapping 0x22 reads for different DIDs
    /// can be silently swapped even on the same physical diagnostic endpoint.
    /// </summary>
    private static bool ResponsePayloadMatchesRequest(byte[] request, byte[] response)
    {
        if (request.Length == 0 || response.Length == 0) return false;
        if (response[0] == 0x7F) return true; // NRC omits DID/routine id.
        var sid = request[0];
        if (response[0] != sid + 0x40) return false;
        return sid switch
        {
            0x22 or 0x24 or 0x2E or 0x2F =>
                request.Length >= 3 && response.Length >= 3 &&
                request[1] == response[1] && request[2] == response[2],
            0x31 =>
                request.Length >= 4 && response.Length >= 4 &&
                (request[1] & 0x7F) == (response[1] & 0x7F) &&
                request[2] == response[2] && request[3] == response[3],
            0x10 or 0x11 or 0x19 or 0x27 or 0x28 or 0x3E or 0x85 or 0x87 =>
                request.Length >= 2 && response.Length >= 2 &&
                (request[1] & 0x7F) == (response[1] & 0x7F),
            _ => true
        };
    }

    /// <summary>A response must follow the request within the window. While the ECU keeps
    /// answering responsePending (NRC 0x78) the wait restarts from the latest one: P2* has no
    /// overall limit, so a long erase routine must still pair with its final response.</summary>
    private static bool IsWithinPairingWindow(PendingTransaction candidate, IsoTpMessage response)
    {
        var request = candidate.Request;
        if (response.StartTimestampNanoseconds < request.EndTimestampNanoseconds)
            return false;
        var reference = request.EndTimestampNanoseconds;
        var previous = candidate.FunctionalResponders is { } responders
            ? responders.TryGetValue(response.Identifier, out var fromThisEcu) ? fromThisEcu : null
            : candidate.Responses;
        if (previous is { Count: > 0 })
            reference = Math.Max(reference, previous[^1].EndTimestampNanoseconds);
        return response.StartTimestampNanoseconds - reference <= 30_000_000_000L;
    }

    private static bool HasPendingResponse(PendingTransaction candidate, uint responseIdentifier) =>
        candidate.FunctionalResponders is { } responders
            ? responders.TryGetValue(responseIdentifier, out var fromThisEcu) && fromThisEcu.Count > 0
            : candidate.Responses.Count > 0;

    /// <summary>11-bit OBD functional identifier 0x7DF, or 29-bit normal fixed functional
    /// addressing (0x18DB target-source).</summary>
    private static bool IsFunctionalRequest(IsoTpMessage request) =>
        request.IsExtended
            ? (request.Identifier & 0x1FFF0000u) == 0x18DB0000u
            : request.Identifier == 0x7DF;

    private static bool EndpointsCompatible(
        IsoTpMessage request,
        IsoTpMessage response,
        IReadOnlyList<DidCatalog> catalogs)
    {
        if (request.Channel != response.Channel || request.IsExtended != response.IsExtended) return false;
        // A declared request->response route is a constraint, not merely
        // an optional positive hint. If the loaded CDD/ODX declares the
        // expected reply ID and the observed one differs, never fall back
        // to a permissive OEM SID/time heuristic.
        var configured = catalogs.SelectMany(catalog => catalog.ConnectionDefinitions)
            .Where(connection => connection.RequestIdentifier == request.Identifier &&
                                 connection.IsExtendedIdentifier == request.IsExtended)
            .ToArray();
        var declaredResponses = configured.Where(connection =>
            connection.ResponseIdentifier.HasValue).ToArray();
        if (declaredResponses.Length > 0)
            return declaredResponses.Any(connection =>
                connection.ResponseIdentifier == response.Identifier);
        if (!request.IsExtended && !response.IsExtended && request.Identifier is >= 0x7E0 and <= 0x7E7)
        {
            return response.Identifier == request.Identifier + 8;
        }
        if (!request.IsExtended && request.Identifier == 0x7DF)
        {
            return response.Identifier is >= 0x7E8 and <= 0x7EF;
        }
        if (request.IsExtended && response.IsExtended && IsNormalFixed(request.Identifier) && IsNormalFixed(response.Identifier))
        {
            // A functional request (0x18DB) is answered physically (0x18DA) by each ECU with its
            // own source address, addressed back to the tester.
            if (IsFunctionalRequest(request))
                return (response.Identifier & 0x1FFF0000u) == 0x18DA0000u &&
                       Source(request.Identifier) == Target(response.Identifier);
            return Source(request.Identifier) == Target(response.Identifier) &&
                   Target(request.Identifier) == Source(response.Identifier);
        }

        // OEM-specific IDs cannot be proven from identifier shape alone. SID and timing are
        // accepted as inference, but never presented as confirmed attribution.
        return request.Identifier != response.Identifier || request.StartTimestampNanoseconds < response.StartTimestampNanoseconds;
    }

    /// <summary>Confirm a matched transaction's ECU from the complete
    /// observed request+response identifiers, rather than guessing it from
    /// a request-only catalog entry or load order.</summary>
    private static DiagnosticEndpoint ResolveObservedEndpoint(
        IsoTpMessage request,
        IReadOnlyList<IsoTpMessage> responses,
        DiagnosticEndpoint requestEndpoint,
        IReadOnlyList<DidCatalog> catalogs)
    {
        if (responses.Count == 0)
            return requestEndpoint;
        var response = responses[^1];
        var pairMatches = catalogs.SelectMany(catalog => catalog.ConnectionDefinitions)
            .Where(connection => connection.RequestIdentifier == request.Identifier &&
                connection.ResponseIdentifier == response.Identifier &&
                connection.IsExtendedIdentifier == request.IsExtended &&
                response.IsExtended == request.IsExtended &&
                response.Channel == request.Channel)
            .Take(2).ToArray();
        if (pairMatches.Length == 1)
        {
            var confirmed = pairMatches[0];
            return new DiagnosticEndpoint(
                "Tester", confirmed.Ecu, DiagnosticDirection.TesterToEcu,
                DiagnosticEvidence.Confirmed,
                $"{confirmed.AddressingType} request 0x{request.Identifier:X} / response 0x{response.Identifier:X}");
        }
        if (pairMatches.Length > 1)
        {
            return requestEndpoint with
            {
                Ecu = $"Ambiguous ECU (0x{request.Identifier:X} / 0x{response.Identifier:X})",
                Evidence = DiagnosticEvidence.Unknown,
                Addressing = "multiple CDD/ODX ECU definitions for same observed CAN endpoints"
            };
        }
        if (requestEndpoint.Evidence == DiagnosticEvidence.Confirmed)
        {
            // A request-only route is not a confirmed observed response
            // identity. Keep the navigation while refusing ECU-specific
            // physical decoding until an exact source connection exists.
            return requestEndpoint with
            {
                Evidence = DiagnosticEvidence.Inferred,
                Addressing = requestEndpoint.Addressing + " · response identity not catalog-verified"
            };
        }
        if (IsFunctionalRequest(request))
        {
            // The request addressed every ECU; the answer identifies which one this transaction
            // belongs to. A catalog may name the ECU that owns the response identifier, but the
            // route itself was not a declared physical request, so it stays inferred.
            var owners = catalogs.SelectMany(catalog => catalog.ConnectionDefinitions)
                .Where(connection => connection.ResponseIdentifier == response.Identifier &&
                                     connection.IsExtendedIdentifier == response.IsExtended)
                .Select(connection => connection.Ecu)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
            var ecu = owners.Length == 1
                ? owners[0]
                : response.IsExtended
                    ? $"0x{Source(response.Identifier):X2}"
                    : response.Identifier is >= 0x7E8 and <= 0x7EF
                        ? $"ECU {response.Identifier - 0x7E8:X1}"
                        : $"ID 0x{response.Identifier:X}";
            return requestEndpoint with
            {
                Ecu = ecu,
                Evidence = DiagnosticEvidence.Inferred,
                Addressing = $"{requestEndpoint.Addressing} · answered by 0x{response.Identifier:X}"
            };
        }
        return requestEndpoint;
    }

    private static DiagnosticEndpoint EndpointForRequest(
        IsoTpMessage request,
        IReadOnlyList<DidCatalog> catalogs)
    {
        // Numeric CAN ID alone cannot name an ECU: 11-bit and 29-bit
        // arbitration identities are distinct. Conflicting catalog entries
        // must never be promoted to "Confirmed addressing" by FirstOrDefault.
        var configuredCandidates = catalogs.SelectMany(catalog => catalog.ConnectionDefinitions)
            .Where(connection => connection.RequestIdentifier == request.Identifier &&
                                 connection.IsExtendedIdentifier == request.IsExtended)
            .ToArray();
        var configured = configuredCandidates.Length == 1 ? configuredCandidates[0] : null;
        if (configured is not null)
        {
            return new DiagnosticEndpoint("Tester", configured.Ecu, DiagnosticDirection.TesterToEcu,
                DiagnosticEvidence.Confirmed,
                $"{configured.AddressingType} 0x{request.Identifier:X}");
        }
        if (!request.IsExtended && request.Identifier is >= 0x7E0 and <= 0x7E7)
        {
            return new DiagnosticEndpoint("Tester", $"ECU {(request.Identifier - 0x7E0):X1}",
                DiagnosticDirection.TesterToEcu, DiagnosticEvidence.Inferred,
                $"physical 11-bit 0x{request.Identifier:X3}");
        }
        if (!request.IsExtended && request.Identifier == 0x7DF)
        {
            return new DiagnosticEndpoint("Tester", "Functional broadcast",
                DiagnosticDirection.TesterToEcu, DiagnosticEvidence.Inferred, "functional 11-bit 0x7DF");
        }
        if (request.IsExtended && IsNormalFixed(request.Identifier))
        {
            return new DiagnosticEndpoint($"0x{Source(request.Identifier):X2}", $"0x{Target(request.Identifier):X2}",
                DiagnosticDirection.TesterToEcu, DiagnosticEvidence.Inferred,
                $"29-bit 0x{request.Identifier:X8}");
        }
        return new DiagnosticEndpoint("Tester?", $"ID 0x{request.Identifier:X}",
            DiagnosticDirection.TesterToEcu, DiagnosticEvidence.Unknown, "OEM identifier · role inferred from SID");
    }

    private static DiagnosticEndpoint EndpointForResponse(
        IsoTpMessage response,
        IReadOnlyList<DidCatalog> catalogs)
    {
        var configuredCandidates = catalogs.SelectMany(catalog => catalog.ConnectionDefinitions)
            .Where(connection => connection.ResponseIdentifier == response.Identifier &&
                                 connection.IsExtendedIdentifier == response.IsExtended)
            .ToArray();
        var configured = configuredCandidates.Length == 1 ? configuredCandidates[0] : null;
        if (configured is not null)
        {
            return new DiagnosticEndpoint("Tester", configured.Ecu, DiagnosticDirection.EcuToTester,
                DiagnosticEvidence.Confirmed,
                $"{configured.AddressingType} 0x{response.Identifier:X}");
        }
        if (!response.IsExtended && response.Identifier is >= 0x7E8 and <= 0x7EF)
        {
            return new DiagnosticEndpoint("Tester", $"ECU {(response.Identifier - 0x7E8):X1}",
                DiagnosticDirection.EcuToTester, DiagnosticEvidence.Inferred,
                $"physical 11-bit 0x{response.Identifier:X3}");
        }
        if (response.IsExtended && IsNormalFixed(response.Identifier))
        {
            return new DiagnosticEndpoint($"0x{Target(response.Identifier):X2}", $"0x{Source(response.Identifier):X2}",
                DiagnosticDirection.EcuToTester, DiagnosticEvidence.Inferred,
                $"29-bit 0x{response.Identifier:X8}");
        }
        return new DiagnosticEndpoint("Tester?", $"ID 0x{response.Identifier:X}",
            DiagnosticDirection.EcuToTester, DiagnosticEvidence.Unknown, "OEM identifier · role inferred from response SID");
    }

    private static bool HasSuppressPositiveResponseBit(byte[] request)
    {
        if (request.Length < 2) return false;
        return request[0] is 0x10 or 0x11 or 0x19 or 0x27 or 0x28 or 0x29 or 0x2C or 0x31 or 0x3E or 0x83 or 0x85 or 0x86 or 0x87 &&
               (request[1] & 0x80) != 0;
    }

    private static bool HasCatalogEvidence(IsoTpMessage message, IReadOnlyList<DidCatalog> catalogs)
    {
        if (message.Payload.Length < 3 || message.Payload[0] is not (0x22 or 0x2E or 0x2F))
        {
            return false;
        }
        var identifier = (message.Payload[1] << 8) | message.Payload[2];
        return catalogs.SelectMany(catalog => catalog.Parameters).Any(did => did.Did == identifier);
    }

    private static bool IsConventionalDiagnosticIdentifier(IsoTpMessage message) =>
        message.IsExtended
            ? IsNormalFixed(message.Identifier)
            : message.Identifier is >= 0x700 and <= 0x7FF;

    private static bool IsNormalFixed(uint id) => (id & 0x1FFF0000u) is 0x18DA0000u or 0x18DB0000u;
    private static byte Target(uint id) => (byte)((id >> 8) & 0xFF);
    private static byte Source(uint id) => (byte)(id & 0xFF);
}
