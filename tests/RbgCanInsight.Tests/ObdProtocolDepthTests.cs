using RbgCanInsight.Diagnostics.Obd;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class ObdProtocolDepthTests
{
    private static long _ts;

    private static CanFrame Sf(uint id, bool ext, params byte[] payload)
    {
        var data = new byte[8];
        Array.Fill(data, (byte)0x55);
        data[0] = (byte)payload.Length;
        payload.CopyTo(data, 1);
        return new CanFrame(0, id, data, _ts += 1_000_000, ext ? CanFrameFlags.Extended : CanFrameFlags.None);
    }

    private static ObdValue OneValue(params CanFrame[] frames)
    {
        var tx = Assert.Single(ObdDecoder.Decode(frames));
        return Assert.Single(Assert.Single(tx.Responses).Values);
    }

    [Fact]
    public void SparkReadinessDecodesSupportedAndCompleteFlags()
    {
        // B = 0x07 (misfire/fuel/components supported, all complete, spark)
        // C = 0xE5 -> catalyst, evap, O2 sensor, O2 heater? bits 0,2,5,6,7 supported
        // D = 0x24 -> bits 2 (evap) and 5 (O2 sensor) not complete
        var v = OneValue(Sf(0x7DF, false, 0x01, 0x01), Sf(0x7E8, false, 0x41, 0x01, 0x00, 0x07, 0xE5, 0x24));
        var r = v.Readiness!;
        Assert.False(r.CompressionIgnition);
        Assert.Equal(11, r.Monitors.Count);
        Assert.All(r.Monitors.Take(3), m => Assert.True(m.Supported && m.Complete));
        var names = r.Incomplete.Select(m => m.Name).ToArray();
        Assert.Equal(new[] { "Evaporative system", "Oxygen sensor" }, names);
        Assert.Contains(r.Monitors, m => m.Name == "Heated catalyst" && !m.Supported);
        Assert.Contains(r.Monitors, m => m.Name == "Oxygen sensor heater" && m.Supported && m.Complete);
        Assert.Contains(r.Monitors, m => m.Name == "Catalyst" && m.State == "complete");
        Assert.Contains(r.Monitors, m => m.Name == "Heated catalyst" && m.State == "not supported");
    }

    [Fact]
    public void ContinuousMonitorNotCompleteFlagsAndCompressionSet()
    {
        // B = 0x08 | 0x01 | 0x10 | 0x04  -> compression, misfire supported & incomplete, components supported
        var v = OneValue(Sf(0x7DF, false, 0x01, 0x01), Sf(0x7E8, false, 0x41, 0x01, 0x81, 0x1D, 0x28, 0x08));
        var r = v.Readiness!;
        Assert.True(r.CompressionIgnition);
        Assert.Contains(r.Monitors, m => m.Name == "Misfire" && m.Supported && !m.Complete);
        Assert.Contains(r.Monitors, m => m.Name == "Components" && m.Supported && m.Complete);
        Assert.Contains(r.Monitors, m => m.Name == "Fuel system" && !m.Supported);
        // C = 0x28: bit3 boost pressure, bit5 exhaust gas sensor; D = 0x08: boost pressure not complete
        Assert.Contains(r.Monitors, m => m.Name == "Boost pressure" && m.Supported && !m.Complete);
        Assert.Contains(r.Monitors, m => m.Name == "Exhaust gas sensor" && m.Supported && m.Complete);
        Assert.DoesNotContain(r.Monitors, m => m.Name == "Catalyst");
        Assert.Equal(3 + 6, r.Monitors.Count); // reserved diesel bits 2 and 4 are not listed
    }

    [Fact]
    public void CalibrationVerificationNumbersAreFourByteHex()
    {
        var v = OneValue(Sf(0x7DF, false, 0x09, 0x06),
            Sf(0x7E8, false, 0x49, 0x06, 0x01, 0xDE, 0xAD, 0xBE, 0xEF));
        Assert.Equal("DEADBEEF", v.Text);
        var c = Assert.Single(v.Counters!);
        Assert.Equal(0xDEADBEEFu, c.Value);
    }

    [Fact]
    public void InUsePerformanceTrackingNamesOnlyTheFirstTwoItems()
    {
        var v = OneValue(Sf(0x7DF, false, 0x09, 0x08),
            Sf(0x7E8, false, 0x49, 0x08, 0x02, 0x00, 0x05, 0x01, 0x03));
        Assert.Equal(2, v.Counters!.Count);
        Assert.StartsWith("OBDCOND", v.Counters[0].Name);
        Assert.Equal(5u, v.Counters[0].Value);
        Assert.StartsWith("IGNCNTR", v.Counters[1].Name);
        Assert.Equal(259u, v.Counters[1].Value);
    }

    [Fact]
    public void EcuNameStripsNulPadding()
    {
        var name = System.Text.Encoding.ASCII.GetBytes("ECM-ENG");
        var data = new byte[20];
        name.CopyTo(data, 0);
        var frames = new[]
        {
            Sf(0x7DF, false, 0x09, 0x0A),
            new CanFrame(0, 0x7E8, [0x10, 0x17, 0x49, 0x0A, 0x01, data[0], data[1], data[2]], _ts += 1_000_000, CanFrameFlags.None),
            new CanFrame(0, 0x7E0, [0x30, 0, 0, 0x55, 0x55, 0x55, 0x55, 0x55], _ts += 1_000_000, CanFrameFlags.None),
            new CanFrame(0, 0x7E8, [0x21, data[3], data[4], data[5], data[6], 0, 0, 0], _ts += 1_000_000, CanFrameFlags.None),
            new CanFrame(0, 0x7E8, [0x22, 0, 0, 0, 0, 0, 0, 0], _ts += 1_000_000, CanFrameFlags.None),
            new CanFrame(0, 0x7E8, [0x23, 0, 0, 0, 0, 0, 0, 0], _ts += 1_000_000, CanFrameFlags.None),
        };
        var v = OneValue(frames);
        Assert.Equal("ECM-ENG", v.Text);
    }

    [Fact]
    public void MaximumValuesAndInjectionTiming()
    {
        var max = OneValue(Sf(0x7DF, false, 0x01, 0x4F), Sf(0x7E8, false, 0x41, 0x4F, 0x02, 0x05, 0x10, 0x0A));
        Assert.Contains("max intake MAP 100 kPa", max.Text);
        Assert.Contains("max O2 sensor current 16 mA", max.Text);

        var maf = OneValue(Sf(0x7DF, false, 0x01, 0x50), Sf(0x7E8, false, 0x41, 0x50, 0x19, 0, 0, 0));
        Assert.Equal(250, maf.NumericValue);

        // (256*0x6B + 0x80)/128 - 210 = 214 - 210 ... 0x6B80/128 = 215 -> 5 deg
        var timing = OneValue(Sf(0x7DF, false, 0x01, 0x5D), Sf(0x7E8, false, 0x41, 0x5D, 0x6B, 0x80));
        Assert.Equal(5, timing.NumericValue);
    }

    [Fact]
    public void EmissionRequirementsPid5FStaysRaw()
    {
        var v = OneValue(Sf(0x7DF, false, 0x01, 0x5F), Sf(0x7E8, false, 0x41, 0x5F, 0x07));
        Assert.False(v.IsKnown);
        Assert.Null(v.NumericValue);
    }

    [Fact]
    public void FreezeFrameDtcPid02On29BitAddressing()
    {
        var tx = Assert.Single(ObdDecoder.Decode([
            Sf(0x18DA10F1, true, 0x02, 0x02, 0x00),
            Sf(0x18DAF110, true, 0x42, 0x02, 0x00, 0x01, 0x33)]));
        Assert.Equal((byte)2, tx.Mode);
        Assert.Equal((byte)0, tx.Request!.FreezeFrameNumber);
        var resp = Assert.Single(tx.Responses);
        Assert.Equal((byte?)0, resp.FreezeFrameNumber);
        Assert.Equal("P0133", Assert.Single(resp.Dtcs).Code);
        Assert.Equal("P0133", Assert.Single(resp.Values).Text);
    }

    [Fact]
    public void Functional29BitRequestIsAnsweredByTwoEcus()
    {
        var tx = Assert.Single(ObdDecoder.Decode([
            Sf(0x18DB33F1, true, 0x01, 0x0D),
            Sf(0x18DAF110, true, 0x41, 0x0D, 0x32),
            Sf(0x18DAF11E, true, 0x41, 0x0D, 0x33)]));
        Assert.True(tx.Request!.IsFunctional);
        Assert.Equal(2, tx.Responses.Count);
        Assert.Equal(new double?[] { 50, 51 }, tx.Responses.Select(r => r.Values[0].NumericValue).ToArray());
    }

    [Fact]
    public void NonObd29BitIdentifierIsIgnored()
    {
        Assert.DoesNotContain(ObdDecoder.Decode([Sf(0x18EF1234, true, 0x01, 0x0D), Sf(0x18DAF110, true, 0x41, 0x0D, 0x32)]),
            t => t.Request is not null);
    }
}
