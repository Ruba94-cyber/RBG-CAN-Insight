using RbgCanInsight.Dbc;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

/// <summary>Regressions found by running public third-party sample files (python-can, opendbc).</summary>
public sealed class OnlineSampleRegressionTests
{

    [Fact]
    public async Task Trc21HonorsDeclaredColumnsIncludingBusAndExtendedIds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cols-{Guid.NewGuid():N}.trc");
        await File.WriteAllLinesAsync(path,
        [
            ";$FILEVERSION=2.1",
            ";$COLUMNS=N,O,T,B,I,d,R,L,D",
            "      1      2501.000 DT 2      00C8 Tx -  8    09 08 07 06 05 04 03 02",
            "      2     17540.300 ST 1         - Rx -  4    00 00 00 08",
            "      3     17700.300 DT 1  00000100 Tx -  8    00 00 00 00 00 00 00 00"
        ]);
        try
        {
            var frames = new List<CanFrame>();
            await foreach (var frame in TraceImporter.ReadAsync(path))
                frames.Add(frame);
            Assert.Equal(2, frames.Count);
            Assert.Equal(0xC8u, frames[0].Identifier);
            Assert.Equal(2, frames[0].Channel);
            Assert.False(frames[0].Flags.HasFlag(CanFrameFlags.Extended));
            Assert.Equal(8, frames[0].DataLength);
            Assert.Equal(0x100u, frames[1].Identifier);
            Assert.True(frames[1].Flags.HasFlag(CanFrameFlags.Extended));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DbcNewSymbolsBlockIsNotTreatedAsUnterminatedStatements()
    {
        const string dbc = "VERSION \"\"\n\n\nNS_ : \n\tNS_DESC_\n\tCM_\n\tBA_DEF_\n\tBA_\n\tVAL_\n\nBS_:\n\nBU_: ECM\n\n" +
                           "BO_ 256 Msg: 8 ECM\n SG_ Sig : 0|8@1+ (1,0) [0|255] \"\" Vector__XXX\n";
        var database = new DbcParser().Parse("t", new StringReader(dbc));
        Assert.Empty(database.ParseWarnings);
        Assert.Single(database.Messages);
    }
}

public sealed class RealCandelaStructureTests
{
    [Fact]
    public void ReadAndWriteServicesOfOneDiagInstShareItsDidAndCommonServicesAreRecognised()
    {
        // Shape taken from real Vector CANdela documents (CANoe sample configurations):
        // [SERVICE Read][SERVICE Write][STATICVALUE did] inside one DIAGINST.
        const string cdd = """
            <CANDELA><ECUDOC><ECU qual="Real">
              <DIAGINST><QUAL>SerialNumber</QUAL>
                <SERVICE><SHORTCUTQUAL>SerialNumber_Read</SHORTCUTQUAL></SERVICE>
                <SERVICE><SHORTCUTQUAL>SerialNumber_Write</SHORTCUTQUAL></SERVICE>
                <STATICVALUE v="146"/>
              </DIAGINST>
              <DIAGINST>
                <SERVICE><SHORTCUTQUAL>FaultMemory_Clear</SHORTCUTQUAL></SERVICE>
                <SERVICE><SHORTCUTQUAL>TesterPresent_SendResponseRequired</SHORTCUTQUAL></SERVICE>
                <SERVICE><SHORTCUTQUAL>NormalMessageTransmission_DisableResponseRequired</SHORTCUTQUAL></SERVICE>
              </DIAGINST>
            </ECU></ECUDOC></CANDELA>
            """;
        var path = Path.Combine(Path.GetTempPath(), $"rbg-real-cdd-{Guid.NewGuid():N}.cdd");
        File.WriteAllText(path, cdd);
        try
        {
            var catalog = RbgCanInsight.DiagData.DidCatalog.Load(path);
            var did = Assert.Single(catalog.Parameters);
            Assert.Equal(146, did.Did);
            var services = catalog.ServiceDefinitions;
            Assert.Equal(5, services.Count);
            Assert.Equal(0x14, services.Single(s => s.Name.StartsWith("FaultMemory_Clear")).ServiceId);
            Assert.Contains(services, s => s.ServiceId == 0x3E);
            Assert.Contains(services, s => s.ServiceId == 0x28);
            Assert.Equal(2, services.Count(s => s.Identifier == 146));
        }
        finally { File.Delete(path); }
    }
}

public sealed class RealWorldFormatTests
{
    [Fact]
    public async Task UnfinalizedMf4WithPlaceholderDataLengthImportsEveryRecord()
    {
        // CSS Electronics CANedge raw logs are "UnFinMF" files whose last ##DT block has length 24 (header only).
        var path = Path.Combine(Path.GetTempPath(), $"unfin-{Guid.NewGuid():N}.mf4");
        var frames = Enumerable.Range(0, 25).Select(i => new RbgCanInsight.Domain.CanFrame(
            1, (uint)(0x100 + i), new byte[] { (byte)i, 2, 3, 4, 5, 6, 7, 8 }, i * 1_000_000L)).ToList();
        await RbgCanInsight.TraceIO.Mdf4Writer.WriteAsync(path, frames);
        var bytes = await File.ReadAllBytesAsync(path);
        "UnFinMF "u8.CopyTo(bytes);
        // Find the last ##DT block and turn its length into the unfinalized placeholder (24).
        var dt = -1;
        for (var i = 0; i + 4 <= bytes.Length; i += 8)
            if (bytes[i] == '#' && bytes[i + 1] == '#' && bytes[i + 2] == 'D' && bytes[i + 3] == 'T') dt = i;
        Assert.True(dt > 0);
        BitConverter.GetBytes(24L).CopyTo(bytes, dt + 8);
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            var read = new List<RbgCanInsight.Domain.CanFrame>();
            await foreach (var f in RbgCanInsight.TraceIO.TraceImporter.ReadAsync(path)) read.Add(f);
            Assert.Equal(25, read.Count);
            Assert.Equal(0x100u + 24, read[^1].Identifier);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CandumpTerminalFormatWithBracketedLengthAndRelativeTimeImports()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cd-{Guid.NewGuid():N}.log");
        await File.WriteAllLinesAsync(path,
        [
            " (000.000000)  can0  18FCF200   [8]  E1 FF FF FF FF FF FF FF",
            " (000.005001)  can0  123   [2]  AA BB",
            " (000.006000)  can0  123   [3]  AA BB"          // length disagrees with the bytes: rejected, never padded
        ]);
        try
        {
            var read = new List<RbgCanInsight.Domain.CanFrame>();
            await foreach (var f in RbgCanInsight.TraceIO.TraceImporter.ReadAsync(path)) read.Add(f);
            Assert.Equal(2, read.Count);
            Assert.True(read[0].Flags.HasFlag(RbgCanInsight.Domain.CanFrameFlags.Extended));
            Assert.False(read[1].Flags.HasFlag(RbgCanInsight.Domain.CanFrameFlags.Extended));
            Assert.Equal(5_001_000L, read[1].TimestampNanoseconds);
        }
        finally { File.Delete(path); }
    }
}
