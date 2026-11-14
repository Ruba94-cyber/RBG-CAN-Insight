using RbgCanInsight.DiagData;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class CddStructuralServiceTests
{
    // Mimics the real CANdela layout: SERVICE.tmplref -> DCLSRVTMPL.tmplref ->
    // PROTOCOLSERVICE whose REQ holds the constant SID component; the DID is a
    // STATICVALUE owned by the DIAGINST; the data type states length/unit/scaling.
    private const string Structured =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <CANDELA>
          <ECUDOC>
            <PROTOCOLSERVICE id="_P22"><QUAL>RDBI</QUAL>
              <REQ><CONSTCOMP id="_c1" spec="sid" bl="8" v="34"/></REQ>
            </PROTOCOLSERVICE>
            <PROTOCOLSERVICE id="_P27"><QUAL>SA</QUAL>
              <REQ><CONSTCOMP id="_c2" spec="sid" bl="8" v="39"/>
                   <STATICCOMP id="_s1" spec="sub" dtref="_T"/></REQ>
            </PROTOCOLSERVICE>
            <PROTOCOLSERVICE id="_P3E"><QUAL>TP</QUAL>
              <REQ><CONSTCOMP id="_c3" spec="sid" bl="8" v="62"/>
                   <CONSTCOMP id="_c4" spec="sub" bl="8" v="1"/></REQ>
            </PROTOCOLSERVICE>
            <DCLSRVTMPL id="_D22" tmplref="_P22"><QUAL>Read</QUAL></DCLSRVTMPL>
            <DCLSRVTMPL id="_D27" tmplref="_P27"><QUAL>Seed</QUAL></DCLSRVTMPL>
            <DCLSRVTMPL id="_D3E" tmplref="_P3E"><QUAL>Send</QUAL></DCLSRVTMPL>
            <SHSTATIC id="_SH" spec="sub"><QUAL>Level</QUAL><STATICCOMPREF idref="_s1"/></SHSTATIC>
            <LINCOMP id="_TV" bm="255"><QUAL>Voltage</QUAL>
              <CVALUETYPE bl="8" enc="uns" sig="0" qty="atom"/>
              <PVALUETYPE bl="64" enc="dbl" sig="1" df="flt" qty="atom"><UNIT>V</UNIT></PVALUETYPE>
              <COMP f="0.1" o="0"/></LINCOMP>
            <IDENT id="_T"><QUAL>U8</QUAL><CVALUETYPE bl="8" enc="uns" sig="0" qty="atom"/></IDENT>
            <DIAGCLASS>
              <DIAGINST id="_I1"><QUAL>BatteryVoltage</QUAL>
                <SERVICE id="_S1" tmplref="_D22"><QUAL>Read</QUAL>
                  <SHORTCUTQUAL>BatteryVoltage_Read</SHORTCUTQUAL></SERVICE>
                <STATICVALUE shstaticref="_SH" v="61840"/>
                <SIMPLECOMPCONT><DATAOBJ dtref="_TV"><QUAL>Voltage</QUAL></DATAOBJ>
                  <SPECDATAOBJ><QUAL>NRC</QUAL></SPECDATAOBJ></SIMPLECOMPCONT>
              </DIAGINST>
              <DIAGINST id="_I2"><QUAL>Security</QUAL>
                <SERVICE id="_S2" tmplref="_D27"><QUAL>Seed</QUAL>
                  <SHORTCUTQUAL>Security_Seed</SHORTCUTQUAL></SERVICE>
                <STATICVALUE shstaticref="_SH" v="1"/>
              </DIAGINST>
              <DIAGINST id="_I3"><QUAL>Tester</QUAL>
                <SERVICE id="_S3" tmplref="_D3E" ><QUAL>Send</QUAL>
                  <SHORTCUTQUAL>Tester_Send</SHORTCUTQUAL></SERVICE>
              </DIAGINST>
              <DIAGINST id="_I4"><QUAL>Mystery</QUAL>
                <SERVICE id="_S4"><QUAL>Read</QUAL>
                  <SHORTCUTQUAL>Mystery_Read</SHORTCUTQUAL></SERVICE>
              </DIAGINST>
            </DIAGCLASS>
          </ECUDOC>
        </CANDELA>
        """;

    [Fact]
    public void ServiceIdsAndSubFunctionsComeFromTheRequestPdu()
    {
        var catalog = LoadText(Structured);
        var byName = catalog.ServiceDefinitions.ToDictionary(s => s.Name);

        var read = byName["BatteryVoltage"];
        Assert.Equal(0x22, read.ServiceId);
        Assert.Null(read.SubFunction);
        Assert.Equal(61840, read.Identifier);
        Assert.Equal(DiagnosticServiceIdSource.Structural, read.Source);

        var seed = byName["Security_Seed"];
        Assert.Equal(0x27, seed.ServiceId);
        Assert.Equal((byte)1, seed.SubFunction);
        Assert.Equal(DiagnosticServiceIdSource.Structural, seed.Source);

        var tester = byName["Tester_Send"];
        Assert.Equal(0x3E, tester.ServiceId);
        Assert.Equal((byte)1, tester.SubFunction);
        Assert.Equal(DiagnosticServiceIdSource.Structural, tester.Source);
    }

    [Fact]
    public void ServiceWithoutResolvablePduFallsBackToNameHeuristicAndSaysSo()
    {
        var mystery = LoadText(Structured).ServiceDefinitions.Single(s => s.Name == "Mystery");
        Assert.Equal(0x22, mystery.ServiceId); // "Read" in the name
        Assert.Equal(DiagnosticServiceIdSource.NameHeuristic, mystery.Source);
    }

    [Fact]
    public void StructuralSidWinsOverMisleadingName()
    {
        // The qualifier says "Read" but the request PDU says 0x2E.
        var xml = Structured.Replace("v=\"34\"", "v=\"46\"");
        var service = LoadText(xml).ServiceDefinitions.Single(s => s.Name == "BatteryVoltage");
        Assert.Equal(0x2E, service.ServiceId);
        Assert.Equal(DiagnosticServiceIdSource.Structural, service.Source);
    }

    [Fact]
    public void LoneScalarDataObjectStatesLengthTypeUnitAndLinearScaling()
    {
        var did = LoadText(Structured).Parameters.Single(p => p.Did == 61840);
        Assert.Equal(8, did.BitLength);
        Assert.Equal("UNSIGNED", did.DataType);
        Assert.Equal("V", did.Unit);
        Assert.Equal(0.1, did.Factor);
        Assert.Equal(0.0, did.Offset);
        Assert.Null(did.Minimum);
        Assert.Null(did.Maximum);
    }

    [Fact]
    public void MultipleDataObjectsDoNotInventWholeDidScaling()
    {
        var xml = Structured.Replace(
            "<SPECDATAOBJ><QUAL>NRC</QUAL></SPECDATAOBJ>",
            "<DATAOBJ dtref=\"_TV\"><QUAL>Second</QUAL></DATAOBJ>");
        var did = LoadText(xml).Parameters.Single(p => p.Did == 61840);
        Assert.Null(did.BitLength);
        Assert.Null(did.Factor);
        Assert.Equal(string.Empty, did.Unit);
    }

    [Fact]
    public void RealSamplesResolveEveryServiceStructurally()
    {
        var dir = FindRealSamples();
        if (dir is null) return; // Git-ignored real files are optional.
        var test = Path.Combine(dir, "Test.cdd");
        var bed = Path.Combine(dir, "kwp2000-bed.cdd");
        if (!File.Exists(test) || !File.Exists(bed)) return;

        // (SID, fixed sub-function) per service in document order, verified by hand
        // against the request PDUs of the files.
        (byte, byte?)[] expectedTest =
        [
            (0x10, 0x81), (0x1A, null), (0x3B, null), (0x1A, null),
            (0x18, 0x02), (0x18, 0x03), (0x17, null), (0x14, null),
            (0x28, 0x01), (0x29, 0x01), (0x3E, 0x01)
        ];
        (byte, byte?)[] expectedBed =
        [
            (0x10, 0xA0), (0x20, null), (0x1A, null), (0x1A, null), (0x1A, null),
            (0x3B, null), (0x27, 0x01), (0x27, 0x02), (0x18, 0x02), (0x18, 0x03),
            (0x14, null), (0x3B, null), (0x1A, null), (0x34, null), (0x36, null),
            (0x37, null), (0x28, null), (0x29, null), (0x11, 0x01), (0x3E, 0x01),
            (0x3E, 0x02)
        ];

        foreach (var (file, expected) in new[] { (test, expectedTest), (bed, expectedBed) })
        {
            var services = DidCatalog.Load(file).ServiceDefinitions;
            Assert.Equal(expected, services.Select(s => (s.ServiceId, s.SubFunction)).ToArray());
            Assert.All(services, s => Assert.Equal(DiagnosticServiceIdSource.Structural, s.Source));
        }
    }

    private static string? FindRealSamples()
    {
        var overridden = Environment.GetEnvironmentVariable("RBG_CDD_REAL_DIR");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "artifacts", "online-samples", "cdd-real");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static DidCatalog LoadText(string xml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rbg-cdd-struct-{Guid.NewGuid():N}.cdd");
        File.WriteAllText(path, xml);
        try { return DidCatalog.Load(path); }
        finally { File.Delete(path); }
    }
}
