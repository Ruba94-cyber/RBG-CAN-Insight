using System.Xml.Linq;
using RbgCanInsight.DiagData;
using RbgCanInsight.Diagnostics;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class OdxDtcEnvironmentDataTests
{
    private const string Source = """
        <ODX>
          <REQUEST ID="REQ">
            <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE>
              <DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
            <PARAM SEMANTIC="ID"><CODED-VALUE>61840</CODED-VALUE>
              <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
          </REQUEST>
          <POS-RESPONSE ID="RESPONSE"><PARAM SEMANTIC="DATA"><SHORT-NAME>VIN</SHORT-NAME>
            <DOP-SNREF SHORT-NAME="U8"/></PARAM></POS-RESPONSE>
          <DIAG-SERVICE><SHORT-NAME>Read_VIN</SHORT-NAME>
            <REQUEST-REF ID-REF="REQ"/><POS-RESPONSE-REF ID-REF="RESPONSE"/></DIAG-SERVICE>
          <DATA-OBJECT-PROP ID="DOP_SPEED"><SHORT-NAME>Speed</SHORT-NAME>
            <COMPU-METHOD><CATEGORY>LINEAR</CATEGORY><COMPU-INTERNAL-TO-PHYS><COMPU-SCALES><COMPU-SCALE>
              <COMPU-RATIONAL-COEFFS>
                <COMPU-NUMERATOR><V>0</V><V>0.5</V></COMPU-NUMERATOR>
                <COMPU-DENOMINATOR><V>1</V></COMPU-DENOMINATOR>
              </COMPU-RATIONAL-COEFFS></COMPU-SCALE></COMPU-SCALES></COMPU-INTERNAL-TO-PHYS></COMPU-METHOD>
            <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32"><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
            <UNIT-REF ID-REF="KMH"/>
          </DATA-OBJECT-PROP>
          <DATA-OBJECT-PROP ID="DOP_COUNT"><SHORT-NAME>Count</SHORT-NAME>
            <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32"><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
          </DATA-OBJECT-PROP>
          <DATA-OBJECT-PROP ID="U8"><SHORT-NAME>U8</SHORT-NAME>
            <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32"><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
          </DATA-OBJECT-PROP>
          <UNIT ID="KMH"><SHORT-NAME>kmh</SHORT-NAME><DISPLAY-NAME>km/h</DISPLAY-NAME></UNIT>
          <DTC ID="D1"><SHORT-NAME>P0123</SHORT-NAME><TROUBLE-CODE>1193046</TROUBLE-CODE><TEXT>Throttle low</TEXT></DTC>
          <DTC ID="D2"><SHORT-NAME>P0456</SHORT-NAME><TROUBLE-CODE>1193047</TROUBLE-CODE><TEXT>Other</TEXT></DTC>
          <ENV-DATA ID="ENV_SNAP"><SHORT-NAME>FreezeFrame</SHORT-NAME>
            <DTC-VALUES><DTC-VALUE>1193046</DTC-VALUE></DTC-VALUES>
            <PARAMS>
              <PARAM><SHORT-NAME>VehicleSpeed</SHORT-NAME><BYTE-POSITION>0</BYTE-POSITION>
                <DOP-REF ID-REF="DOP_SPEED"/></PARAM>
              <PARAM><SHORT-NAME>Counter</SHORT-NAME><BYTE-POSITION>1</BYTE-POSITION>
                <DOP-REF ID-REF="DOP_COUNT"/></PARAM>
            </PARAMS>
          </ENV-DATA>
          <ENV-DATA ID="ENV_EXT"><SHORT-NAME>ExtendedDataRecord</SHORT-NAME>
            <ALL-VALUE/>
            <PARAMS>
              <PARAM><SHORT-NAME>Occurrences</SHORT-NAME><BYTE-POSITION>0</BYTE-POSITION>
                <DOP-REF ID-REF="DOP_COUNT"/></PARAM>
            </PARAMS>
          </ENV-DATA>
        </ODX>
        """;

    private static IReadOnlyList<DidCatalog> Load() =>
        OdxCatalogReader.LoadFromDocuments([XDocument.Parse(Source)], "env.odx");

    [Fact]
    public void EnvDataBecomesSnapshotAndExtendedDefinitionsPerDtc()
    {
        var catalog = Assert.Single(Load());
        var first = catalog.DtcDefinitions.Single(d => d.Code == 0x123456);
        var snap = first.SnapshotFields;
        Assert.Equal(["VehicleSpeed", "Counter"], snap.Select(f => f.Name));
        Assert.Equal([0, 8], snap.Select(f => f.BitOffset));
        Assert.Equal("km/h", snap[0].Unit);
        Assert.Equal("Occurrences", Assert.Single(first.ExtendedDataFields).Name);

        // The snapshot block names only the first DTC; the extended block is ALL-VALUE.
        var second = catalog.DtcDefinitions.Single(d => d.Code == 0x123457);
        Assert.Empty(second.SnapshotFields);
        Assert.Equal("Occurrences", Assert.Single(second.ExtendedDataFields).Name);
    }

    [Fact]
    public void LoadedLayoutsDecodeARealReadDtcInformationResponse()
    {
        var catalogs = Load();
        static IsoTpMessage Msg(uint id, byte[] payload, long t) =>
            new(0, id, false, t, t, [0], payload, true, string.Empty);

        // 0x19 0x06 extended data record 1 of DTC 0x123456: one occurrence counter byte.
        var decoded = UdsServiceDecoder.Decode(
            Msg(0x7E0, [0x19, 0x06, 0x12, 0x34, 0x56, 0x01], 0),
            [Msg(0x7E8, [0x59, 0x06, 0x12, 0x34, 0x56, 0x09, 0x01, 0x07], 1_000_000)],
            catalogs);
        var record = Assert.Single(decoded.DtcRecords);
        Assert.Equal(DtcRecordKind.ExtendedData, record.Kind);
        var value = Assert.Single(record.Values);
        Assert.Equal("Occurrences", value.Name);
        Assert.Equal(7d, value.NumericValue);
    }
}
