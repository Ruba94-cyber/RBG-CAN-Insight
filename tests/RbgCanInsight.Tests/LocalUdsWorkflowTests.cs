using RbgCanInsight.Diagnostics;
using RbgCanInsight.DiagData;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceIO;
using Xunit;
using Xunit.Abstractions;

namespace RbgCanInsight.Tests;

/// <summary>Opt-in end-to-end proof against local OEM files. CI remains self-contained; setting
/// both environment variables turns this into a strict import/catalog/transaction validation.</summary>
public sealed class LocalUdsWorkflowTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LoadsRealTraceAndDiagnosticCatalogWhenConfigured()
    {
        var tracePath = Environment.GetEnvironmentVariable("RBG_CAN_LOCAL_UDS_TRACE");
        var catalogPath = Environment.GetEnvironmentVariable("RBG_CAN_LOCAL_UDS_CATALOG");
        if (string.IsNullOrWhiteSpace(tracePath) || string.IsNullOrWhiteSpace(catalogPath))
        {
            output.WriteLine("Local OEM validation not configured; set RBG_CAN_LOCAL_UDS_TRACE and RBG_CAN_LOCAL_UDS_CATALOG.");
            return;
        }

        Assert.True(File.Exists(tracePath), $"Trace not found: {tracePath}");
        Assert.True(File.Exists(catalogPath), $"Catalog not found: {catalogPath}");

        var frames = new List<CanFrame>();
        await foreach (var frame in TraceImporter.ReadAsync(tracePath))
        {
            frames.Add(frame);
        }
        frames.Sort(static (left, right) => left.TimestampNanoseconds.CompareTo(right.TimestampNanoseconds));
        var catalog = DidCatalog.Load(catalogPath);
        var transactions = UdsTransactionAnalyzer.Analyze(frames, [catalog]);
        var positive = transactions.Count(transaction => transaction.Outcome == DiagnosticOutcome.Positive);
        var negative = transactions.Count(transaction => transaction.Outcome == DiagnosticOutcome.Negative);
        var noResponse = transactions.Count(transaction => transaction.Outcome == DiagnosticOutcome.NoResponse);
        var serviceSummary = string.Join(", ", transactions
            .GroupBy(transaction => transaction.Decoded.ServiceId)
            .OrderByDescending(group => group.Count())
            .Take(12)
            .Select(group => $"0x{group.Key:X2}={group.Count()}"));

        output.WriteLine($"frames={frames.Count}; dids={catalog.Parameters.Count}; services={catalog.ServiceDefinitions.Count}; " +
                         $"dtcs={catalog.DtcDefinitions.Count}; routines={catalog.RoutineDefinitions.Count}; transactions={transactions.Count}; " +
                         $"positive={positive}; negative={negative}; noResponse={noResponse}; bySid=[{serviceSummary}]");
        Assert.NotEmpty(frames);
        Assert.True(catalog.Parameters.Count > 0 || catalog.ServiceDefinitions.Count > 0,
            "The local diagnostic catalog produced no usable definitions.");
        Assert.NotEmpty(transactions);
        Assert.Contains(transactions, transaction => transaction.Outcome is DiagnosticOutcome.Positive or DiagnosticOutcome.Negative);
        Assert.Contains(transactions, transaction => transaction.Decoded.ServiceId == 0x19);
        Assert.Contains(transactions, transaction => transaction.Decoded.ServiceId == 0x19 && transaction.Decoded.Dtcs.Count > 0);
    }
}
