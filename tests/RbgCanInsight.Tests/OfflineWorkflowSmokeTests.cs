using RbgCanInsight.Analysis;
using RbgCanInsight.Correlation;
using RbgCanInsight.Dbc;
using RbgCanInsight.DiagData;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class OfflineWorkflowSmokeTests
{
    [Fact]
    public async Task SyntheticOfflineWorkflowImportsDecodesAnalyzesMapsAndExports()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"rbg-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var tracePath = Path.Combine(workDir, "synthetic-drive.asc");
            var dbcPath = Path.Combine(workDir, "synthetic-network.dbc");
            var cddPath = Path.Combine(workDir, "synthetic-diagnostics.cdd");
            var mapPath = Path.Combine(workDir, "did-can-map.rbgmap.json");
            var csvPath = Path.Combine(workDir, "change-matrix.csv");
            var xlsxPath = Path.Combine(workDir, "change-matrix.xlsx");

            await File.WriteAllTextAsync(tracePath,
                """
                date Mon Jul 06 10:00:00.000 2026
                base hex  timestamps absolute
                internal events logged
                0.000000 1 100 Rx d 8 32 20 00 00 00 00 00 00
                0.100000 1 100 Rx d 8 37 28 00 00 00 00 00 00
                0.200000 1 100 Rx d 8 3C 30 00 00 00 00 00 00
                0.300000 1 200 Rx d 8 88 13 00 00 00 00 00 00
                """);

            await File.WriteAllTextAsync(dbcPath,
                """
                VERSION ""

                BO_ 256 EngineData: 8 ECM
                 SG_ EngCoolantTemp : 0|8@1+ (1,-40) [-40|215] "degC" Vector__XXX
                 SG_ EngineLoad : 8|8@1+ (0.5,0) [0|100] "%" Vector__XXX

                BO_ 512 VehicleData: 8 BCM
                 SG_ VehicleSpeed : 0|16@1+ (0.01,0) [0|250] "km/h" Vector__XXX
                """);

            await File.WriteAllTextAsync(cddPath,
                """
                <?xml version="1.0" encoding="utf-8"?>
                <CANDELA>
                  <ECU qual="ECM">
                    <DIAGCLASS>
                      <SERVICE>
                        <NAME><TUV>Engine coolant temperature</TUV></NAME>
                        <SHORTCUTQUAL>EngineCoolantTemperature_Read</SHORTCUTQUAL>
                        <SEMANTIC>CURRENTDATA</SEMANTIC>
                      </SERVICE>
                      <STATICVALUE v="62469" />
                    </DIAGCLASS>
                  </ECU>
                </CANDELA>
                """);

            var frames = new List<CanFrame>();
            await foreach (var frame in TraceImporter.ReadAsync(tracePath))
            {
                frames.Add(frame);
            }

            var database = new DbcParser().ParseFile(dbcPath);
            var timelines = SignalTimelineBuilder.Build(frames, [database]);
            var seenIds = frames.Select(frame => frame.Identifier).ToHashSet();
            var reports = SignalChangeAnalyzer.Analyze(timelines, [database], seenIds);
            var coolant = Assert.Single(reports, report => report.SignalName == "EngCoolantTemp");

            Assert.Equal(4, frames.Count);
            Assert.Equal(SignalActivity.Variable, coolant.Activity);
            Assert.True(coolant.ChangeCount >= 2);
            Assert.Contains(coolant.Changes, change => change.FromValue == 10 && change.ToValue == 15);

            var catalog = DidCatalog.Load(cddPath);
            var did = Assert.Single(catalog.Parameters);
            var candidates = DidSignalCorrelator.Correlate(
                did,
                [database],
                reports
                    .GroupBy(report => $"{report.Identifier}:{report.SignalName}")
                    .ToDictionary(group => group.Key, group => group.First()));

            Assert.NotEmpty(candidates);
            Assert.Equal("EngCoolantTemp", candidates[0].Signal.Name);

            var document = new MappingDocument(
                "Synthetic smoke DID map",
                DateTimeOffset.UtcNow,
                [
                    new DidSignalMapping(
                        did.Did,
                        did.Name,
                        did.Ecu,
                        candidates[0].Database,
                        candidates[0].Message.Name,
                        candidates[0].Signal.Name,
                        candidates[0].Confidence,
                        candidates[0].ReasonText,
                        MappingStatus.Confirmed)
                ]);
            document.Save(mapPath);
            Assert.Equal("EngCoolantTemp", MappingDocument.Load(mapPath).Mappings[0].Signal);

            var columns = new List<ExportColumn>
            {
                new("Signal", [coolant.SignalName]),
                new("Activity", [coolant.Activity.ToString()]),
                new("Changes", [coolant.ChangeCount.ToString()]),
                new("Amplitude", [coolant.Amplitude.ToString("G")])
            };
            TabularExport.WriteCsv(csvPath, columns);
            TabularExport.WriteXlsx(xlsxPath, "Change Matrix", columns);

            Assert.True(new FileInfo(csvPath).Length > 0);
            Assert.True(new FileInfo(xlsxPath).Length > 0);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
