using System.Text;
using RbgCanInsight.Analysis;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class J1939SignalDecoderTests
{
    private static J1939SignalValue Get(IReadOnlyList<J1939SignalValue> list, int spn) =>
        Assert.Single(list, v => v.Spn == spn);

    [Fact]
    public void CatalogNamesWellKnownPgns()
    {
        Assert.Equal("EEC1", J1939Catalog.TryDescribe(61444)!.Acronym);
        Assert.Equal("CCVS", J1939Catalog.TryDescribe(65265)!.Acronym);
        Assert.Equal("ET1", J1939Catalog.TryDescribe(65262)!.Acronym);
        Assert.Equal("DM1", J1939Catalog.TryDescribe(0xFECA)!.Acronym);
        Assert.Equal("AC", J1939Catalog.TryDescribe(60928)!.Acronym);
        Assert.Equal("ACK", J1939Catalog.TryDescribe(59392)!.Acronym);
        Assert.Equal("TP.CM", J1939Catalog.TryDescribe(60416)!.Acronym);
        Assert.Equal("TP.DT", J1939Catalog.TryDescribe(60160)!.Acronym);
        Assert.Equal("SOFT", J1939Catalog.TryDescribe(65242)!.Acronym);
        Assert.Equal("ECUID", J1939Catalog.TryDescribe(64965)!.Acronym);
        Assert.Equal("CI", J1939Catalog.TryDescribe(65259)!.Acronym);
        Assert.Equal("VI", J1939Catalog.TryDescribe(65260)!.Acronym);
    }

    [Fact]
    public void Pdu1PgnWithDestinationAddressIsNormalised()
    {
        Assert.Equal("Request", J1939Catalog.TryDescribe(0xEA2A)!.Acronym);
        Assert.True(J1939Catalog.TryDescribe(0xEF55)!.IsProprietary);
    }

    [Fact]
    public void ProprietaryBRangesAndUnknownPgns()
    {
        var b = J1939Catalog.TryDescribe(0xFF12)!;
        Assert.True(b.IsProprietary);
        Assert.Equal("PropB", b.Acronym);
        Assert.Equal("PropB2", J1939Catalog.TryDescribe(0x1FF05)!.Acronym);
        Assert.Null(J1939Catalog.TryDescribe(0xF123));
        Assert.Empty(J1939SignalDecoder.Decode(0xF123, new byte[8]));
    }

    [Fact]
    public void Eec1DecodesSpeedAndTorque()
    {
        // mode 3, driver demand 50 %, actual 25 %, 1500 rpm (12000 * 0.125), source 0
        byte[] p = [0xF3, 175, 150, 0xE0, 0x2E, 0x00, 0xFF, 0xFF];
        var v = J1939SignalDecoder.Decode(61444, p);
        Assert.Equal(1500, Get(v, 190).Value);
        Assert.Equal("rpm", Get(v, 190).Unit);
        Assert.Equal(50, Get(v, 512).Value);
        Assert.Equal(25, Get(v, 513).Value);
        Assert.Equal(3, Get(v, 899).Value);
        Assert.Equal(0, Get(v, 1483).Value);
        Assert.Equal(J1939ValueState.NotAvailable, Get(v, 2432).State);
    }

    [Fact]
    public void SentinelsAreNeverScaled()
    {
        byte[] p = [0xFF, 0xFE, 0xFF, 0x12, 0xFE, 0xFB, 0, 0];
        var v = J1939SignalDecoder.Decode(61444, p);
        Assert.Equal(J1939ValueState.NotAvailable, Get(v, 899).State);
        Assert.Equal(J1939ValueState.Error, Get(v, 512).State);
        Assert.Null(Get(v, 512).Value);
        Assert.Equal(J1939ValueState.NotAvailable, Get(v, 513).State);
        Assert.Equal(J1939ValueState.Error, Get(v, 190).State); // high byte 0xFE
        Assert.Null(Get(v, 190).Value);
        Assert.Equal("error", Get(v, 190).Text);
        Assert.Equal(J1939ValueState.Reserved, Get(v, 1483).State);

        var na = J1939SignalDecoder.Decode(61444, new byte[] { 0xF0, 0, 0, 0xFF, 0xFF, 0, 0, 0 });
        Assert.Equal(J1939ValueState.NotAvailable, Get(na, 190).State);
        // Largest valid 16-bit value (0xFAFF) is still a number.
        var max = J1939SignalDecoder.Decode(61444, new byte[] { 0xF0, 0, 0, 0xFF, 0xFA, 0, 0, 0 });
        Assert.Equal(J1939ValueState.Valid, Get(max, 190).State);
        Assert.Equal(0xFAFF * 0.125, Get(max, 190).Value);
    }

    [Fact]
    public void ShortPayloadOmitsSignalsBeyondItsEnd()
    {
        var v = J1939SignalDecoder.Decode(61444, new byte[] { 0xF0, 100, 100, 0x10 });
        Assert.DoesNotContain(v, x => x.Spn == 190);
        Assert.Contains(v, x => x.Spn == 513);
    }

    [Fact]
    public void CcvsDecodesSpeedAndSwitches()
    {
        // parking brake set (bits 3-4 = 01), 80 km/h = 0x5000/256, brake depressed (bits 5-6 of byte 4 = 01), set speed 90
        byte[] p = [0xF4, 0x00, 0x50, 0x10, 0x5A, 0xFF, 0xFF, 0xFF];
        var v = J1939SignalDecoder.Decode(65265, p);
        Assert.Equal(80, Get(v, 84).Value);
        Assert.Equal("set", Get(v, 70).Text);
        Assert.Equal("pedal depressed", Get(v, 597).Text);
        Assert.Equal("switched off", Get(v, 595).Text);
        Assert.Equal(90, Get(v, 86).Value);
        var fraction = J1939SignalDecoder.Decode(65265, new byte[] { 0xFF, 0x80, 0x00, 0xFF, 0xFF });
        Assert.Equal(0.5, Get(fraction, 84).Value);
        Assert.Equal(J1939ValueState.NotAvailable, Get(fraction, 70).State);
    }

    [Fact]
    public void Et1DecodesTemperaturesWithOffsets()
    {
        // coolant 85 C, fuel n/a, oil 100 C ((100+273)/0.03125 = 11936 = 0x2EA0), turbo n/a, intercooler error
        byte[] p = [125, 0xFF, 0xA0, 0x2E, 0xFF, 0xFF, 0xFE, 0xFF];
        var v = J1939SignalDecoder.Decode(65262, p);
        Assert.Equal(85, Get(v, 110).Value);
        Assert.Equal("°C", Get(v, 110).Unit);
        Assert.Equal(J1939ValueState.NotAvailable, Get(v, 174).State);
        Assert.Equal(100, Get(v, 175).Value);
        Assert.Equal(J1939ValueState.NotAvailable, Get(v, 176).State);
        Assert.Equal(J1939ValueState.Error, Get(v, 52).State);
        Assert.Equal(-40, Get(J1939SignalDecoder.Decode(65262, new byte[] { 0 }), 110).Value);
    }

    [Fact]
    public void FuelEconomyAndFluidPressures()
    {
        var lfe = J1939SignalDecoder.Decode(65266, [0x90, 0x01, 0x00, 0x02, 0xFF, 0xFF, 0xFA, 0xFF]);
        Assert.Equal(20, Get(lfe, 183).Value);
        Assert.Equal(1.0, Get(lfe, 184).Value);
        Assert.Equal(J1939ValueState.NotAvailable, Get(lfe, 185).State);
        Assert.Equal(100.0, Get(lfe, 51).Value!.Value, 6);

        var efl = J1939SignalDecoder.Decode(65263, [0xFF, 0xFF, 0xFF, 0x4B, 0xFF, 0xFF, 0xFF, 0xFF]);
        Assert.Equal(300, Get(efl, 100).Value);
        Assert.Equal("kPa", Get(efl, 100).Unit);
    }

    [Fact]
    public void RequestPgnIsDecoded()
    {
        var v = Assert.Single(J1939SignalDecoder.Decode(0xEA2A, [0xEE, 0xFE, 0x00]));
        Assert.Equal(65262, v.Value);
        Assert.Contains("ET1", v.Text);
        Assert.Empty(J1939SignalDecoder.Decode(59904, [0xEE, 0xFE]));
    }

    [Fact]
    public void AckAndNackAreDecoded()
    {
        var nack = J1939SignalDecoder.Decode(59392, [0x01, 0xFF, 0xFF, 0xFF, 0x0F, 0xEE, 0xFE, 0x00]);
        Assert.Contains("NACK", nack.Single(x => x.Name == "Control Byte").Text);
        Assert.Equal(0x0F, nack.Single(x => x.Name == "Address Acknowledged").Value);
        Assert.Equal(65262, nack.Single(x => x.Name == "Acknowledged PGN").Value);
        Assert.Equal(J1939ValueState.NotAvailable, nack.Single(x => x.Name == "Group Function Value").State);
        var ack = J1939SignalDecoder.Decode(59392, [0x00, 0x05, 0xFF, 0xFF, 0x00, 0xCA, 0xFE, 0x00]);
        Assert.Contains("ACK", ack.Single(x => x.Name == "Control Byte").Text);
        Assert.Contains("DM1", ack.Single(x => x.Name == "Acknowledged PGN").Text);
    }

    [Fact]
    public void ComponentIdentificationSplitsStarDelimitedFields()
    {
        var v = J1939SignalDecoder.Decode(65259, Encoding.ASCII.GetBytes("ACME*X15*12345*7*"));
        Assert.Equal(["Make", "Model", "Serial Number", "Unit Number"], v.Select(x => x.Name).ToArray());
        Assert.Equal("X15", v[1].Text);
        Assert.Empty(J1939SignalDecoder.Decode(65259, [0x01, 0x02, 0x03]));
    }

    [Fact]
    public void AddressClaimedExposesNameFields()
    {
        // identity 0x1234, manufacturer 0x123 (291), function 0x11
        ulong raw = 0x1234UL | (0x123UL << 21) | (0x11UL << 40) | (1UL << 60) | (1UL << 63);
        var v = J1939SignalDecoder.Decode(60928, BitConverter.GetBytes(raw));
        Assert.Equal(0x1234, v.Single(x => x.Name == "Identity Number").Value);
        Assert.Equal(0x123, v.Single(x => x.Name == "Manufacturer Code").Value);
        Assert.Equal("On-Highway", v.Single(x => x.Name == "Industry Group").Text);
    }
}
