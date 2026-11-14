using System.Globalization;
using RbgCanInsight.Domain;

namespace RbgCanInsight.Diagnostics.Calibration;

/// <summary>Offline XCP-on-CAN and CCP 2.1 decoder. Frame indices refer to the input list.
/// Command names outside the tables below are intentionally left raw.</summary>
public static class XcpCcpDecoder
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(500);

    private static readonly IReadOnlyDictionary<byte, string> XcpCommands = new Dictionary<byte, string>
    {
        [0xFF] = "CONNECT", [0xFE] = "DISCONNECT", [0xFD] = "GET_STATUS", [0xFC] = "SYNCH",
        [0xFB] = "GET_COMM_MODE_INFO", [0xFA] = "GET_ID", [0xF9] = "SET_REQUEST", [0xF8] = "GET_SEED",
        [0xF7] = "UNLOCK", [0xF6] = "SET_MTA", [0xF5] = "UPLOAD", [0xF4] = "SHORT_UPLOAD",
        [0xF3] = "BUILD_CHECKSUM", [0xF2] = "TRANSPORT_LAYER_CMD", [0xF1] = "USER_CMD",
        [0xF0] = "DOWNLOAD", [0xEF] = "DOWNLOAD_NEXT", [0xEE] = "DOWNLOAD_MAX", [0xED] = "SHORT_DOWNLOAD",
        [0xEC] = "MODIFY_BITS", [0xEB] = "SET_CAL_PAGE", [0xEA] = "GET_CAL_PAGE",
        [0xE2] = "SET_DAQ_PTR", [0xE1] = "WRITE_DAQ", [0xE0] = "SET_DAQ_LIST_MODE",
        [0xDF] = "GET_DAQ_LIST_MODE", [0xDE] = "START_STOP_DAQ_LIST", [0xDD] = "START_STOP_SYNCH",
        [0xDC] = "GET_DAQ_CLOCK", [0xDB] = "READ_DAQ", [0xDA] = "GET_DAQ_PROCESSOR_INFO",
        [0xD9] = "GET_DAQ_RESOLUTION_INFO", [0xD8] = "GET_DAQ_LIST_INFO", [0xD7] = "GET_DAQ_EVENT_INFO",
        [0xD6] = "FREE_DAQ", [0xD5] = "ALLOC_DAQ", [0xD4] = "ALLOC_ODT", [0xD3] = "ALLOC_ODT_ENTRY",
        [0xD2] = "PROGRAM_START", [0xD1] = "PROGRAM_CLEAR", [0xD0] = "PROGRAM", [0xCF] = "PROGRAM_RESET",
        [0xC9] = "PROGRAM_MAX"
    };

    private static readonly IReadOnlyDictionary<byte, string> XcpErrors = new Dictionary<byte, string>
    {
        [0x00] = "ERR_CMD_SYNCH", [0x10] = "ERR_CMD_BUSY", [0x11] = "ERR_DAQ_ACTIVE", [0x12] = "ERR_PGM_ACTIVE",
        [0x20] = "ERR_CMD_UNKNOWN", [0x21] = "ERR_CMD_SYNTAX", [0x22] = "ERR_OUT_OF_RANGE",
        [0x23] = "ERR_WRITE_PROTECTED", [0x24] = "ERR_ACCESS_DENIED", [0x25] = "ERR_ACCESS_LOCKED",
        [0x26] = "ERR_PAGE_NOT_VALID", [0x27] = "ERR_MODE_NOT_VALID", [0x28] = "ERR_SEGMENT_NOT_VALID",
        [0x29] = "ERR_SEQUENCE", [0x2A] = "ERR_DAQ_CONFIG", [0x30] = "ERR_MEMORY_OVERFLOW",
        [0x31] = "ERR_GENERIC", [0x32] = "ERR_VERIFY"
    };

    private static readonly IReadOnlyDictionary<byte, string> CcpCommands = new Dictionary<byte, string>
    {
        [0x01] = "CONNECT", [0x02] = "SET_MTA", [0x03] = "DNLOAD", [0x04] = "UPLOAD", [0x05] = "TEST",
        [0x06] = "START_STOP", [0x07] = "DISCONNECT", [0x08] = "START_STOP_ALL", [0x09] = "GET_ACTIVE_CAL_PAGE",
        [0x0C] = "SET_S_STATUS", [0x0D] = "GET_S_STATUS", [0x0E] = "BUILD_CHKSUM", [0x0F] = "SHORT_UP",
        [0x10] = "CLEAR_MEMORY", [0x11] = "SELECT_CAL_PAGE", [0x12] = "GET_SEED", [0x13] = "UNLOCK",
        [0x14] = "GET_DAQ_SIZE", [0x15] = "SET_DAQ_PTR", [0x16] = "WRITE_DAQ", [0x17] = "EXCHANGE_ID",
        [0x18] = "PROGRAM", [0x19] = "MOVE", [0x1B] = "GET_CCP_VERSION", [0x20] = "DIAG_SERVICE",
        [0x21] = "ACTION_SERVICE", [0x22] = "PROGRAM_6", [0x23] = "DNLOAD_6"
    };

    private static readonly IReadOnlyDictionary<byte, string> CcpReturnCodes = new Dictionary<byte, string>
    {
        [0x00] = "acknowledge / no error", [0x01] = "DAQ processor overload", [0x10] = "command processor busy",
        [0x11] = "DAQ processor busy", [0x12] = "internal timeout", [0x18] = "key request",
        [0x19] = "session status request", [0x20] = "cold start request", [0x21] = "cal data init request",
        [0x22] = "DAQ list init request", [0x23] = "code update request", [0x30] = "unknown command",
        [0x31] = "command syntax", [0x32] = "parameter out of range", [0x33] = "access denied",
        [0x34] = "overload", [0x35] = "access locked", [0x36] = "resource/function not available"
    };

    public static string? XcpCommandName(byte code) => XcpCommands.GetValueOrDefault(code);
    public static string? XcpErrorName(byte code) => XcpErrors.GetValueOrDefault(code);
    public static string? CcpCommandName(byte code) => CcpCommands.GetValueOrDefault(code);
    public static string? CcpReturnCodeName(byte code) => CcpReturnCodes.GetValueOrDefault(code);

    // ---------------------------------------------------------------- endpoint suggestion

    public static IReadOnlyList<CalibrationEndpointCandidate> SuggestEndpoints(
        IReadOnlyList<CanFrame> frames, TimeSpan? window = null)
    {
        var limit = (window ?? DefaultTimeout).Ticks * 100L; // ns
        var counts = new Dictionary<CalibrationEndpoint, int>();
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (!IsData(f) || f.DataLength < 2) continue;
            var xcp = f.GetByte(0) == 0xFF && f.GetByte(1) == 0x00;
            var ccp = f.GetByte(0) == 0x01 && f.DataLength >= 4;
            if (!xcp && !ccp) continue;

            for (var j = i + 1; j < frames.Count; j++)
            {
                var r = frames[j];
                var dt = r.TimestampNanoseconds - f.TimestampNanoseconds;
                if (dt > limit) break;
                if (dt < 0 || !IsData(r) || r.Channel != f.Channel) continue;
                if (r.Identifier == f.Identifier && r.IsExtended == f.IsExtended) continue;
                if (r.DataLength < 3 || r.GetByte(0) != 0xFF) continue;

                CalibrationProtocol? hit = null;
                if (xcp && r.DataLength >= 8) hit = CalibrationProtocol.Xcp;
                else if (ccp && r.GetByte(1) == 0x00 && r.GetByte(2) == f.GetByte(1)) hit = CalibrationProtocol.Ccp;
                if (hit is null) continue;

                var ep = new CalibrationEndpoint(hit.Value, f.Channel, f.Identifier, f.IsExtended,
                    r.Identifier, r.IsExtended);
                counts[ep] = counts.GetValueOrDefault(ep) + 1;
                break; // first answer only
            }
        }

        return counts.Select(p => new CalibrationEndpointCandidate(p.Key, p.Value))
            .OrderByDescending(c => c.Evidence).ThenBy(c => c.Endpoint.MasterId).ToList();
    }

    // ---------------------------------------------------------------- decoding

    public static CalibrationAnalysis Decode(
        IReadOnlyList<CanFrame> frames, CalibrationEndpoint endpoint, TimeSpan? timeout = null)
    {
        var ctx = new Context(endpoint, (timeout ?? DefaultTimeout).Ticks * 100L);
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (!IsData(f) || f.Channel != endpoint.Channel || f.DataLength == 0) continue;
            var isMaster = f.Identifier == endpoint.MasterId && f.IsExtended == endpoint.MasterExtended;
            var isSlave = f.Identifier == endpoint.SlaveId && f.IsExtended == endpoint.SlaveExtended;
            if (!isMaster && !isSlave) continue;
            var data = f.GetPayload();
            if (endpoint.Protocol == CalibrationProtocol.Xcp) ctx.Xcp(i, f.TimestampNanoseconds, data, isMaster);
            else ctx.Ccp(i, f.TimestampNanoseconds, data, isMaster);
        }
        return ctx.Finish();
    }

    private static bool IsData(CanFrame f) => !f.IsError && !f.Flags.HasFlag(CanFrameFlags.Remote);

    private sealed class Pending
    {
        public int Index;
        public long Ts;
        public byte Code;
        public string Name = "";
        public byte[] Data = [];
        public List<CalibrationField> Fields = [];
    }

    private sealed class Context(CalibrationEndpoint ep, long timeoutNs)
    {
        private readonly List<CalibrationTransaction> _tx = [];
        private readonly List<CalibrationDaqSample> _daq = [];
        private readonly List<CalibrationEvent> _events = [];
        private readonly Queue<Pending> _xcpPending = new();
        private readonly Dictionary<byte, Pending> _ccpPending = [];
        private bool? _bigEndian;
        private int _unmatched, _stim;

        // ---- XCP
        public void Xcp(int idx, long ts, byte[] d, bool isMaster)
        {
            ExpireXcp(ts);
            if (isMaster)
            {
                if (d[0] < 0xC0) { _stim++; return; }
                var p = new Pending
                {
                    Index = idx, Ts = ts, Code = d[0], Data = d,
                    Name = XcpCommandName(d[0]) ?? $"UNKNOWN_0x{d[0]:X2}"
                };
                DecodeXcpRequest(p);
                _xcpPending.Enqueue(p);
                return;
            }
            switch (d[0])
            {
                case 0xFF:
                case 0xFE:
                    if (_xcpPending.Count == 0) { _unmatched++; return; }
                    var cmd = _xcpPending.Dequeue();
                    var ok = d[0] == 0xFF;
                    var resp = new List<CalibrationField>();
                    byte? err = null;
                    string? errName = null;
                    if (ok) DecodeXcpResponse(cmd, d, resp);
                    else
                    {
                        err = d.Length > 1 ? d[1] : (byte)0;
                        errName = XcpErrorName(err.Value) ?? $"ERR_0x{err:X2}";
                        resp.Add(new("error", errName));
                    }
                    _tx.Add(Make(cmd, idx, ts, resp, ok ? CalibrationResult.Positive : CalibrationResult.Error, err, errName));
                    break;
                case 0xFD:
                    _events.Add(new(idx, ts, "EV", d.Length > 1 ? d[1] : (byte)0));
                    break;
                case 0xFC:
                    _events.Add(new(idx, ts, "SERV", d.Length > 1 ? d[1] : (byte)0));
                    break;
                default:
                    if (d[0] <= 0xFB) _daq.Add(new(idx, ts, d[0], d));
                    break;
            }
        }

        private void ExpireXcp(long now)
        {
            while (_xcpPending.Count > 0 && now - _xcpPending.Peek().Ts > timeoutNs)
                _tx.Add(Make(_xcpPending.Dequeue(), null, 0, [], CalibrationResult.Timeout, null, null));
        }

        private bool Big => _bigEndian == true;

        private uint U32(byte[] d, int o) => Big
            ? (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3])
            : (uint)(d[o + 3] << 24 | d[o + 2] << 16 | d[o + 1] << 8 | d[o]);

        private string OrderNote() =>
            _bigEndian is null ? "assumed little (no CONNECT seen)" : Big ? "big" : "little";

        private void DecodeXcpRequest(Pending p)
        {
            var d = p.Data;
            var f = p.Fields;
            switch (p.Code)
            {
                case 0xFF when d.Length >= 2:
                    f.Add(new("mode", d[1] == 0 ? "normal" : d[1] == 1 ? "user-defined" : Hex(d[1])));
                    break;
                case 0xF6 when d.Length >= 8:
                    f.Add(new("addressExtension", Hex(d[3])));
                    f.Add(new("address", $"0x{U32(d, 4):X8}"));
                    f.Add(new("byteOrder", OrderNote()));
                    break;
                case 0xF4 when d.Length >= 8:
                    f.Add(new("size", d[1].ToString(CultureInfo.InvariantCulture)));
                    f.Add(new("addressExtension", Hex(d[3])));
                    f.Add(new("address", $"0x{U32(d, 4):X8}"));
                    f.Add(new("byteOrder", OrderNote()));
                    break;
                case 0xF5 when d.Length >= 2:
                    f.Add(new("size", d[1].ToString(CultureInfo.InvariantCulture)));
                    break;
            }
        }

        private void DecodeXcpResponse(Pending cmd, byte[] d, List<CalibrationField> f)
        {
            if (cmd.Code == 0xFF && d.Length >= 8)
            {
                var res = d[1];
                var mode = d[2];
                _bigEndian = (mode & 1) != 0;
                var names = new List<string>();
                if ((res & 0x01) != 0) names.Add("CAL_PAG");
                if ((res & 0x04) != 0) names.Add("DAQ");
                if ((res & 0x08) != 0) names.Add("STIM");
                if ((res & 0x10) != 0) names.Add("PGM");
                f.Add(new("resource", names.Count == 0 ? "none" : string.Join("|", names)));
                f.Add(new("byteOrder", Big ? "big" : "little"));
                f.Add(new("addressGranularity", ((mode >> 1) & 3) switch { 0 => "byte", 1 => "word", 2 => "dword", _ => "reserved" }));
                f.Add(new("slaveBlockMode", ((mode & 0x40) != 0).ToString()));
                f.Add(new("optional", ((mode & 0x80) != 0).ToString()));
                f.Add(new("maxCto", d[3].ToString(CultureInfo.InvariantCulture)));
                var maxDto = Big ? d[4] << 8 | d[5] : d[5] << 8 | d[4];
                f.Add(new("maxDto", maxDto.ToString(CultureInfo.InvariantCulture)));
                f.Add(new("protocolLayerVersion", d[6].ToString(CultureInfo.InvariantCulture)));
                f.Add(new("transportLayerVersion", d[7].ToString(CultureInfo.InvariantCulture)));
            }
            else if (cmd.Code == 0xFD && d.Length >= 6)
            {
                var s = d[1];
                var st = new List<string>();
                if ((s & 0x01) != 0) st.Add("STORE_CAL_REQ");
                if ((s & 0x04) != 0) st.Add("STORE_DAQ_REQ");
                if ((s & 0x08) != 0) st.Add("CLEAR_DAQ_REQ");
                if ((s & 0x40) != 0) st.Add("DAQ_RUNNING");
                if ((s & 0x80) != 0) st.Add("RESUME");
                f.Add(new("sessionStatus", st.Count == 0 ? "none" : string.Join("|", st)));
                f.Add(new("resourceProtection", Hex(d[2])));
                var cfg = Big ? d[4] << 8 | d[5] : d[5] << 8 | d[4];
                f.Add(new("sessionConfigurationId", $"0x{cfg:X4}"));
            }
            else if (d.Length > 1)
            {
                f.Add(new("data", Convert.ToHexString(d, 1, d.Length - 1)));
            }
        }

        // ---- CCP
        public void Ccp(int idx, long ts, byte[] d, bool isMaster)
        {
            ExpireCcp(ts);
            if (isMaster)
            {
                if (d.Length < 2) return;
                var p = new Pending
                {
                    Index = idx, Ts = ts, Code = d[0], Data = d,
                    Name = CcpCommandName(d[0]) ?? $"UNKNOWN_0x{d[0]:X2}"
                };
                DecodeCcpRequest(p);
                if (_ccpPending.TryGetValue(d[1], out var old))
                    _tx.Add(Make(old, null, 0, [], CalibrationResult.Timeout, null, null));
                _ccpPending[d[1]] = p;
                return;
            }
            if (d[0] == 0xFF)
            {
                if (d.Length < 3 || !_ccpPending.Remove(d[2], out var cmd)) { _unmatched++; return; }
                var rc = d[1];
                var name = CcpReturnCodeName(rc) ?? $"unknown 0x{rc:X2}";
                var resp = new List<CalibrationField> { new("returnCode", name) };
                if (d.Length > 3) resp.Add(new("data", Convert.ToHexString(d, 3, d.Length - 3)));
                _tx.Add(Make(cmd, idx, ts, resp, rc == 0 ? CalibrationResult.Positive : CalibrationResult.Error,
                    rc == 0 ? null : rc, rc == 0 ? null : name));
            }
            else if (d[0] == 0xFE)
                _events.Add(new(idx, ts, "EVENT", d.Length > 1 ? d[1] : (byte)0));
            else
                _daq.Add(new(idx, ts, d[0], d));
        }

        private void ExpireCcp(long now)
        {
            foreach (var kv in _ccpPending.Where(k => now - k.Value.Ts > timeoutNs).ToList())
            {
                _ccpPending.Remove(kv.Key);
                _tx.Add(Make(kv.Value, null, 0, [], CalibrationResult.Timeout, null, null));
            }
        }

        private static void DecodeCcpRequest(Pending p)
        {
            var d = p.Data;
            var f = p.Fields;
            f.Add(new("counter", d[1].ToString(CultureInfo.InvariantCulture)));
            switch (p.Code)
            {
                case 0x01 when d.Length >= 4:
                    f.Add(new("stationAddress", $"0x{d[2] | d[3] << 8:X4}"));
                    break;
                case 0x02 when d.Length >= 8:
                    f.Add(new("mtaNumber", d[2].ToString(CultureInfo.InvariantCulture)));
                    f.Add(new("addressExtension", Hex(d[3])));
                    f.Add(new("addressBytes", Convert.ToHexString(d, 4, 4)));
                    break;
                case 0x03 when d.Length >= 3:
                    f.Add(new("size", d[2].ToString(CultureInfo.InvariantCulture)));
                    if (d.Length > 3) f.Add(new("data", Convert.ToHexString(d, 3, d.Length - 3)));
                    break;
            }
        }

        // ---- shared
        private CalibrationTransaction Make(Pending cmd, int? respIdx, long respTs, List<CalibrationField> resp,
            CalibrationResult result, byte? err, string? errName) =>
            new(ep.Protocol, cmd.Index, cmd.Ts, respIdx, respIdx is null ? null : respTs, cmd.Code, cmd.Name,
                cmd.Fields, resp, result, err, errName,
                respIdx is null ? null : (respTs - cmd.Ts) / 1_000_000.0);

        public CalibrationAnalysis Finish()
        {
            foreach (var p in _xcpPending) _tx.Add(Make(p, null, 0, [], CalibrationResult.Timeout, null, null));
            foreach (var p in _ccpPending.Values) _tx.Add(Make(p, null, 0, [], CalibrationResult.Timeout, null, null));
            _xcpPending.Clear();
            _ccpPending.Clear();
            _tx.Sort((a, b) => a.CommandFrameIndex.CompareTo(b.CommandFrameIndex));

            var commands = _tx.GroupBy(t => t.CommandName).ToDictionary(g => g.Key, g => g.Count());
            var errors = _tx.Where(t => t.ErrorName is not null).GroupBy(t => t.ErrorName!)
                .ToDictionary(g => g.Key, g => g.Count());
            var pids = _daq.GroupBy(s => s.Pid).ToDictionary(g => g.Key, g => g.Count());
            var summary = new CalibrationSummary(_tx.Count, commands, errors, pids,
                _tx.Count(t => t.Result == CalibrationResult.Timeout), _unmatched, _events.Count, _stim);
            return new CalibrationAnalysis(ep, _tx, _daq, _events, summary, _bigEndian);
        }
    }

    private static string Hex(byte b) => $"0x{b:X2}";
}
