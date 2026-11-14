using RbgCanInsight.DiagData;
using RbgCanInsight.Diagnostics;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>ODX BIT-POSITION is the position of a parameter's least significant bit inside its
/// byte, counted from the byte's LSB (ISO 22901-1). Sub-byte big-endian fields must therefore be
/// read from the right bits, not mirrored.</summary>
public sealed class OdxBitPositionTests
{
    private static string Fixture(string codedTypeAttributes) =>
        $"""
        <ODX>
          <DIAG-LAYER-CONTAINER><SHORT-NAME>ECM</SHORT-NAME></DIAG-LAYER-CONTAINER>
          <REQUEST ID="REQ">
            <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
            <PARAM SEMANTIC="ID"><CODED-VALUE>62469</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
          </REQUEST>
          <POS-RESPONSE ID="RESP">
            <PARAM SEMANTIC="DATA"><SHORT-NAME>Flag</SHORT-NAME>
              <BYTE-POSITION>3</BYTE-POSITION><BIT-POSITION>3</BIT-POSITION><DOP-REF ID-REF="DOP1"/></PARAM>
            <PARAM SEMANTIC="DATA"><SHORT-NAME>Nibble</SHORT-NAME>
              <BYTE-POSITION>3</BYTE-POSITION><BIT-POSITION>4</BIT-POSITION><DOP-REF ID-REF="DOP4"/></PARAM>
            <PARAM SEMANTIC="DATA"><SHORT-NAME>Wide</SHORT-NAME>
              <BYTE-POSITION>4</BYTE-POSITION><BIT-POSITION>2</BIT-POSITION><DOP-REF ID-REF="DOP10"/></PARAM>
          </POS-RESPONSE>
          <DATA-OBJECT-PROP ID="DOP1"><SHORT-NAME>OneBit</SHORT-NAME>
            <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32"{codedTypeAttributes}><BIT-LENGTH>1</BIT-LENGTH></DIAG-CODED-TYPE>
          </DATA-OBJECT-PROP>
          <DATA-OBJECT-PROP ID="DOP4"><SHORT-NAME>FourBits</SHORT-NAME>
            <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32"{codedTypeAttributes}><BIT-LENGTH>4</BIT-LENGTH></DIAG-CODED-TYPE>
          </DATA-OBJECT-PROP>
          <DATA-OBJECT-PROP ID="DOP10"><SHORT-NAME>TenBits</SHORT-NAME>
            <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32"{codedTypeAttributes}><BIT-LENGTH>10</BIT-LENGTH></DIAG-CODED-TYPE>
          </DATA-OBJECT-PROP>
          <DIAG-SERVICE>
            <SHORT-NAME>Read_Status</SHORT-NAME>
            <REQUEST-REF ID-REF="REQ"/><POS-RESPONSE-REF ID-REF="RESP"/>
          </DIAG-SERVICE>
        </ODX>
        """;

    private static DidCatalog Load(string xml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rbg-odx-bitpos-{Guid.NewGuid():N}.odx");
        File.WriteAllText(path, xml);
        try { return DidCatalog.Load(path); }
        finally { File.Delete(path); }
    }

    private static Dictionary<string, string> DecodeRaw(DidCatalog catalog, params byte[] data)
    {
        var response = new byte[] { (byte)(3 + data.Length), 0x62, 0xF4, 0x05 }.Concat(data).ToArray();
        var frames = new[]
        {
            new CanFrame(0, 0x7E0, [0x03, 0x22, 0xF4, 0x05], 0),
            new CanFrame(0, 0x7E8, response, 1_000_000)
        };
        var transaction = Assert.Single(UdsTransactionAnalyzer.Analyze(frames, [catalog]));
        return transaction.Decoded.Values.ToDictionary(
            value => value.Path.Split('.', '/', ' ').Last(part => part.Length > 0), value => value.Raw);
    }

    [Fact]
    public void BigEndianSubByteFieldsAreLocatedFromTheLeastSignificantBit()
    {
        var catalog = Load(Fixture(string.Empty)); // IS-HIGHLOW-BYTE-ORDER defaults to true
        var fields = Assert.Single(catalog.Parameters).Fields.ToDictionary(field => field.Name);

        // MSB-first offsets inside the data record: bit 3 of byte 0 is the 5th bit from the MSB;
        // bits 7..4 are the first four; a 10-bit value with its LSB at bit 2 of the second of its
        // two bytes starts 16 - 2 - 10 = 4 bits into byte 1.
        Assert.Equal(4, fields["Flag"].BitOffset);
        Assert.Equal(0, fields["Nibble"].BitOffset);
        Assert.Equal(8 + 4, fields["Wide"].BitOffset);

        // Byte 0 = 0xA8 = 1010 1000: bit 3 set, upper nibble 0xA.
        // Bytes 1-2 = 0x0A 0xBC = 0000 1010 1011 1100: (0x0ABC >> 2) & 0x3FF = 0x2AF.
        var raw = DecodeRaw(catalog, 0xA8, 0x0A, 0xBC);
        Assert.Equal("0x1", raw["Flag"]);
        Assert.Equal("0xA", raw["Nibble"]);
        Assert.Equal("0x2AF", raw["Wide"]);
    }

    [Fact]
    public void LittleEndianSubByteFieldsKeepTheirLsbFirstOffsets()
    {
        var catalog = Load(Fixture(" IS-HIGHLOW-BYTE-ORDER=\"false\""));
        var fields = Assert.Single(catalog.Parameters).Fields.ToDictionary(field => field.Name);

        Assert.Equal(3, fields["Flag"].BitOffset);
        Assert.Equal(4, fields["Nibble"].BitOffset);
        Assert.Equal(8 + 2, fields["Wide"].BitOffset);

        // Little-endian 10-bit value: bytes BC 0A -> 0x0ABC, >> 2, & 0x3FF = 0x2AF.
        var raw = DecodeRaw(catalog, 0xA8, 0xBC, 0x0A);
        Assert.Equal("0x1", raw["Flag"]);
        Assert.Equal("0xA", raw["Nibble"]);
        Assert.Equal("0x2AF", raw["Wide"]);
    }
}
