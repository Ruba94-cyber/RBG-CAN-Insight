using RbgCanInsight.Dbc;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class ArxmlTests
{
    [Fact]
    public void ImportsBasicCanFrameAndSignalMapping()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rbg-arxml-{Guid.NewGuid():N}.arxml");
        File.WriteAllText(path,
            """
            <AUTOSAR>
              <AR-PACKAGES>
                <AR-PACKAGE>
                  <SHORT-NAME>Demo</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL>
                      <SHORT-NAME>VehicleSpeed</SHORT-NAME>
                      <LENGTH>16</LENGTH>
                      <NETWORK-REPRESENTATION-PROPS>
                        <SW-DATA-DEF-PROPS-VARIANTS>
                          <SW-DATA-DEF-PROPS-CONDITIONAL>
                            <COMPU-METHOD-REF DEST="COMPU-METHOD">/Demo/SpeedCompu</COMPU-METHOD-REF>
                            <UNIT-REF DEST="UNIT">/Demo/Kph</UNIT-REF>
                          </SW-DATA-DEF-PROPS-CONDITIONAL>
                        </SW-DATA-DEF-PROPS-VARIANTS>
                      </NETWORK-REPRESENTATION-PROPS>
                    </I-SIGNAL>
                    <UNIT>
                      <SHORT-NAME>Kph</SHORT-NAME>
                      <DISPLAY-NAME>km/h</DISPLAY-NAME>
                    </UNIT>
                    <COMPU-METHOD>
                      <SHORT-NAME>SpeedCompu</SHORT-NAME>
                      <COMPU-INTERNAL-TO-PHYS>
                        <COMPU-SCALES>
                          <COMPU-SCALE>
                            <COMPU-RATIONAL-COEFFS>
                              <COMPU-NUMERATOR>
                                <V>0</V>
                                <V>0.01</V>
                              </COMPU-NUMERATOR>
                              <COMPU-DENOMINATOR>
                                <V>1</V>
                              </COMPU-DENOMINATOR>
                            </COMPU-RATIONAL-COEFFS>
                          </COMPU-SCALE>
                        </COMPU-SCALES>
                      </COMPU-INTERNAL-TO-PHYS>
                    </COMPU-METHOD>
                    <I-SIGNAL-GROUP>
                      <SHORT-NAME>VehicleDynamics</SHORT-NAME>
                      <I-SIGNAL-REFS>
                        <I-SIGNAL-REF DEST="I-SIGNAL">/Demo/VehicleSpeed</I-SIGNAL-REF>
                      </I-SIGNAL-REFS>
                    </I-SIGNAL-GROUP>
                    <I-SIGNAL-I-PDU>
                      <SHORT-NAME>VehiclePdu</SHORT-NAME>
                      <I-SIGNAL-TO-PDU-MAPPINGS>
                        <I-SIGNAL-TO-I-PDU-MAPPING>
                          <SHORT-NAME>VehicleSpeedMap</SHORT-NAME>
                          <START-POSITION>0</START-POSITION>
                          <I-SIGNAL-REF DEST="I-SIGNAL">/Demo/VehicleSpeed</I-SIGNAL-REF>
                        </I-SIGNAL-TO-I-PDU-MAPPING>
                      </I-SIGNAL-TO-PDU-MAPPINGS>
                    </I-SIGNAL-I-PDU>
                    <CAN-FRAME>
                      <SHORT-NAME>VehicleData</SHORT-NAME>
                      <FRAME-LENGTH>8</FRAME-LENGTH>
                      <PDU-TO-FRAME-MAPPINGS>
                        <PDU-TO-FRAME-MAPPING>
                          <PDU-REF DEST="I-SIGNAL-I-PDU">/Demo/VehiclePdu</PDU-REF>
                        </PDU-TO-FRAME-MAPPING>
                      </PDU-TO-FRAME-MAPPINGS>
                    </CAN-FRAME>
                    <CAN-FRAME-TRIGGERING>
                      <SHORT-NAME>VehicleDataTrigger</SHORT-NAME>
                      <IDENTIFIER>291</IDENTIFIER>
                      <FRAME-REF DEST="CAN-FRAME">/Demo/VehicleData</FRAME-REF>
                    </CAN-FRAME-TRIGGERING>
                  </ELEMENTS>
                </AR-PACKAGE>
              </AR-PACKAGES>
            </AUTOSAR>
            """);

        try
        {
            var database = new ArxmlParser().ParseFile(path);

            var message = Assert.Single(database.Messages.Values);
            Assert.Equal("VehicleData", message.Name);
            Assert.Equal(0x123u, message.Identifier);
            var signal = Assert.Single(message.Signals);
            Assert.Equal("VehicleSpeed", signal.Name);
            Assert.Equal(16, signal.BitLength);
            Assert.Equal(0.01, signal.Factor);
            Assert.Equal("km/h", signal.Unit);
            Assert.Contains("VehicleDynamics", signal.Comment);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
