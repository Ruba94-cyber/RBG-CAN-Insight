namespace RbgCanInsight.GuiTests;

// Synthetic offline inputs only. Never check private OEM datasets into the repo.
internal static class Fixtures
{
    public const string Dbc = """
        VERSION ""

        BO_ 256 EngineData: 8 ECM
         SG_ EngCoolantTemp : 0|8@1+ (1,-40) [-40|200] "degC" Vector__XXX
         SG_ EngineLoad : 8|8@1+ (0.5,0) [0|100] "%" Vector__XXX
         SG_ Mode : 16|8@1+ (1,0) [0|3] "" Vector__XXX

        BO_ 512 VehicleData: 8 BCM
         SG_ VehicleSpeed : 0|16@1+ (0.01,0) [0|250] "km/h" Vector__XXX

        VAL_ 256 Mode 0 "Off" 1 "On";
        """;

    public const string Trace = """
        date Tue Jul 07 10:00:00.000 2026
        base hex  timestamps absolute
        internal events logged
        0.000000 1 100 Rx d 8 32 20 00 00 00 00 00 00
        0.100000 1 100 Rx d 8 37 28 00 00 00 00 00 00
        0.200000 1 100 Rx d 8 3C 30 00 00 00 00 00 00
        0.300000 1 200 Rx d 8 88 13 00 00 00 00 00 00
        0.400000 1 7E0 Rx d 8 03 22 F4 05 00 00 00 00
        0.410000 1 7E8 Rx d 8 04 62 F4 05 32 00 00 00
        0.500000 1 7E0 Rx d 8 03 22 F4 05 00 00 00 00
        0.510000 1 7E8 Rx d 8 04 62 F4 05 37 00 00 00
        """;

    // Matches the source-backed nested CDD hierarchy already exercised in
    // EngineeringParameterHierarchyTests (Synthetic; no OEM information).
    public const string Cdd = """
        <?xml version="1.0" encoding="utf-8"?>
        <!DOCTYPE CANDELA SYSTEM "https://invalid.example/candela.dtd">
        <CANDELA><ECU qual="BodyEcu"/><ECUDOC>
          <DATATYPES>
            <IDENT id="t.speed"><QUAL>SpeedType</QUAL>
              <CVALUETYPE bl="16"/></IDENT>
            <STRUCT id="t.packet"><QUAL>PacketType</QUAL>
              <DATAOBJ id="p.speed" par="0x0A" dtref="t.speed"><QUAL>VehicleSpeed</QUAL>
                <NAME><TUV xml:lang="en-US">Vehicle speed</TUV></NAME>
                <DESC><TUV xml:lang="en-US">Encoded speed</TUV></DESC>
              </DATAOBJ>
              <STRUCT id="p.group"><QUAL>ModeGroup</QUAL>
                <GAPDATAOBJ id="p.bit" bl="1"><QUAL>ModeFlag</QUAL>
                  <DESC><TUV xml:lang="en-US">One status bit</TUV></DESC>
                </GAPDATAOBJ>
              </STRUCT>
            </STRUCT>
          </DATATYPES>
          <DIAGCLASS><DIAGINST id="d1"><QUAL>EngineeringStatus</QUAL>
            <NAME><TUV xml:lang="en-US">Engineering status</TUV></NAME>
            <SERVICE><QUAL>Read</QUAL></SERVICE>
            <STATICVALUE v="0xF190"/>
            <SIMPLECOMPCONT>
              <DATAOBJ id="par.master" dtref="t.packet"><QUAL>EngineeringPackage</QUAL>
                <DESC><TUV xml:lang="en-US">Top-level package</TUV></DESC>
              </DATAOBJ>
            </SIMPLECOMPCONT>
            <SIMPLECOMPCONT>
              <SPECDATAOBJ id="negative" spec="rc"><QUAL>NRC</QUAL></SPECDATAOBJ>
            </SIMPLECOMPCONT>
          </DIAGINST></DIAGCLASS>
        </ECUDOC></CANDELA>
        """;

    public const string Odx = """
        <ODX>
          <DIAG-LAYER-CONTAINER><SHORT-NAME>GuiFixture</SHORT-NAME></DIAG-LAYER-CONTAINER>
          <ECU-VARIANT><SHORT-NAME>ECM</SHORT-NAME></ECU-VARIANT>
          <REQUEST ID="REQ_F405">
            <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
            <PARAM SEMANTIC="ID"><CODED-VALUE>62469</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
          </REQUEST>
          <POS-RESPONSE ID="RESP_F405"><PARAM SEMANTIC="DATA"><DOP-REF ID-REF="DOP_TEMP" /></PARAM></POS-RESPONSE>
          <DATA-OBJECT-PROP ID="DOP_TEMP">
            <SHORT-NAME>EngineCoolantTemperature</SHORT-NAME>
            <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT8"><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
            <PHYSICAL-TYPE BASE-DATA-TYPE="A_FLOAT64" />
            <COMPU-METHOD><COMPU-INTERNAL-TO-PHYS><COMPU-SCALES><COMPU-SCALE><COMPU-RATIONAL-COEFFS>
            <COMPU-NUMERATOR><V>-40</V><V>1</V></COMPU-NUMERATOR>
            </COMPU-RATIONAL-COEFFS><LOWER-LIMIT>-40</LOWER-LIMIT><UPPER-LIMIT>215</UPPER-LIMIT></COMPU-SCALE>
            </COMPU-SCALES></COMPU-INTERNAL-TO-PHYS></COMPU-METHOD>
          </DATA-OBJECT-PROP>
          <DIAG-SERVICE>
            <SHORT-NAME>Read_EngineCoolantTemperature</SHORT-NAME>
            <REQUEST-REF ID-REF="REQ_F405" /><POS-RESPONSE-REF ID-REF="RESP_F405" />
          </DIAG-SERVICE>
        </ODX>
        """;
}
