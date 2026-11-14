using RbgCanInsight.Analysis.Xcp;
using RbgCanInsight.Analysis.Xcp.A2l;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class XcpA2lTests
{
    internal const string SampleA2l = """
        /* hand-written test file */
        ASAP2_VERSION 1 71
        /begin PROJECT P ""
          /begin MODULE M ""
            /begin A2ML
              block "IF_DATA" taggedunion if_data { "XCP" struct { uint; }; };
            /end A2ML
            /begin MOD_COMMON "" BYTE_ORDER MSB_LAST ALIGNMENT_BYTE 1 /end MOD_COMMON
            /begin IF_DATA XCPplus
              /begin DAQ
                STATIC 0 1 0 OPTIMISATION_TYPE_DEFAULT ADDRESS_EXTENSION_FREE IDENTIFICATION_FIELD_TYPE_ABSOLUTE
                /begin EVENT "10ms" "10ms" 0 DAQ 0xFF 10 6 0 /end EVENT
              /end DAQ
              /begin XCP_ON_CAN 0x0101
                CAN_ID_BROADCAST 0x7DF CAN_ID_MASTER 0x7E0 CAN_ID_SLAVE 0x7E1 BAUDRATE 500000
                /begin DAQ_LIST_CAN_ID 0 FIXED 0x7E2 /end DAQ_LIST_CAN_ID
              /end XCP_ON_CAN
            /end IF_DATA
            /begin COMPU_METHOD CM_Rpm "quarter rpm" LINEAR "%6.1" "rpm" COEFFS_LINEAR 0.25 0 /end COMPU_METHOD
            /begin COMPU_METHOD CM_Temp "offset" RAT_FUNC "%4.0" "degC" COEFFS 0 1 40 0 0 1 /end COMPU_METHOD
            /begin COMPU_METHOD CM_State "state" TAB_VERB "%2.0" "" COMPU_TAB_REF VT_State /end COMPU_METHOD
            /begin COMPU_VTAB VT_State "states" TAB_VERB 2 0 "Off" 1 "On" DEFAULT_VALUE "Unknown" /end COMPU_VTAB
            /begin RECORD_LAYOUT RL_F32 FNC_VALUES 1 FLOAT32_IEEE COLUMN_DIR DIRECT /end RECORD_LAYOUT
            /begin MEASUREMENT EngineSpeed "engine speed" UWORD CM_Rpm 1 0.5 0 16383
              ECU_ADDRESS 0x1000 PHYS_UNIT "rpm"
            /end MEASUREMENT
            /begin MEASUREMENT CoolantTemp "coolant" SBYTE CM_Temp 1 1 -40 215 ECU_ADDRESS 0x1002 /end MEASUREMENT
            /begin MEASUREMENT RelayState "relay" UBYTE CM_State 1 1 0 1 ECU_ADDRESS 0x1003 /end MEASUREMENT
            /begin MEASUREMENT Table "array" UBYTE NO_COMPU_METHOD 1 1 0 255 ECU_ADDRESS 0x1100 ARRAY_SIZE 16 /end MEASUREMENT
            /begin CHARACTERISTIC Gain "gain" VALUE 0x2000 RL_F32 0 NO_COMPU_METHOD -10 10 /end CHARACTERISTIC
          /end MODULE
        /end PROJECT
        """;

    [Fact]
    public void ReaderExtractsObjectsAndXcpOnCan()
    {
        var a = A2lReader.Read(SampleA2l, "test.a2l");
        Assert.Equal("P", a.ProjectName);
        Assert.Equal("M", a.ModuleName);
        Assert.Equal(false, a.BigEndian);
        Assert.Equal(4, a.MeasurementCount);
        Assert.Equal(1, a.CharacteristicCount);
        var gain = a.Find("Gain")!;
        Assert.Equal(0x2000UL, gain.Address);
        Assert.Equal(A2lDataType.Float32, gain.DataType);
        Assert.Equal(4, gain.SizeBytes);
        Assert.Equal(16, a.Find("Table")!.SizeBytes);
        var can = Assert.Single(a.XcpOnCan);
        Assert.Equal(0x7E0u, can.MasterId);
        Assert.Equal(0x7E1u, can.SlaveId);
        Assert.Equal(500000, can.Baudrate);
        var ep = can.ToEndpoint()!;
        Assert.Equal(new uint[] { 0x7E2 }, ep.DaqIds);
        var ev = Assert.Single(a.XcpEvents);
        Assert.Equal("10ms", ev.Name);
        Assert.Equal(0, ev.Channel);
    }

    [Fact]
    public void ResolveHandlesExactContainmentAndMiss()
    {
        var a = A2lReader.Read(SampleA2l);
        Assert.Equal("EngineSpeed", a.Resolve(0x1000)!.Text);
        Assert.Equal("EngineSpeed+1", a.Resolve(0x1001)!.Text);
        Assert.Equal("Table+5", a.Resolve(0x1105)!.Text);
        Assert.Null(a.Resolve(0x1110));
        Assert.Null(a.Resolve(0x1000, 1)); // other address extension
    }

    [Fact]
    public void ConversionsAreExactOrRefused()
    {
        var a = A2lReader.Read(SampleA2l);
        Assert.Equal("2000 rpm", a.FormatValue(a.Find("EngineSpeed")!, [0x40, 0x1F], false, out _));
        Assert.Equal("-35 degC", a.FormatValue(a.Find("CoolantTemp")!, [5], false, out _));
        Assert.Equal("On", a.FormatValue(a.Find("RelayState")!, [1], false, out _));
        Assert.Equal("Unknown", a.FormatValue(a.Find("RelayState")!, [9], false, out _));
        Assert.Equal("1.5", a.FormatValue(a.Find("Gain")!, [0, 0, 0xC0, 0x3F], false, out _));
    }

    [Fact]
    public void QuadraticRationalFunctionIsNotGuessed()
    {
        var text = SampleA2l.Replace("COEFFS 0 1 40 0 0 1", "COEFFS 1 1 40 0 0 1");
        var a = A2lReader.Read(text);
        var s = a.FormatValue(a.Find("CoolantTemp")!, [5], false, out var note);
        Assert.Contains("(raw)", s);
        Assert.Contains("quadratic", note);
    }

    [Fact]
    public void MalformedInputIsReportedNotThrownAsCrash()
    {
        Assert.Throws<A2lFormatException>(() => A2lReader.Read("hello"));
        var a = A2lReader.Read(SampleA2l.Replace("/end PROJECT", ""));
        Assert.Contains(a.Warnings, w => w.Contains("not closed"));
    }

    private static CanFrame F(uint id, long ms, params byte[] d) => new(0, id, d, ms * 1_000_000);

    private static List<CanFrame> Trace() =>
    [
        F(0x7E0, 0, 0xFF, 0x00), F(0x7E1, 1, 0xFF, 0x05, 0x00, 0x08, 0x08, 0x00, 0x01, 0x01),
        F(0x7E0, 10, 0xF6, 0, 0, 0, 0x00, 0x10, 0, 0), F(0x7E1, 11, 0xFF),
        F(0x7E0, 12, 0xF5, 0x02), F(0x7E1, 13, 0xFF, 0x40, 0x1F),
        F(0x7E0, 14, 0xF6, 0, 0, 0, 0x00, 0x20, 0, 0), F(0x7E1, 15, 0xFF),
        F(0x7E0, 16, 0xF0, 0x04, 0x00, 0x00, 0xC0, 0x3F), F(0x7E1, 17, 0xFF),
        F(0x7E0, 20, 0xD6), F(0x7E1, 21, 0xFF),
        F(0x7E0, 22, 0xD5, 0, 0x01, 0x00), F(0x7E1, 23, 0xFF),
        F(0x7E0, 24, 0xD4, 0, 0, 0, 0x01), F(0x7E1, 25, 0xFF),
        F(0x7E0, 26, 0xD3, 0, 0, 0, 0, 0x02), F(0x7E1, 27, 0xFF),
        F(0x7E0, 28, 0xE2, 0, 0, 0, 0, 0), F(0x7E1, 29, 0xFF),
        F(0x7E0, 30, 0xE1, 0xFF, 0x02, 0, 0x00, 0x10, 0, 0), F(0x7E1, 31, 0xFF),
        F(0x7E0, 32, 0xE1, 0xFF, 0x01, 0, 0x02, 0x10, 0, 0), F(0x7E1, 33, 0xFF),
        F(0x7E0, 34, 0xE0, 0, 0, 0, 0, 0, 0x01, 0x00), F(0x7E1, 35, 0xFF),
        F(0x7E0, 36, 0xDE, 0x02, 0, 0), F(0x7E1, 37, 0xFF, 0x04),
        F(0x7E0, 38, 0xDD, 0x01), F(0x7E1, 39, 0xFF),
        F(0x7E2, 50, 0x04, 0x40, 0x1F, 0x05), F(0x7E2, 60, 0x04, 0xA0, 0x0F, 0x06),
    ];

    private static readonly XcpEndpoint Ep = new(XcpProtocol.Xcp, 0x7E0, 0x7E1, DaqIds: [0x7E2]);

    [Fact]
    public void MtaUploadDownloadResolveToVariableNames()
    {
        var a = A2lReader.Read(SampleA2l);
        var an = XcpAnalyzer.Analyze(Trace(), Ep, new XcpOptions(A2l: a));
        var setMta = an.Transactions.Where(t => t.CommandName == "SET_MTA").ToList();
        Assert.Contains("A2L: EngineSpeed", setMta[0].RequestDecoded);
        Assert.Contains("A2L: Gain", setMta[1].RequestDecoded);
        var up = an.Transactions.Single(t => t.CommandName == "UPLOAD");
        Assert.Contains("EngineSpeed", up.RequestDecoded);
        Assert.Contains("EngineSpeed = 2000 rpm", up.ResponseDecoded);
        var down = an.Transactions.Single(t => t.CommandName == "DOWNLOAD");
        Assert.Contains("writes Gain = 1.5", down.RequestDecoded);
    }

    [Fact]
    public void DaqPacketsDecodeWithTraceConfigurationAndA2l()
    {
        var a = A2lReader.Read(SampleA2l);
        var an = XcpAnalyzer.Analyze(Trace(), Ep, new XcpOptions(A2l: a));
        Assert.Equal(2, an.DaqDecoded!.Count);
        var p0 = an.DaqDecoded[0];
        Assert.Equal("10ms", p0.EventName);
        Assert.Equal("EngineSpeed", p0.Values[0].Name);
        Assert.Equal("2000 rpm", p0.Values[0].Text);
        Assert.Equal("-35 degC", p0.Values[1].Text);
        Assert.Equal("1000 rpm", an.DaqDecoded[1].Values[0].Text);
        Assert.Contains(an.Transactions, t => t.CommandName == "SET_DAQ_LIST_MODE" && t.RequestDecoded.Contains("event '10ms'"));
    }

    [Fact]
    public void WithoutA2lNothingIsAnnotated()
    {
        var an = XcpAnalyzer.Analyze(Trace(), Ep);
        Assert.Null(an.DaqDecoded);
        Assert.DoesNotContain("A2L", an.Transactions.First(t => t.CommandName == "SET_MTA").RequestDecoded);
    }

    [Fact]
    public void ReportUsesA2lIdentifiersAndAddsDaqSignalRows()
    {
        var a = A2lReader.Read(SampleA2l);
        var rep = XcpReportRows.Build(Trace(), null, new XcpOptions(A2l: a));
        Assert.Contains(rep.Rows, r => r[3] == "A2L" && r[4].Contains("0x7E0"));
        Assert.Contains(rep.Rows, r => r[3] == "DAQ signal" && r[4] == "EngineSpeed" && r[6].StartsWith("2000 rpm"));
        // Report without A2L has no A2L rows.
        Assert.DoesNotContain(XcpReportRows.Build(Trace()).Rows, r => r[3] is "A2L" or "DAQ signal");
    }

    [Fact]
    public void PublicAsap2DemoSampleParsesWhenPresent()
    {
        var dir = AppContext.BaseDirectory;
        string? path = null;
        for (var d = new DirectoryInfo(dir); d is not null && path is null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "artifacts", "a2l_samples", "ASAP2_Demo_V161.a2l");
            if (File.Exists(c)) path = c;
        }
        if (path is null) return; // public sample is kept local under artifacts/, never committed
        var a = A2lReader.ReadFile(path);
        Assert.Contains(a.Warnings, w => w.Contains("no ECU address")); // the demo has no ECU_ADDRESS on measurements
        Assert.True(a.CharacteristicCount >= 40);
        Assert.NotEmpty(a.CompuMethods);
    }
}
