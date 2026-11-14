using RbgCanInsight.Diagnostics;
using RbgCanInsight.DiagData;
using RbgCanInsight.Domain;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class KwpServiceDecodingTests
{
    private static DidCatalog Catalog(params (byte Sid, int? Id, string Name)[] services) =>
        new("kwp", "ECU", "synthetic.cdd", [],
            Services: services.Select(s => new DiagnosticServiceDefinition(
                s.Sid, null, s.Id, s.Name, s.Name, "", "ECU", "synthetic.cdd",
                DiagnosticServiceIdSource.Structural)).ToArray());

    private static CanFrame Frame(uint id, byte[] data, long ts) => new(0, id, data, ts, CanFrameFlags.None);

    [Fact]
    public void KwpCatalogIsDetectedFromKwpOnlyServicesAndNotFromSharedOnes()
    {
        Assert.True(KwpServiceDecoder.IsKwpCatalogSet([Catalog((0x1A, 0x80, "Ident"), (0x3E, null, "TP"))]));
        Assert.False(KwpServiceDecoder.IsKwpCatalogSet([Catalog((0x22, 0xF190, "VIN"), (0x3E, null, "TP"))]));
        // A UDS-only service rules KWP2000 out, even when a KWP-only SID is present too.
        Assert.False(KwpServiceDecoder.IsKwpCatalogSet([Catalog((0x1A, 0x80, "Ident"), (0x19, null, "DTC"))]));
        Assert.False(KwpServiceDecoder.IsKwpCatalogSet([]));
    }

    [Fact]
    public void ReadDataByLocalIdentifierIsPairedAndNamedFromTheCatalog()
    {
        var catalog = Catalog((0x21, 0x07, "EngineSpeed"));
        var frames = new[]
        {
            Frame(0x7E0, [0x02, 0x21, 0x07], 0),
            Frame(0x7E8, [0x04, 0x61, 0x07, 0x12, 0x34], 1_000_000)
        };

        var tx = Assert.Single(UdsTransactionAnalyzer.Analyze(frames, [catalog]));

        Assert.Equal(0x21, tx.Decoded.ServiceId);
        Assert.Equal("EngineSpeed", tx.Decoded.ServiceName);
        Assert.Equal(DiagnosticOutcome.Positive, tx.Outcome);
        Assert.Contains("local ID 0x07", tx.Decoded.RequestSummary);
        Assert.Contains("EngineSpeed", tx.Decoded.RequestSummary);
        Assert.Contains("12 34", tx.Decoded.ResponseSummary);
    }

    [Fact]
    public void WithoutAKwpCatalogKwpOnlyServicesStayUnrecognised()
    {
        var frames = new[]
        {
            Frame(0x7E0, [0x02, 0x21, 0x07], 0),
            Frame(0x7E8, [0x04, 0x61, 0x07, 0x12, 0x34], 1_000_000)
        };
        Assert.Empty(UdsTransactionAnalyzer.Analyze(frames, [Catalog((0x22, 0xF190, "VIN"))]));
    }

    [Fact]
    public void KwpNegativeResponseIsAttributedToTheKwpService()
    {
        var catalog = Catalog((0x3B, 0x01, "WriteCoding"));
        var frames = new[]
        {
            Frame(0x7E0, [0x03, 0x3B, 0x01, 0xAA], 0),
            Frame(0x7E8, [0x03, 0x7F, 0x3B, 0x33], 1_000_000)
        };
        var tx = Assert.Single(UdsTransactionAnalyzer.Analyze(frames, [catalog]));
        Assert.Equal(DiagnosticOutcome.Negative, tx.Outcome);
        Assert.Equal((byte?)0x33, tx.Decoded.NegativeResponseCode);
        Assert.Equal("WriteCoding", tx.Decoded.ServiceName);
    }

    [Fact]
    public void RealKwpSampleFileSelectsKwpProfileAndNamesItsServices()
    {
        var path = FindReal("kwp2000-bed.cdd");
        if (path is null) return; // git-ignored real file is optional
        var catalog = DidCatalog.Load(path);
        Assert.True(KwpServiceDecoder.IsKwpCatalogSet([catalog]));
        // Every one of the 21 services must be a KWP2000 service ID.
        Assert.All(catalog.ServiceDefinitions, s => Assert.True(KwpServiceDecoder.IsKnownRequest(s.ServiceId)));
    }

    private static string? FindReal(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "artifacts", "online-samples", "cdd-real", name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
