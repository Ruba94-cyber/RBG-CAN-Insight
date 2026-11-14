using RbgCanInsight.Diagnostics;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class UdsServiceCompletenessTests
{
    private static IsoTpMessage Message(uint id, byte[] payload, long time = 0) =>
        new(0, id, false, time, time, [0], payload, true, string.Empty);

    private static DecodedUdsOperation Decode(byte[] request, byte[] response) =>
        UdsServiceDecoder.Decode(Message(0x7E0, request), [Message(0x7E8, response, 1_000_000)], []);

    [Theory]
    [InlineData((byte)0x29, "Authentication")]
    [InlineData((byte)0x83, "AccessTimingParameter")]
    [InlineData((byte)0x84, "SecuredDataTransmission")]
    public void NewServicesAreKnownRequestsWithPositiveResponses(byte sid, string name)
    {
        Assert.True(UdsServiceDecoder.IsKnownRequest(sid));
        Assert.True(UdsServiceDecoder.IsPositiveResponse((byte)(sid + 0x40)));
        Assert.Equal(name, Decode([sid, 0x01, 0x00], [(byte)(sid + 0x40), 0x01, 0x00]).ServiceName);
    }

    [Fact]
    public void ControlDtcSettingPositiveResponseIsRecognisedAbove0xC0()
    {
        Assert.True(UdsServiceDecoder.IsPositiveResponse(0xC5));
        Assert.True(UdsServiceDecoder.IsPositiveResponse(0xC6));
        Assert.False(UdsServiceDecoder.IsPositiveResponse(0xFF));
    }

    [Fact]
    public void AuthenticationSubFunctionAndReturnParameterAreNamed()
    {
        var decoded = Decode([0x29, 0x05, 0x01], [0x69, 0x05, 0x00, 0xAA, 0xBB, 0xCC]);
        Assert.Equal("requestChallengeForAuthentication · 1 parameter byte(s)", decoded.RequestSummary);
        Assert.Contains("requestAccepted", decoded.ResponseSummary);
        Assert.Contains("3 data byte(s)", decoded.ResponseSummary);

        var deAuth = Decode([0x29, 0x00], [0x69, 0x00, 0x10]);
        Assert.Equal("deAuthenticate", deAuth.ObjectText);
        Assert.Contains("deAuthentication successful", deAuth.ResponseSummary);
    }

    [Fact]
    public void AccessTimingParameterDecodesP2AndP2StarRecords()
    {
        var read = Decode([0x83, 0x03], [0xC3, 0x03, 0x00, 0x32, 0x01, 0xF4]);
        Assert.Contains("readCurrentlyActiveTimingParameters", read.ResponseSummary);
        Assert.Contains("P2 50 ms, P2* 5000 ms", read.ResponseSummary);

        var set = Decode([0x83, 0x04, 0x00, 0x64, 0x03, 0xE8], [0xC3, 0x04]);
        Assert.Contains("setTimingParametersToGivenValues", set.RequestSummary);
        Assert.Contains("P2 100 ms, P2* 10000 ms", set.RequestSummary);
    }

    [Fact]
    public void SecuredDataTransmissionHasNoSubFunction()
    {
        var decoded = Decode([0x84, 0x00, 0x05, 0x01, 0x02, 0x03], [0xC4, 0x00, 0x05, 0x09]);
        Assert.Equal("administrative parameter 0x0005", decoded.ObjectText);
        Assert.Contains("3 security data byte(s)", decoded.RequestSummary);
        Assert.Contains("administrative parameter 0x0005", decoded.ResponseSummary);
    }

    [Fact]
    public void SessionControlResponseReportsAnnouncedTiming()
    {
        var decoded = Decode([0x10, 0x03], [0x50, 0x03, 0x00, 0x19, 0x01, 0xF4]);
        Assert.Contains("extended session", decoded.ResponseSummary);
        Assert.Contains("P2 25 ms, P2* 5000 ms", decoded.ResponseSummary);
    }

    [Theory]
    [InlineData((byte)0x34, "authenticationRequired")]
    [InlineData((byte)0x38, "secureDataTransmissionRequired")]
    [InlineData((byte)0x3A, "secureDataVerificationFailed")]
    [InlineData((byte)0x50, "certificateVerificationFailedInvalidTimePeriod")]
    [InlineData((byte)0x5D, "deAuthenticationFailed")]
    [InlineData((byte)0x78, "responsePending")]
    [InlineData((byte)0x81, "rpmTooHigh")]
    [InlineData((byte)0x8F, "brakeSwitchNotClosed")]
    [InlineData((byte)0x94, "resourceTemporarilyNotAvailable")]
    public void NrcTableCoversTheIso14229Codes(byte code, string name)
    {
        Assert.Equal(name, UdsServiceDecoder.NrcName(code));
        Assert.True(UdsServiceDecoder.IsStandardNrc(code));
    }

    [Theory]
    [InlineData((byte)0x95, "reservedForSpecificConditionsNotCorrect")]
    [InlineData((byte)0xF0, "vehicleManufacturerSpecificConditionsNotCorrect")]
    [InlineData((byte)0x40, "reservedByExtendedDataLinkSecurityDocument")]
    [InlineData((byte)0x15, "NRC 0x15")]
    public void ReservedAndManufacturerRangesAreLabelled(byte code, string prefix)
    {
        Assert.StartsWith(prefix, UdsServiceDecoder.NrcName(code));
        Assert.False(UdsServiceDecoder.IsStandardNrc(code));
    }

    [Fact]
    public void NegativeResponseUsesTheCompleteTable()
    {
        var decoded = Decode([0x29, 0x01, 0x00], [0x7F, 0x29, 0x51]);
        Assert.Equal((byte)0x51, decoded.NegativeResponseCode);
        Assert.Equal("certificateVerificationFailedInvalidSignature", decoded.NegativeResponseText);
    }
}
