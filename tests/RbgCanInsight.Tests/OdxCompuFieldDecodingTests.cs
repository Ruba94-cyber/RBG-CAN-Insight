using System.Text;
using System.Xml.Linq;
using RbgCanInsight.DiagData;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>
/// ODX DATA-OBJECT-PROP COMPU-METHOD wired into DID field decoding. Every expected physical value is computed by
/// hand from the ODX formulas in the comments of each test (ISO 22901-1 / ODX 2.2):
/// LINEAR / RAT-FUNC: f(x) = (V0 + V1*x) / (D0 + D1*x);  TAB-INTP: y0 + (y1 - y0)*(x - x0)/(x1 - x0).
/// The XML fragments are hand written; there is no OEM ODX oracle behind them.
/// </summary>
public sealed class OdxCompuFieldDecodingTests
{
    private const string Header = """
        <ODX>
          <DIAG-LAYER-CONTAINER><SHORT-NAME>ECM</SHORT-NAME></DIAG-LAYER-CONTAINER>
          <UNIT ID="U_KMH"><SHORT-NAME>kmh</SHORT-NAME><DISPLAY-NAME>km/h</DISPLAY-NAME>
            <PHYSICAL-DIMENSION-REF ID-REF="PD_SPEED"/></UNIT>
          <PHYSICAL-DIMENSION ID="PD_SPEED"><SHORT-NAME>speed</SHORT-NAME>
            <LENGTH-EXP>1</LENGTH-EXP><TIME-EXP>-1</TIME-EXP><MASS-EXP>0</MASS-EXP></PHYSICAL-DIMENSION>
        """;

    private static string Service(string name, int did, string dop, string unused = "") => $"""
          <REQUEST ID="REQ_{name}">
            <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
            <PARAM SEMANTIC="ID"><CODED-VALUE>{did}</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
          </REQUEST>
          <POS-RESPONSE ID="RESP_{name}">
            <PARAM SEMANTIC="DATA"><SHORT-NAME>Value</SHORT-NAME><BYTE-POSITION>3</BYTE-POSITION><DOP-REF ID-REF="DOP_{name}"/></PARAM>
          </POS-RESPONSE>
          {dop.Replace("DOPID", $"DOP_{name}")}
          <DIAG-SERVICE><SHORT-NAME>Read_{name}</SHORT-NAME><REQUEST-REF ID-REF="REQ_{name}"/><POS-RESPONSE-REF ID-REF="RESP_{name}"/></DIAG-SERVICE>
        """;

    /// <summary>A DOP "Value" with the given coded type, optional extra children and COMPU-METHOD xml.</summary>
    private static string Dop(string coded, string compu, string extra = "", string unit = "") =>
        $"""<DATA-OBJECT-PROP ID="DOPID"><SHORT-NAME>Value</SHORT-NAME>{compu}{coded}{extra}{unit}</DATA-OBJECT-PROP>""";

    private static string Coded(string type, int bits, string attributes = "") =>
        $"""<DIAG-CODED-TYPE BASE-DATA-TYPE="{type}"{attributes}><BIT-LENGTH>{bits}</BIT-LENGTH></DIAG-CODED-TYPE>""";

    private static string Compu(string category, string body = "") =>
        $"<COMPU-METHOD><CATEGORY>{category}</CATEGORY><COMPU-INTERNAL-TO-PHYS><COMPU-SCALES>{body}</COMPU-SCALES></COMPU-INTERNAL-TO-PHYS></COMPU-METHOD>";

    private static string Rational(string num, string den = "", string lower = "", string upper = "", string lowerType = "", string upperType = "") =>
        "<COMPU-SCALE>" +
        (lower.Length > 0 ? $"<LOWER-LIMIT{lowerType}>{lower}</LOWER-LIMIT>" : "") +
        (upper.Length > 0 ? $"<UPPER-LIMIT{upperType}>{upper}</UPPER-LIMIT>" : "") +
        $"<COMPU-RATIONAL-COEFFS><COMPU-NUMERATOR>{num}</COMPU-NUMERATOR>" +
        (den.Length > 0 ? $"<COMPU-DENOMINATOR>{den}</COMPU-DENOMINATOR>" : "") +
        "</COMPU-RATIONAL-COEFFS></COMPU-SCALE>";

    private static string Const(string lower, string upper, string text) =>
        $"<COMPU-SCALE><LOWER-LIMIT>{lower}</LOWER-LIMIT><UPPER-LIMIT>{upper}</UPPER-LIMIT><COMPU-CONST><VT>{text}</VT></COMPU-CONST></COMPU-SCALE>";

    private static string Point(string x, string y) =>
        $"<COMPU-SCALE><LOWER-LIMIT>{x}</LOWER-LIMIT><COMPU-CONST><V>{y}</V></COMPU-CONST></COMPU-SCALE>";

    private static DidCatalog Load(params string[] services)
    {
        var xml = Header + string.Concat(services) + "</ODX>";
        var path = Path.Combine(Path.GetTempPath(), $"rbg-odx-compu-{Guid.NewGuid():N}.odx");
        File.WriteAllText(path, xml);
        try { return DidCatalog.Load(path); }
        finally { File.Delete(path); }
    }

    private static DecodedDiagnosticValue Decode(string dop, params byte[] data)
    {
        var catalog = Load(Service("A", 62469, dop, ""));
        return Assert.Single(DecodeAll([catalog], data));
    }

    private static IReadOnlyList<DecodedDiagnosticValue> DecodeAll(IReadOnlyList<DidCatalog> catalogs, byte[] data)
    {
        static IsoTpMessage Msg(uint id, byte[] payload, long t) =>
            new(0, id, false, t, t, [0], payload, true, string.Empty);
        return UdsServiceDecoder.Decode(
            Msg(0x7E0, [0x22, 0xF4, 0x05], 0),
            [Msg(0x7E8, new byte[] { 0x62, 0xF4, 0x05 }.Concat(data).ToArray(), 1_000_000)], catalogs).Values;
    }

    [Fact]
    public void ValueSurvivesTheCanSourceVerificationOfTheTransactionAnalyzer()
    {
        // Padded single frames: the analyzer proves the bytes against the CAN frames and keeps provenance.
        var catalog = Load(Service("A", 62469,
            Dop(Coded("A_UINT32", 8), Compu("LINEAR", Rational("<V>-40</V><V>0.5</V>"))), ""));
        var frames = new[]
        {
            new CanFrame(0, 0x7E0, [0x03, 0x22, 0xF4, 0x05, 0, 0, 0, 0], 0),
            new CanFrame(0, 0x7E8, [0x04, 0x62, 0xF4, 0x05, 0x64, 0, 0, 0], 1_000_000)
        };
        var v = Assert.Single(Assert.Single(UdsTransactionAnalyzer.Analyze(frames, [catalog])).Decoded.Values);
        Assert.Equal(10d, v.NumericValue);                            // -40 + 0.5*100
        Assert.True(v.HasVerifiedSource);
        Assert.Contains("COMPU-METHOD LINEAR", v.Provenance);
    }

    // ---------------------------------------------------------------- IDENTICAL / LINEAR

    [Fact]
    public void IdenticalReturnsRawAsPhysicalAndKeepsTheRawHexNextToIt()
    {
        var v = Decode(Dop(Coded("A_UINT32", 8), Compu("IDENTICAL")), 0x2A);
        Assert.Equal("0x2A", v.Raw);
        Assert.Equal("42", v.Physical); // identity: 0x2A = 42
        Assert.Equal(42d, v.NumericValue);
        Assert.Contains("COMPU-METHOD IDENTICAL", v.Meaning);
    }

    [Fact]
    public void LinearAppliesOffsetAndFactorAndFlagsValuesOutsideTheScaleLimits()
    {
        // f(x) = V0 + V1*x = -40 + 0.5*x, scale valid for 0..200 (closed).
        var dop = Dop(Coded("A_UINT32", 8),
            Compu("LINEAR", Rational("<V>-40</V><V>0.5</V>", "<V>1</V>", "0", "200")));
        Assert.Equal(-40d, Decode(dop, 0x00).NumericValue);           // lower boundary: -40 + 0
        Assert.Equal(10d, Decode(dop, 0x64).NumericValue);            // x = 100: -40 + 50
        var upper = Decode(dop, 0xC8);                                // x = 200: -40 + 100
        Assert.Equal(60d, upper.NumericValue);

        var outside = Decode(dop, 0xC9);                              // x = 201 is not covered by any scale
        Assert.Null(outside.NumericValue);                            // flagged, not clamped to 60
        Assert.Equal("out-of-range raw value 0xC9", outside.Physical);
        Assert.Equal("0xC9", outside.Raw);
        Assert.Contains("no scale or table entry covers it", outside.Meaning);
    }

    [Fact]
    public void LinearWithDenominatorDividesTheWholeNumerator()
    {
        // f(x) = (V0 + V1*x)/(D0 + D1*x) = (10 + 1*x)/4 ; x = 6 -> 16/4 = 4
        var dop = Dop(Coded("A_UINT32", 8), Compu("LINEAR", Rational("<V>10</V><V>1</V>", "<V>4</V>")));
        Assert.Equal(4d, Decode(dop, 0x06).NumericValue);
    }

    [Fact]
    public void LinearWithZeroDenominatorIsMalformedNotInfinity()
    {
        var dop = Dop(Coded("A_UINT32", 8), Compu("LINEAR", Rational("<V>1</V><V>1</V>", "<V>0</V>")));
        var v = Decode(dop, 0x05);
        Assert.Null(v.NumericValue);
        Assert.Equal("0x5", v.Physical);
        Assert.Contains("malformed or degenerate", v.Meaning);
    }

    [Fact]
    public void UnreadableScaleLimitDoesNotSilentlyBecomeInfinite()
    {
        var dop = Dop(Coded("A_UINT32", 8), Compu("LINEAR", Rational("<V>0</V><V>1</V>", "", "abc", "10")));
        var v = Decode(dop, 0x05);
        Assert.Null(v.NumericValue);
        Assert.Contains("malformed", v.Meaning);
    }

    [Fact]
    public void SignedBitFieldsAreSignExtendedBeforeScaling()
    {
        // A_INT32, 12 bits, big-endian, BIT-POSITION 0: the field occupies bits 4..15 of the two bytes 0F F0,
        // i.e. 0xFF0 = 4080 -> 4080 - 4096 = -16 ; f(x) = 0.5*x = -8
        var dop = Dop(Coded("A_INT32", 12), Compu("LINEAR", Rational("<V>0</V><V>0.5</V>")));
        var v = Decode(dop, 0x0F, 0xF0);
        Assert.Equal(-8d, v.NumericValue);
        Assert.Equal("0xFF0", v.Raw);
        Assert.Contains("signed -16", v.Meaning);
    }

    [Fact]
    public void LowHighByteOrderIsHonoured()
    {
        // IS-HIGHLOW-BYTE-ORDER=false: bytes 34 12 -> 0x1234 = 4660 ; f(x) = 0.01*x = 46.6
        var dop = Dop(Coded("A_UINT32", 16, " IS-HIGHLOW-BYTE-ORDER=\"false\""),
            Compu("LINEAR", Rational("<V>0</V><V>0.01</V>")));
        var v = Decode(dop, 0x34, 0x12);
        Assert.Equal(0x1234, 4660);
        Assert.Equal(4660 * 0.01, v.NumericValue!.Value, 9);
        Assert.Equal("0x1234", v.Raw);
    }

    // ---------------------------------------------------------------- SCALE-LINEAR

    [Fact]
    public void ScaleLinearPicksTheScaleAndTreatsOpenLimitsExactly()
    {
        // scale 1: [0,99] closed, f = x ; scale 2: (99,255] lower open, f = -100 + 2x
        var dop = Dop(Coded("A_UINT32", 8), Compu("SCALE-LINEAR",
            Rational("<V>0</V><V>1</V>", "", "0", "99") +
            Rational("<V>-100</V><V>2</V>", "", "99", "255", " INTERVAL-TYPE=\"OPEN\"")));
        Assert.Equal(99d, Decode(dop, 99).NumericValue);              // only scale 1 covers 99
        Assert.Equal(100d, Decode(dop, 100).NumericValue);            // -100 + 200
        Assert.Equal(410d, Decode(dop, 255).NumericValue);            // -100 + 510
    }

    [Fact]
    public void ScaleLinearGapIsFlaggedNotInterpolated()
    {
        // scales [0,9] and [20,29]: raw 15 lies in the gap
        var dop = Dop(Coded("A_UINT32", 8), Compu("SCALE-LINEAR",
            Rational("<V>0</V><V>1</V>", "", "0", "9") + Rational("<V>0</V><V>2</V>", "", "20", "29")));
        var v = Decode(dop, 15);
        Assert.Null(v.NumericValue);
        Assert.Equal("out-of-range raw value 0xF", v.Physical);
        Assert.Equal(40d, Decode(dop, 20).NumericValue);
    }

    // ---------------------------------------------------------------- TEXTTABLE

    [Fact]
    public void TextTableMapsDeclaredCodesAndNeverInventsTextForUnmappedOnes()
    {
        var dop = Dop(Coded("A_UINT32", 8), Compu("TEXTTABLE", Const("0", "0", "Off") + Const("1", "1", "On")));
        var on = Decode(dop, 1);
        Assert.Equal("On", on.Physical);
        Assert.Null(on.NumericValue);
        Assert.Equal("0x1", on.Raw);
        Assert.Equal("Off", Decode(dop, 0).Physical);

        var unmapped = Decode(dop, 7);
        Assert.Equal("unmapped raw value 0x7", unmapped.Physical);
        Assert.Null(unmapped.NumericValue);
        Assert.Equal("0x7", unmapped.Raw);
    }

    [Fact]
    public void TextTableDefaultValueIsShownAsDefaultNotAsAMatch()
    {
        var compu = "<COMPU-METHOD><CATEGORY>TEXTTABLE</CATEGORY><COMPU-INTERNAL-TO-PHYS><COMPU-SCALES>" +
            Const("0", "0", "Off") +
            "</COMPU-SCALES><COMPU-DEFAULT-VALUE><VT>Reserved</VT></COMPU-DEFAULT-VALUE></COMPU-INTERNAL-TO-PHYS></COMPU-METHOD>";
        var v = Decode(Dop(Coded("A_UINT32", 8), compu), 9);
        Assert.Equal("Reserved", v.Physical);
        Assert.Null(v.NumericValue);
        Assert.Contains("COMPU-DEFAULT-VALUE, not a conversion", v.Meaning);
    }

    // ---------------------------------------------------------------- TAB-INTP

    [Fact]
    public void TabIntpInterpolatesLinearlyBetweenPointsAndDoesNotExtrapolate()
    {
        // points (0,0) (100,10) (200,50). x=150: 10 + (50-10)*(150-100)/(200-100) = 30 ; x=50: 0 + 10*50/100 = 5
        var dop = Dop(Coded("A_UINT32", 8), Compu("TAB-INTP", Point("0", "0") + Point("200", "50") + Point("100", "10")));
        Assert.Equal(30d, Decode(dop, 150).NumericValue);
        Assert.Equal(5d, Decode(dop, 50).NumericValue);
        Assert.Equal(10d, Decode(dop, 100).NumericValue);             // exact table point
        Assert.Equal(0d, Decode(dop, 0).NumericValue);                // first point
        Assert.Equal(50d, Decode(dop, 200).NumericValue);             // last point
        var beyond = Decode(dop, 201);                                // beyond the last point: no extrapolation
        Assert.Null(beyond.NumericValue);
        Assert.Equal("out-of-range raw value 0xC9", beyond.Physical);
    }

    [Fact]
    public void TabIntpWithDuplicateXIsMalformed()
    {
        var dop = Dop(Coded("A_UINT32", 8), Compu("TAB-INTP", Point("0", "0") + Point("0", "5")));
        var v = Decode(dop, 0);
        Assert.Null(v.NumericValue);
        Assert.Contains("malformed", v.Meaning);
    }

    // ---------------------------------------------------------------- unsupported / missing

    [Fact]
    public void CompuCodeIsMarkedUnsupportedAndKeepsTheRawValue()
    {
        var v = Decode(Dop(Coded("A_UINT32", 8), Compu("COMPUCODE")), 0x10);
        Assert.Null(v.NumericValue);
        Assert.Equal("0x10", v.Physical);
        Assert.Equal("0x10", v.Raw);
        Assert.Contains("UNSUPPORTED method 'COMPUCODE'", v.Meaning);
    }

    [Fact]
    public void MissingCompuMethodShowsTheCodedValueAsUncertain()
    {
        var v = Decode(Dop(Coded("A_UINT32", 8), ""), 0x07);
        Assert.Equal("7", v.Physical);
        Assert.Contains("physical scaling not declared", v.Meaning);
        Assert.Contains("(uncertain)", v.Meaning);
        Assert.Contains("has no COMPU-METHOD", v.Provenance);
    }

    // ---------------------------------------------------------------- INTERNAL-CONSTR validity

    [Fact]
    public void InternalConstrLimitsFlagRawValuesOutsideTheValidRange()
    {
        const string constr = """
            <INTERNAL-CONSTR><LOWER-LIMIT>10</LOWER-LIMIT><UPPER-LIMIT>200</UPPER-LIMIT>
              <SCALE-CONSTRS><SCALE-CONSTR VALIDITY="NOT-AVAILABLE"><LOWER-LIMIT>190</LOWER-LIMIT><UPPER-LIMIT>200</UPPER-LIMIT></SCALE-CONSTR></SCALE-CONSTRS>
            </INTERNAL-CONSTR>
            """;
        var dop = Dop(Coded("A_UINT32", 8), Compu("IDENTICAL"), constr);
        Assert.Equal(10d, Decode(dop, 10).NumericValue);              // boundary is valid (closed)
        Assert.Equal(189d, Decode(dop, 189).NumericValue);
        var below = Decode(dop, 9);
        Assert.Null(below.NumericValue);
        Assert.Contains("INTERNAL-CONSTR", below.Meaning);
        var unavailable = Decode(dop, 195);
        Assert.Null(unavailable.NumericValue);
        Assert.Contains("NOT-AVAILABLE", unavailable.Meaning);
    }

    // ---------------------------------------------------------------- float types

    [Fact]
    public void Float32IsDecodedAsIeee754InBothByteOrders()
    {
        // 0x41200000 = 10.0f ; f(x) = 1 + 2x = 21
        var compu = Compu("LINEAR", Rational("<V>1</V><V>2</V>"));
        var big = Decode(Dop(Coded("A_FLOAT32", 32), compu), 0x41, 0x20, 0x00, 0x00);
        Assert.Equal(21d, big.NumericValue);
        Assert.Equal("41 20 00 00", big.Raw);
        var little = Decode(Dop(Coded("A_FLOAT32", 32, " IS-HIGHLOW-BYTE-ORDER=\"false\""), compu), 0x00, 0x00, 0x20, 0x41);
        Assert.Equal(21d, little.NumericValue);
    }

    private static DecodedDiagnosticValue DecodeLong(DidCatalog catalog, byte[] data)
    {
        static IsoTpMessage Msg(uint id, byte[] payload, long t) =>
            new(0, id, false, t, t, [0], payload, true, string.Empty);
        var decoded = UdsServiceDecoder.Decode(
            Msg(0x7E0, [0x22, 0xF4, 0x05], 0),
            [Msg(0x7E8, new byte[] { 0x62, 0xF4, 0x05 }.Concat(data).ToArray(), 1_000_000)], [catalog]);
        return Assert.Single(decoded.Values);
    }

    [Fact]
    public void Float64IsDecodedAndNanIsFlaggedNotConverted()
    {
        // 1.5 = 0x3FF8000000000000 (big-endian); IDENTICAL
        var catalog = Load(Service("A", 62469, Dop(Coded("A_FLOAT64", 64), Compu("IDENTICAL")), ""));
        var v = DecodeLong(catalog, [0x3F, 0xF8, 0, 0, 0, 0, 0, 0]);
        Assert.Equal(1.5, v.NumericValue);
        Assert.Equal("1.5", v.Physical);

        var nan = Load(Service("B", 62469, Dop(Coded("A_FLOAT32", 32), Compu("IDENTICAL")), ""));
        var n = DecodeLong(nan, [0x7F, 0xC0, 0, 0]);                  // quiet NaN
        Assert.Null(n.NumericValue);
        Assert.Equal("7F C0 00 00", n.Physical);
        Assert.Contains("NaN or infinite", n.Meaning);
    }

    [Fact]
    public void FloatWithWrongBitLengthStaysRaw()
    {
        var v = Decode(Dop(Coded("A_FLOAT32", 16), Compu("IDENTICAL")), 0x41, 0x20);
        Assert.Null(v.NumericValue);
        Assert.Contains("needs a byte-aligned 32-bit field", v.Meaning);
    }

    // ---------------------------------------------------------------- unit + provenance

    [Fact]
    public void UnitAndPhysicalDimensionComeFromTheDopAndProvenanceNamesDopAndFile()
    {
        var dop = Dop(Coded("A_UINT32", 8), Compu("LINEAR", Rational("<V>0</V><V>2</V>")), "", "<UNIT-REF ID-REF=\"U_KMH\"/>");
        var v = Decode(dop, 0x05);
        Assert.Equal(10d, v.NumericValue);                            // 2*5
        Assert.Equal("km/h", v.Unit);
        Assert.Contains("ODX DOP 'Value'", v.Provenance);
        Assert.Contains("COMPU-METHOD LINEAR", v.Provenance);
        Assert.Contains("unit dimension length^1 time^-1", v.Provenance);
        Assert.Contains(".odx", v.Provenance);
    }

    // ---------------------------------------------------------------- conflicts

    [Fact]
    public void ServicesDeclaringTheSameDidWithDifferentCompuMethodsAreNotVerified()
    {
        var a = Service("A", 62469, Dop(Coded("A_UINT32", 8), Compu("LINEAR", Rational("<V>0</V><V>1</V>"))), "");
        var b = Service("B", 62469, Dop(Coded("A_UINT32", 8), Compu("LINEAR", Rational("<V>0</V><V>2</V>"))), "");
        var catalog = Load(a, b);
        Assert.Contains(Assert.Single(catalog.Parameters).EngineeringIssues,
            issue => issue.StartsWith("Conflicting physical field layouts", StringComparison.Ordinal));
        var v = Assert.Single(DecodeAll([catalog], [0x05]));
        Assert.Null(v.NumericValue);                                  // neither 5 nor 10 is claimed
        Assert.Empty(v.Provenance);
    }

    [Fact]
    public void IdenticalCompuMethodsInTwoServicesAreNotAFieldConflict()
    {
        var a = Service("A", 62469, Dop(Coded("A_UINT32", 8), Compu("LINEAR", Rational("<V>0</V><V>2</V>"))), "");
        var b = Service("B", 62469, Dop(Coded("A_UINT32", 8), Compu("LINEAR", Rational("<V>0</V><V>2</V>"))), "");
        var catalog = Load(a, b);
        // The two services still differ in their engineering structure (reported separately), but the
        // field conversion itself is not a conflict.
        Assert.DoesNotContain(Assert.Single(catalog.Parameters).EngineeringIssues,
            issue => issue.StartsWith("Conflicting physical field layouts", StringComparison.Ordinal));
        Assert.Single(Assert.Single(catalog.Parameters).Fields);
    }

    [Fact]
    public void VariantsOfTheSameEcuWithDifferentCompuMethodsAreNeverResolvedByLoadOrder()
    {
        var one = Load(Service("A", 62469, Dop(Coded("A_UINT32", 8), Compu("LINEAR", Rational("<V>0</V><V>1</V>"))), ""));
        var two = Load(Service("A", 62469, Dop(Coded("A_UINT32", 8), Compu("LINEAR", Rational("<V>0</V><V>3</V>"))), ""));
        var values = DecodeAll([one, two], [0x05]);
        Assert.DoesNotContain(values, v => v.NumericValue is 5d or 15d);
    }

    // ---------------------------------------------------------------- variable length (stand-alone)

    private static OdxVariableLengthType Variable(string body) => OdxVariableLengthType.Parse(XElement.Parse(
        $"<DIAG-CODED-TYPE xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:type=\"MIN-MAX-LENGTH-TYPE\" BASE-DATA-TYPE=\"A_ASCIISTRING\">{body}</DIAG-CODED-TYPE>"))!;

    [Fact]
    public void ZeroTerminatedStringIsCutAtTheTerminatorAndLengthChecked()
    {
        var type = Variable("<MIN-LENGTH>1</MIN-LENGTH><MAX-LENGTH>4</MAX-LENGTH><TERMINATION>ZERO</TERMINATION>");
        Assert.True(type.TryExtract("AB\0CD"u8, out var value, out var consumed, out _));
        Assert.Equal("AB", Encoding.ASCII.GetString(value));
        Assert.Equal(3, consumed);                                    // value + terminator

        Assert.True(type.TryExtract("ABCD\0"u8, out value, out consumed, out _)); // exactly MAX-LENGTH bytes
        Assert.Equal("ABCD", Encoding.ASCII.GetString(value));

        Assert.False(type.TryExtract("ABCDE\0"u8, out _, out _, out var tooLong));
        Assert.Contains("terminator", tooLong);
        Assert.False(type.TryExtract("\0"u8, out _, out _, out var tooShort));
        Assert.Contains("MIN-LENGTH 1", tooShort);
        Assert.False(type.TryExtract("AB"u8, out _, out _, out _));   // unterminated
    }

    [Fact]
    public void HexFfAndEndOfPduTerminationsAreSupportedAndUnknownOnesAreNot()
    {
        var ff = Variable("<MIN-LENGTH>0</MIN-LENGTH><MAX-LENGTH>8</MAX-LENGTH><TERMINATION>HEX-FF</TERMINATION>");
        Assert.True(ff.TryExtract(new byte[] { 1, 2, 0xFF, 9 }, out var value, out var consumed, out _));
        Assert.Equal(new byte[] { 1, 2 }, value.ToArray());
        Assert.Equal(3, consumed);

        var pdu = Variable("<MIN-LENGTH>2</MIN-LENGTH><MAX-LENGTH>3</MAX-LENGTH><TERMINATION>END-OF-PDU</TERMINATION>");
        Assert.True(pdu.TryExtract(new byte[] { 1, 2, 3 }, out value, out consumed, out _));
        Assert.Equal(3, consumed);
        Assert.False(pdu.TryExtract(new byte[] { 1, 2, 3, 4 }, out _, out _, out _));
        Assert.False(pdu.TryExtract(new byte[] { 1 }, out _, out _, out _));

        var odd = Variable("<MIN-LENGTH>0</MIN-LENGTH><TERMINATION>HI-LO</TERMINATION>");
        Assert.False(odd.TryExtract(new byte[] { 1 }, out _, out _, out var issue));
        Assert.Contains("Unsupported or missing TERMINATION", issue);
    }

    [Fact]
    public void StandardLengthTypesAreNotVariableLength() =>
        Assert.Null(OdxVariableLengthType.Parse(XElement.Parse(Coded("A_UINT32", 8))));
}
