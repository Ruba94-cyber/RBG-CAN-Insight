using System.Globalization;

namespace RbgCanInsight.Analysis.Xcp;

/// <summary>Names from ASAM MCD-1 XCP (command/error/event codes) and CCP 2.1. Unknown codes return null, never a guess.</summary>
public static class XcpCatalog
{
    private static readonly Dictionary<int, string> XcpCommands = new()
    {
        [0xFF] = "CONNECT", [0xFE] = "DISCONNECT", [0xFD] = "GET_STATUS", [0xFC] = "SYNCH", [0xFB] = "GET_COMM_MODE_INFO",
        [0xFA] = "GET_ID", [0xF9] = "SET_REQUEST", [0xF8] = "GET_SEED", [0xF7] = "UNLOCK", [0xF6] = "SET_MTA",
        [0xF5] = "UPLOAD", [0xF4] = "SHORT_UPLOAD", [0xF3] = "BUILD_CHECKSUM", [0xF2] = "TRANSPORT_LAYER_CMD", [0xF1] = "USER_CMD",
        [0xF0] = "DOWNLOAD", [0xEF] = "DOWNLOAD_NEXT", [0xEE] = "DOWNLOAD_MAX", [0xED] = "SHORT_DOWNLOAD", [0xEC] = "MODIFY_BITS",
        [0xEB] = "SET_CAL_PAGE", [0xEA] = "GET_CAL_PAGE", [0xE9] = "GET_PAG_PROCESSOR_INFO", [0xE8] = "GET_SEGMENT_INFO",
        [0xE7] = "GET_PAGE_INFO", [0xE6] = "SET_SEGMENT_MODE", [0xE5] = "GET_SEGMENT_MODE", [0xE4] = "COPY_CAL_PAGE",
        [0xE3] = "CLEAR_DAQ_LIST", [0xE2] = "SET_DAQ_PTR", [0xE1] = "WRITE_DAQ", [0xE0] = "SET_DAQ_LIST_MODE",
        [0xDF] = "GET_DAQ_LIST_MODE", [0xDE] = "START_STOP_DAQ_LIST", [0xDD] = "START_STOP_SYNCH", [0xDC] = "GET_DAQ_CLOCK",
        [0xDB] = "READ_DAQ", [0xDA] = "GET_DAQ_PROCESSOR_INFO", [0xD9] = "GET_DAQ_RESOLUTION_INFO", [0xD8] = "GET_DAQ_LIST_INFO",
        [0xD7] = "GET_DAQ_EVENT_INFO", [0xD6] = "FREE_DAQ", [0xD5] = "ALLOC_DAQ", [0xD4] = "ALLOC_ODT", [0xD3] = "ALLOC_ODT_ENTRY",
        [0xD2] = "PROGRAM_START", [0xD1] = "PROGRAM_CLEAR", [0xD0] = "PROGRAM", [0xCF] = "PROGRAM_RESET",
        [0xCE] = "GET_PGM_PROCESSOR_INFO", [0xCD] = "GET_SECTOR_INFO", [0xCC] = "PROGRAM_PREPARE", [0xCB] = "PROGRAM_FORMAT",
        [0xCA] = "PROGRAM_NEXT", [0xC9] = "PROGRAM_MAX", [0xC8] = "PROGRAM_VERIFY", [0xC7] = "WRITE_DAQ_MULTIPLE",
        [0xC6] = "TIME_CORRELATION_PROPERTIES", [0xC5] = "DTO_CTR_PROPERTIES",
    };

    private static readonly Dictionary<int, string> XcpErrors = new()
    {
        [0x00] = "ERR_CMD_SYNCH", [0x10] = "ERR_CMD_BUSY", [0x11] = "ERR_DAQ_ACTIVE", [0x12] = "ERR_PGM_ACTIVE",
        [0x20] = "ERR_CMD_UNKNOWN", [0x21] = "ERR_CMD_SYNTAX", [0x22] = "ERR_OUT_OF_RANGE", [0x23] = "ERR_WRITE_PROTECTED",
        [0x24] = "ERR_ACCESS_DENIED", [0x25] = "ERR_ACCESS_LOCKED", [0x26] = "ERR_PAGE_NOT_VALID", [0x27] = "ERR_MODE_NOT_VALID",
        [0x28] = "ERR_SEGMENT_NOT_VALID", [0x29] = "ERR_SEQUENCE", [0x2A] = "ERR_DAQ_CONFIG", [0x30] = "ERR_MEMORY_OVERFLOW",
        [0x31] = "ERR_GENERIC", [0x32] = "ERR_VERIFY", [0x33] = "ERR_RESOURCE_TEMPORARY_NOT_ACCESSIBLE",
    };

    private static readonly Dictionary<int, string> XcpEvents = new()
    {
        [0x00] = "EV_RESUME_MODE", [0x01] = "EV_CLEAR_DAQ", [0x02] = "EV_STORE_DAQ", [0x03] = "EV_STORE_CAL",
        [0x05] = "EV_CMD_PENDING", [0x06] = "EV_DAQ_OVERLOAD", [0x07] = "EV_SESSION_TERMINATED", [0x08] = "EV_TIME_SYNC",
        [0x09] = "EV_STIM_TIMEOUT", [0x0A] = "EV_SLEEP", [0x0B] = "EV_WAKE_UP", [0xFE] = "EV_USER", [0xFF] = "EV_TRANSPORT",
    };

    private static readonly Dictionary<int, string> CcpCommands = new()
    {
        [0x01] = "CONNECT", [0x02] = "SET_MTA", [0x03] = "DNLOAD", [0x04] = "UPLOAD", [0x05] = "TEST", [0x06] = "START_STOP",
        [0x07] = "DISCONNECT", [0x08] = "START_STOP_ALL", [0x09] = "GET_ACTIVE_CAL_PAGE", [0x0C] = "SET_S_STATUS",
        [0x0D] = "GET_S_STATUS", [0x0E] = "BUILD_CHKSUM", [0x0F] = "SHORT_UP", [0x10] = "CLEAR_MEMORY", [0x11] = "SELECT_CAL_PAGE",
        [0x12] = "GET_SEED", [0x13] = "UNLOCK", [0x14] = "GET_DAQ_SIZE", [0x15] = "SET_DAQ_PTR", [0x16] = "WRITE_DAQ",
        [0x17] = "EXCHANGE_ID", [0x18] = "PROGRAM", [0x19] = "MOVE", [0x1B] = "GET_CCP_VERSION", [0x20] = "DIAG_SERVICE",
        [0x21] = "ACTION_SERVICE", [0x22] = "PROGRAM_6", [0x23] = "DNLOAD_6",
    };

    private static readonly Dictionary<int, string> CcpErrors = new()
    {
        [0x00] = "Acknowledge / no error", [0x01] = "DAQ processor overload", [0x10] = "Command processor busy",
        [0x11] = "DAQ processor busy", [0x12] = "Internal timeout", [0x18] = "Key request", [0x19] = "Session status request",
        [0x20] = "Cold start request", [0x21] = "Cal. data init request", [0x22] = "DAQ list init request",
        [0x23] = "Code update request", [0x30] = "Unknown command", [0x31] = "Command syntax", [0x32] = "Parameter out of range",
        [0x33] = "Access denied", [0x34] = "Overload", [0x35] = "Access locked", [0x36] = "Resource/function not available",
    };

    public static string? XcpCommandName(int code) => XcpCommands.GetValueOrDefault(code);
    public static string? XcpErrorName(int code) => XcpErrors.GetValueOrDefault(code);
    public static string? XcpEventName(int code) => XcpEvents.GetValueOrDefault(code);
    public static string? CcpCommandName(int code) => CcpCommands.GetValueOrDefault(code);
    public static string? CcpErrorName(int code) => CcpErrors.GetValueOrDefault(code);

    public static string Hex(ReadOnlySpan<byte> bytes, int max = 64)
    {
        var n = Math.Min(bytes.Length, max);
        var parts = new string[n];
        for (var i = 0; i < n; i++) parts[i] = bytes[i].ToString("X2", CultureInfo.InvariantCulture);
        return string.Join(' ', parts) + (bytes.Length > max ? " ..." : "");
    }
}
