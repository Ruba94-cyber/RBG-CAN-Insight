using RbgCanInsight.Diagnostics;
using RbgCanInsight.DiagData;
using RbgCanInsight.Domain;
using RbgCanInsight.TraceVerify;
using Xunit;

namespace RbgCanInsight.Tests;

public sealed class UdsFieldEvidenceDumpTests
{
    private static IReadOnlyList<DiagnosticTransaction> SourceBackedTransactions()
    {
        var field = new DiagnosticFieldDefinition("Temperature", "Temperature",
            "", 0, 8, "A_UINT8", "degC", null, null, 0.5, -40,
            false, "BIG_ENDIAN");
        var catalog = new DidCatalog("synthetic", "ECM", "synthetic.odx",
            [new DidParameter(0xF405, "TemperatureDid", "TemperatureDid",
                "", "degC", null, null, 0.5, -40, "A_UINT8", 8,
                "ECM", "synthetic.odx", [field])],
            Connections:
            [
                new DiagnosticConnection("ECM", 0x7E0, 0x7E8, false,
                    "normal", "ECM", "synthetic.odx")
            ]);
        var frames = new[]
        {
            new CanFrame(0, 0x7E0,
                [0x03, 0x22, 0xF4, 0x05, 0, 0, 0, 0], 0),
            new CanFrame(0, 0x7E8,
                [0x04, 0x62, 0xF4, 0x05, 0x28, 0, 0, 0], 1_000_000)
        };
        return UdsTransactionAnalyzer.Analyze(frames, [catalog]);
    }

    [Fact]
    public void SourceBackedDumpIncludesExactCatalogRawCodeAndOriginalCanSpan()
    {
        var transactions = SourceBackedTransactions();
        using var output = new StringWriter();
        var counts = UdsFieldEvidenceDump.Write(output,
            transactions, new string('A', 64));

        Assert.Equal(1, counts.Transactions);
        Assert.Equal(1, counts.SourceBackedRows);
        Assert.Equal(0, counts.UnverifiedRows);
        var lines = output.ToString().Trim().Split('\n');
        Assert.Equal(UdsFieldEvidenceDump.CsvHeader, lines[0].TrimEnd('\r'));
        var cells = lines[1].TrimEnd('\r').Split(',');
        Assert.Equal(20, cells.Length);
        Assert.Equal("1", cells[0]);       // one-based transaction sequence
        Assert.Equal("0", cells[1]);       // zero-based CAN request index
        Assert.Equal("1", cells[2]);       // zero-based CAN response index
        Assert.Equal("0", cells[3]);       // physical channel
        Assert.Equal("2016", cells[4]);    // request 0x7E0
        Assert.Equal("2024", cells[5]);    // response 0x7E8
        Assert.Equal(new string('A', 64), cells[7]);
        Assert.Equal("Confirmed", cells[8]);
        Assert.Equal("ECM", cells[9]);
        Assert.Equal("34", cells[10]);     // UDS 0x22
        Assert.Equal("62469", cells[11]);  // DID 0xF405
        Assert.Equal("24", cells[13]);
        Assert.Equal("8", cells[14]);
        Assert.Equal("0x28", cells[15]);   // original raw coded value
        Assert.Equal("-20", cells[16]);    // must be parseable, never "'-20"
        Assert.Equal("degC", cells[17]);
        Assert.Equal("1", cells[18]);
        Assert.Equal("1:4:3:1", cells[19]); // frame, CAN byte, UDS byte, count
    }

    [Fact]
    public void UnverifiedSourceAndNonPositiveTransactionsCannotBecomeAuditRows()
    {
        var original = Assert.Single(SourceBackedTransactions());
        var value = Assert.Single(original.Decoded.Values);
        var incomplete = original with
        {
            Sequence = 2,
            Decoded = original.Decoded with
            {
                Values = [value with { SourceSpans = null, NumericValue = null }]
            }
        };
        var negative = original with
        {
            Sequence = 3, Outcome = DiagnosticOutcome.Negative
        };
        using var output = new StringWriter();
        var counts = UdsFieldEvidenceDump.Write(output,
            [original, incomplete, negative], new string('B', 64));
        Assert.Equal(1, counts.SourceBackedRows);
        Assert.Equal(2, counts.UnverifiedRows);
        Assert.Equal(2, output.ToString().Count(character => character == '\n'));
    }

    [Theory]
    [InlineData("=HYPERLINK(1,2)")]
    [InlineData("+SUM(1,2)")]
    [InlineData("+20")]
    [InlineData("-cmd|/C echo unsafe")]
    [InlineData("@SUM(1,2)")]
    public void FormulaLikeRawValuesRemainInertText(string raw)
    {
        var original = Assert.Single(SourceBackedTransactions());
        var value = Assert.Single(original.Decoded.Values);
        var result = original with
        {
            Decoded = original.Decoded with
            {
                Values = [value with { Raw = raw }]
            }
        };
        using var writer = new StringWriter();
        UdsFieldEvidenceDump.Write(writer, [result], new string('C', 64));
        Assert.Contains("'" + raw, writer.ToString());
    }

    [Fact]
    public void StrictSourceCoverageMustBeNonemptyAndHaveNoUnverifiedDidFields()
    {
        Assert.False(UdsFieldEvidenceDump.HasCompleteVerifiedCoverage(null));
        Assert.False(UdsFieldEvidenceDump.HasCompleteVerifiedCoverage(
            new UdsFieldEvidenceDump.Result(0, 0, 0)));
        Assert.False(UdsFieldEvidenceDump.HasCompleteVerifiedCoverage(
            new UdsFieldEvidenceDump.Result(0, 0, 5)));
        Assert.False(UdsFieldEvidenceDump.HasCompleteVerifiedCoverage(
            new UdsFieldEvidenceDump.Result(10, 1, 6)));
        Assert.True(UdsFieldEvidenceDump.HasCompleteVerifiedCoverage(
            new UdsFieldEvidenceDump.Result(10, 0, 6)));
        Assert.False(UdsFieldEvidenceDump.HasCompleteVerifiedCoverage(
            new UdsFieldEvidenceDump.Result(10, 0, 6, UnresolvedDidResponses: 1)));
    }

    [Fact]
    public void StrictGateRejectsPartialDidResponseEvenWhenFirstDecodedFieldIsValid()
    {
        var original = Assert.Single(SourceBackedTransactions());
        var unresolved = original with
        {
            Decoded = original.Decoded with { DidPayloadFullyDecoded = false }
        };
        using var output = new StringWriter();
        var result = UdsFieldEvidenceDump.Write(output, [original, unresolved],
            new string('A', 64));
        Assert.Equal(2, result.SourceBackedRows);
        Assert.Equal(0, result.UnverifiedRows);
        Assert.Equal(1, result.UnresolvedDidResponses);
        Assert.False(UdsFieldEvidenceDump.HasCompleteVerifiedCoverage(result));
    }

    [Fact]
    public void MissingOrMalformedCatalogIdentityFailsClosed()
    {
        using var output = new StringWriter();
        Assert.Throws<ArgumentException>(() =>
            UdsFieldEvidenceDump.Write(output, SourceBackedTransactions(), "not-a-SHA"));
    }

    [Fact]
    public void UntrustedFieldNameCannotExecuteAsSpreadsheetFormula()
    {
        var original = Assert.Single(SourceBackedTransactions());
        var value = Assert.Single(original.Decoded.Values);
        var injected = original with
        {
            Decoded = original.Decoded with
            {
                Values = [value with
                {
                    Path = "=HYPERLINK(\"https://invalid.example\",\"x\")"
                }]
            }
        };
        using var output = new StringWriter();
        UdsFieldEvidenceDump.Write(output, [injected], new string('C', 64));
        var csv = output.ToString();
        Assert.Contains("'=HYPERLINK", csv);
        Assert.DoesNotContain(",=HYPERLINK", csv);
    }
}
