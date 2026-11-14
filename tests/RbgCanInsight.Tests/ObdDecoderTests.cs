using System.Text;
using RbgCanInsight.Diagnostics.Obd;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class ObdDecoderTests
{
    private static long _ts;

    private static CanFrame Raw(uint id, byte[] data, bool ext = false) =>
        new(0, id, data, _ts += 1_000_000, ext ? CanFrameFlags.Extended : CanFrameFlags.None);

    /// <summary>ISO-TP single frame padded to 8 bytes with 0x55.</summary>
    private static CanFrame Sf(uint id, params byte[] payload)
    {
        var data = new byte[8];
        Array.Fill(data, (byte)0x55);
        data[0] = (byte)payload.Length;
        payload.CopyTo(data, 1);
        return Raw(id, data);
    }

    private static ObdTransaction Single(params CanFrame[] frames)
    {
        var tx = Assert.Single(ObdDecoder.Decode(frames));
        return tx;
    }

    [Fact]
    public void EngineSpeedUsesQuarterRpmFormula()
    {
        var tx = Single(Sf(0x7DF, 0x01, 0x0C), Sf(0x7E8, 0x41, 0x0C, 0x1A, 0xF8));
        Assert.Equal(ObdOutcome.Positive, tx.Outcome);
        Assert.Equal((byte)1, tx.Mode);
        Assert.Equal((byte?)0x0C, tx.Pid);
        var response = Assert.Single(tx.Responses);
        Assert.Equal(0x7E8u, response.ResponderId);
        var value = Assert.Single(response.Values);
        Assert.Equal((0x1A * 256 + 0xF8) / 4.0, value.NumericValue);
        Assert.Equal("rpm", value.Unit);
        Assert.Equal(new byte[] { 0x1A, 0xF8 }, value.Raw);
        Assert.Equal(new[] { 0, 1 }, tx.FrameIndices);
        Assert.NotNull(response.LatencyMilliseconds);
    }

    [Fact]
    public void CoolantTemperatureSubtracts40()
    {
        var tx = Single(Sf(0x7E0, 0x01, 0x05), Sf(0x7E8, 0x41, 0x05, 0x7B));
        var value = Assert.Single(Assert.Single(tx.Responses).Values);
        Assert.Equal(83, value.NumericValue);
        Assert.Equal("°C", value.Unit);
        Assert.False(tx.Request!.IsFunctional);
    }

    [Fact]
    public void SupportedPidBitmapListsEverySetBit()
    {
        // BE 3F A8 13: 1011 1110 0011 1111 1010 1000 0001 0011
        var tx = Single(Sf(0x7DF, 0x01, 0x00), Sf(0x7E8, 0x41, 0x00, 0xBE, 0x3F, 0xA8, 0x13));
        var value = Assert.Single(Assert.Single(tx.Responses).Values);
        Assert.Equal(
            new[] { 0x01, 0x03, 0x04, 0x05, 0x06, 0x07, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11,
                    0x13, 0x15, 0x1C, 0x1F, 0x20 },
            value.SupportedPids);
    }

    [Fact]
    public void MonitorStatusReportsMilAndDtcCount()
    {
        var tx = Single(Sf(0x7DF, 0x01, 0x01), Sf(0x7E8, 0x41, 0x01, 0x82, 0x07, 0xE5, 0x00));
        var status = Assert.Single(Assert.Single(tx.Responses).Values).Monitor!;
        Assert.True(status.MilOn);
        Assert.Equal(2, status.DtcCount);
        Assert.False(status.CompressionIgnition);
    }

    [Fact]
    public void MultiFrameVinIsReassembledViaIsoTp()
    {
        const string vin = "TESTVIN0000000001";
        var v = Encoding.ASCII.GetBytes(vin);
        var frames = new[]
        {
            Sf(0x7DF, 0x09, 0x02),
            Raw(0x7E8, [0x10, 0x14, 0x49, 0x02, 0x01, v[0], v[1], v[2]]),
            Raw(0x7E0, [0x30, 0x00, 0x00, 0x55, 0x55, 0x55, 0x55, 0x55]),
            Raw(0x7E8, [0x21, v[3], v[4], v[5], v[6], v[7], v[8], v[9]]),
            Raw(0x7E8, [0x22, v[10], v[11], v[12], v[13], v[14], v[15], v[16]])
        };
        var tx = Single(frames);
        var response = Assert.Single(tx.Responses);
        Assert.Equal(vin, response.Vin);
        Assert.Equal(new[] { 0, 1, 3, 4 }, tx.FrameIndices);
        Assert.Equal(ObdOutcome.Positive, tx.Outcome);
    }

    [Fact]
    public void StoredDtcListDecodesP0133()
    {
        // count=2: P0133, U0100
        var tx = Single(Sf(0x7DF, 0x03), Sf(0x7E8, 0x43, 0x02, 0x01, 0x33, 0xC1, 0x00));
        var dtcs = Assert.Single(tx.Responses).Dtcs;
        Assert.Equal(new[] { "P0133", "U0100" }, dtcs.Select(d => d.Code));
        Assert.Equal("", Assert.Single(tx.Responses).Issue);
    }

    [Fact]
    public void DtcLetterMappingCoversAllSystems()
    {
        Assert.Equal("P0133", ObdDecoder.DtcText(0x01, 0x33));
        Assert.Equal("C1234", ObdDecoder.DtcText(0x52, 0x34));
        Assert.Equal("B2ABC", ObdDecoder.DtcText(0xAA, 0xBC));
        Assert.Equal("U3FFF", ObdDecoder.DtcText(0xFF, 0xFF));
    }

    [Fact]
    public void NegativeResponseCarriesNrcName()
    {
        var tx = Single(Sf(0x7E0, 0x01, 0xFF), Sf(0x7E8, 0x7F, 0x01, 0x12));
        Assert.Equal(ObdOutcome.Negative, tx.Outcome);
        var response = Assert.Single(tx.Responses);
        Assert.True(response.IsNegative);
        Assert.Equal((byte?)0x12, response.NegativeResponseCode);
        Assert.Equal("subFunctionNotSupported", response.NegativeResponseName);
    }

    [Fact]
    public void FunctionalRequestIsAnsweredByTwoEcus()
    {
        var frames = new[]
        {
            Sf(0x7DF, 0x01, 0x0D),
            Sf(0x7E8, 0x41, 0x0D, 0x32),
            Sf(0x7E9, 0x41, 0x0D, 0x31)
        };
        var tx = Single(frames);
        Assert.True(tx.Request!.IsFunctional);
        Assert.Equal(new[] { 0x7E8u, 0x7E9u }, tx.Responses.Select(r => r.ResponderId));
        Assert.Equal(new double?[] { 50, 49 }, tx.Responses.Select(r => r.Values[0].NumericValue));
        Assert.Equal(new[] { 0, 1, 2 }, tx.FrameIndices);
    }

    [Fact]
    public void UnknownPidStaysRawAndIsNotInterpreted()
    {
        var tx = Single(Sf(0x7DF, 0x01, 0x6B), Sf(0x7E8, 0x41, 0x6B, 0x12, 0x34, 0x56));
        var value = Assert.Single(Assert.Single(tx.Responses).Values);
        Assert.False(value.IsKnown);
        Assert.Null(value.NumericValue);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56 }, value.Raw);
    }

    [Fact]
    public void ClearDtcsPositiveResponseAndExtendedAddressing()
    {
        var request = Raw(0x18DA10F1, [0x01, 0x04, 0x55, 0x55, 0x55, 0x55, 0x55, 0x55], ext: true);
        var response = Raw(0x18DAF110, [0x01, 0x44, 0x55, 0x55, 0x55, 0x55, 0x55, 0x55], ext: true);
        var tx = Single(request, response);
        Assert.Equal((byte)4, tx.Mode);
        Assert.Equal(ObdOutcome.Positive, tx.Outcome);
        Assert.Equal(0x18DAF110u, tx.Responses[0].ResponderId);
    }

    [Fact]
    public void UdsTrafficOnObdIdentifiersIsIgnored()
    {
        var frames = new[] { Sf(0x7E0, 0x22, 0xF1, 0x90), Sf(0x7E8, 0x62, 0xF1, 0x90, 0x41) };
        Assert.Empty(ObdDecoder.Decode(frames));
    }

    [Fact]
    public void CatalogExposesFormulasForRequiredPids()
    {
        foreach (var pid in new byte[] { 0x00, 0x20, 0x40, 0x60, 0x80, 0xA0, 0xC0, 0x01, 0x03, 0x04, 0x05,
                     0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x1F, 0x21,
                     0x2F, 0x31, 0x33, 0x42, 0x43, 0x45, 0x46, 0x49, 0x4A, 0x5C, 0x5E })
            Assert.True(ObdPidCatalog.TryGet(1, pid, out _), $"PID {pid:X2}");
        Assert.False(ObdPidCatalog.TryGet(1, 0x6B, out _));
        Assert.True(ObdPidCatalog.TryGet(1, 0x42, out var voltage));
        Assert.Equal(14.0, voltage.Convert!([0x36, 0xB0]));
    }
}
