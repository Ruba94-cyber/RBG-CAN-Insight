using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class DiagDataTests
{
    [Fact]
    public void LoadsDidsFromCddServices()
    {
        const string cdd =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <CANDELA>
              <ECU qual="DemoEcu">
                <DIAGCLASS>
                  <SERVICE>
                    <NAME><TUV>Engine coolant temperature</TUV></NAME>
                    <SHORTCUTQUAL>EngineCoolantTemperature_Read</SHORTCUTQUAL>
                    <SEMANTIC>CURRENTDATA</SEMANTIC>
                  </SERVICE>
                  <STATICVALUE v="62469" />
                  <SERVICE>
                    <NAME><TUV>Write configuration</TUV></NAME>
                    <SHORTCUTQUAL>Configuration_Write</SHORTCUTQUAL>
                    <SEMANTIC>VARIANTCODINGWRITE</SEMANTIC>
                  </SERVICE>
                  <STATICVALUE v="62470" />
                </DIAGCLASS>
              </ECU>
            </CANDELA>
            """;
        var path = Path.Combine(Path.GetTempPath(), $"rbg-test-{Guid.NewGuid():N}.cdd");
        File.WriteAllText(path, cdd);
        try
        {
            var catalog = DidCatalog.Load(path);

            Assert.Equal("DemoEcu", catalog.Ecu);
            // Read and configured write DIDs are both catalogued (write-only DIDs
            // still carry engineering parameters worth exporting).
            Assert.Equal(2, catalog.Parameters.Count);
            var did = catalog.Parameters.Single(item => item.Did == 0xF405);
            Assert.Equal("EngineCoolantTemperature", did.Name);
            Assert.Equal("Engine coolant temperature", did.LongName);
            Assert.Contains(catalog.Parameters, item => item.Did == 0xF406);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadsDidsFromOdxDiagServices()
    {
        const string odx =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <ODX MODEL-VERSION="2.2.0">
              <DIAG-LAYER-CONTAINER ID="dlc.1">
                <SHORT-NAME>DemoEcu</SHORT-NAME>
                <BASE-VARIANT ID="bv.1">
                  <SHORT-NAME>DemoEcu</SHORT-NAME>
                  <DIAG-DATA-DICTIONARY-SPEC>
                    <UNITS>
                      <UNIT ID="unit.degc">
                        <SHORT-NAME>DegreeCelsius</SHORT-NAME>
                        <DISPLAY-NAME>degC</DISPLAY-NAME>
                      </UNIT>
                    </UNITS>
                    <DATA-OBJECT-PROPS>
                      <DATA-OBJECT-PROP ID="dop.temp">
                        <SHORT-NAME>Temperature</SHORT-NAME>
                        <COMPU-METHOD>
                          <CATEGORY>LINEAR</CATEGORY>
                          <COMPU-INTERNAL-TO-PHYS>
                            <COMPU-SCALES>
                              <COMPU-SCALE>
                                <COMPU-RATIONAL-COEFFS>
                                  <COMPU-NUMERATOR><V>-40</V><V>1</V></COMPU-NUMERATOR>
                                  <COMPU-DENOMINATOR><V>1</V></COMPU-DENOMINATOR>
                                </COMPU-RATIONAL-COEFFS>
                              </COMPU-SCALE>
                            </COMPU-SCALES>
                          </COMPU-INTERNAL-TO-PHYS>
                        </COMPU-METHOD>
                        <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32" xsi:type="STANDARD-LENGTH-TYPE" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                          <BIT-LENGTH>8</BIT-LENGTH>
                        </DIAG-CODED-TYPE>
                        <UNIT-REF ID-REF="unit.degc" />
                      </DATA-OBJECT-PROP>
                    </DATA-OBJECT-PROPS>
                  </DIAG-DATA-DICTIONARY-SPEC>
                  <DIAG-COMMS>
                    <DIAG-SERVICE ID="svc.1" SEMANTIC="CURRENTDATA">
                      <SHORT-NAME>EngineCoolantTemperature_Read</SHORT-NAME>
                      <LONG-NAME>Engine coolant temperature</LONG-NAME>
                      <REQUEST-REF ID-REF="req.1" />
                      <POS-RESPONSE-REFS>
                        <POS-RESPONSE-REF ID-REF="resp.1" />
                      </POS-RESPONSE-REFS>
                    </DIAG-SERVICE>
                  </DIAG-COMMS>
                  <REQUESTS>
                    <REQUEST ID="req.1">
                      <PARAMS>
                        <PARAM SEMANTIC="SERVICE-ID" xsi:type="CODED-CONST" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                          <SHORT-NAME>SID</SHORT-NAME>
                          <CODED-VALUE>34</CODED-VALUE>
                          <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32" xsi:type="STANDARD-LENGTH-TYPE">
                            <BIT-LENGTH>8</BIT-LENGTH>
                          </DIAG-CODED-TYPE>
                        </PARAM>
                        <PARAM SEMANTIC="ID" xsi:type="CODED-CONST" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                          <SHORT-NAME>DID</SHORT-NAME>
                          <CODED-VALUE>62469</CODED-VALUE>
                          <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT32" xsi:type="STANDARD-LENGTH-TYPE">
                            <BIT-LENGTH>16</BIT-LENGTH>
                          </DIAG-CODED-TYPE>
                        </PARAM>
                      </PARAMS>
                    </REQUEST>
                  </REQUESTS>
                  <POS-RESPONSES>
                    <POS-RESPONSE ID="resp.1">
                      <PARAMS>
                        <PARAM SEMANTIC="DATA" xsi:type="VALUE" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                          <SHORT-NAME>Value</SHORT-NAME>
                          <DOP-REF ID-REF="dop.temp" />
                        </PARAM>
                      </PARAMS>
                    </POS-RESPONSE>
                  </POS-RESPONSES>
                </BASE-VARIANT>
              </DIAG-LAYER-CONTAINER>
            </ODX>
            """;
        var path = Path.Combine(Path.GetTempPath(), $"rbg-test-{Guid.NewGuid():N}.odx");
        File.WriteAllText(path, odx);
        try
        {
            var catalog = DidCatalog.Load(path);

            var did = Assert.Single(catalog.Parameters);
            Assert.Equal(0xF405, did.Did);
            Assert.Equal("EngineCoolantTemperature", did.Name);
            // Whole-DID scale is withheld until the data-record boundary is verified.
            Assert.Equal("", did.Unit);
            Assert.Null(did.Factor);
            Assert.Null(did.Offset);
            Assert.Null(did.BitLength);
            var field = Assert.Single(did.Fields);
            Assert.Equal("Value", field.Name);
            Assert.Equal(-1, field.BitOffset);
            Assert.Equal(8, field.BitLength);
            Assert.Equal("degC", field.Unit);
            Assert.Equal(1, field.Factor);
            Assert.Equal(-40, field.Offset);
            Assert.Contains(catalog.VariantDefinitions, variant => variant.Name == "DemoEcu");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CddNeverStealsDiscriminatorFromTheFollowingService()
    {
        const string cdd = """
            <CANDELA><ECU qual="Demo">
              <DIAGCLASS>
                <SERVICE><SHORTCUTQUAL>Unrelated_Read</SHORTCUTQUAL>
                  <SEMANTIC>CURRENTDATA</SEMANTIC></SERVICE>
                <NAME>an unrelated sibling element</NAME>
                <SERVICE><SHORTCUTQUAL>VehicleIdentifier_Read</SHORTCUTQUAL>
                  <SEMANTIC>IDENTIFICATION</SEMANTIC></SERVICE>
                <STATICVALUE v="0xF190"/>
              </DIAGCLASS>
            </ECU></CANDELA>
            """;
        var path = Path.Combine(Path.GetTempPath(), $"rbg-scoped-cdd-{Guid.NewGuid():N}.cdd");
        File.WriteAllText(path, cdd);
        try
        {
            var catalog = DidCatalog.Load(path);
            var did = Assert.Single(catalog.Parameters);
            Assert.Equal(0xF190, did.Did);
            Assert.Equal("VehicleIdentifier", did.Name);
            Assert.DoesNotContain(catalog.ServiceDefinitions, service => service.Name == "Unrelated" &&
                service.Identifier == 0xF190);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CddRejectsExternalEntitiesAndDtds()
    {
        const string cdd = """
            <?xml version="1.0"?>
            <!DOCTYPE CANDELA [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <CANDELA><ECU qual="&xxe;"/></CANDELA>
            """;
        var path = Path.Combine(Path.GetTempPath(), $"rbg-dtd-cdd-{Guid.NewGuid():N}.cdd");
        File.WriteAllText(path, cdd);
        try
        {
            Assert.ThrowsAny<System.Xml.XmlException>(() => DidCatalog.Load(path));
        }
        finally { File.Delete(path); }
    }


    [Fact]
    public void CddKeepsDeclaredResponseFieldsWithoutInventingUdsRecordOffsets()
    {
        const string cdd = """
            <CANDELA><ECU qual="Demo"><DIAGCLASS>
                <SERVICE>
                  <SHORTCUTQUAL>BatteryStatus_Read</SHORTCUTQUAL>
                  <SEMANTIC>CURRENTDATA</SEMANTIC>
                  <RESPONSE>
                    <DATAFIELD qual="Status">
                      <BITOFFSET>0</BITOFFSET><BITLENGTH>8</BITLENGTH>
                      <DATATYPE>A_UINT32</DATATYPE><UNIT>raw</UNIT>
                    </DATAFIELD>
                    <DATAFIELD qual="Temperature">
                      <BITOFFSET>8</BITOFFSET><BITLENGTH>16</BITLENGTH>
                      <DATATYPE>A_INT32</DATATYPE><UNIT>degC</UNIT>
                      <FACTOR>0.1</FACTOR><OFFSET>-40</OFFSET>
                    </DATAFIELD>
                  </RESPONSE>
                </SERVICE>
                <STATICVALUE v="0xF1A0"/>
            </DIAGCLASS></ECU></CANDELA>
            """;
        var path = Path.Combine(Path.GetTempPath(), $"rbg-cdd-fields-{Guid.NewGuid():N}.cdd");
        File.WriteAllText(path, cdd);
        try
        {
            var did = Assert.Single(DidCatalog.Load(path).Parameters);
            Assert.Equal(0xF1A0, did.Did);
            Assert.Equal(2, did.Fields.Count);
            Assert.Null(did.BitLength);
            var temp = did.Fields.Single(field => field.Name == "Temperature");
            Assert.Equal(16, temp.BitLength);
            Assert.Equal(-1, temp.BitOffset);
            Assert.Equal("degC", temp.Unit);
            Assert.False(temp.SupportsPhysicalConversion);
            Assert.Contains("position 8", temp.Description);
            // Prevent first-field units/scales being misattributed to the entire DID.
            Assert.Equal("", did.Unit);
            Assert.Null(did.Factor);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CddUnknownBitLengthNeverCreatesFabricatedFieldAtOffsetZero()
    {
        const string cdd = """
            <CANDELA><ECU qual="Demo"><DIAGCLASS>
                <SERVICE>
                  <SHORTCUTQUAL>Unspecified_Read</SHORTCUTQUAL>
                  <SEMANTIC>CURRENTDATA</SEMANTIC>
                  <RESPONSE><DATAFIELD qual="FieldA"><BITLENGTH>8</BITLENGTH></DATAFIELD></RESPONSE>
                </SERVICE><STATICVALUE v="62464"/>
            </DIAGCLASS></ECU></CANDELA>
            """;
        var path = Path.Combine(Path.GetTempPath(), $"rbg-cdd-unknown-{Guid.NewGuid():N}.cdd");
        File.WriteAllText(path, cdd);
        try
        {
            var did = Assert.Single(DidCatalog.Load(path).Parameters);
            Assert.Null(did.BitLength);
            var field = Assert.Single(did.Fields);
            Assert.Equal(-1, field.BitOffset);
            Assert.False(field.SupportsPhysicalConversion);
        }
        finally { File.Delete(path); }
    }

}
