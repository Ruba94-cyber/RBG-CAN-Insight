using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Real WPF/UIA acceptance of source-frame navigation for a CDD/ODX
/// decoded DID field; uses only local synthetic trace and description.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiUdsDecodedFieldSourceTests
{
    private const string Odx = """
        <ODX>
          <DIAG-LAYER-CONTAINER><SHORT-NAME>ECM</SHORT-NAME></DIAG-LAYER-CONTAINER>
          <ECU-VARIANT><SHORT-NAME>ECM</SHORT-NAME></ECU-VARIANT>
          <REQUEST ID="REQ_F405">
            <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE>
              <DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
            <PARAM SEMANTIC="ID"><CODED-VALUE>62469</CODED-VALUE>
              <DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
          </REQUEST>
          <POS-RESPONSE ID="RESP_F405">
            <PARAM SEMANTIC="DATA"><BYTE-POSITION>3</BYTE-POSITION>
              <DOP-REF ID-REF="DOP_TEMP"/></PARAM>
          </POS-RESPONSE>
          <DATA-OBJECT-PROP ID="DOP_TEMP">
            <SHORT-NAME>EngineCoolantTemperature</SHORT-NAME>
            <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT8">
              <BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
            <PHYSICAL-TYPE BASE-DATA-TYPE="A_FLOAT64" />
            <COMPU-METHOD><COMPU-INTERNAL-TO-PHYS><COMPU-SCALES>
              <COMPU-SCALE><COMPU-RATIONAL-COEFFS>
                <COMPU-NUMERATOR><V>-40</V><V>1</V></COMPU-NUMERATOR>
              </COMPU-RATIONAL-COEFFS></COMPU-SCALE>
            </COMPU-SCALES></COMPU-INTERNAL-TO-PHYS></COMPU-METHOD>
          </DATA-OBJECT-PROP>
          <DIAG-SERVICE>
            <SHORT-NAME>Read_EngineCoolantTemperature</SHORT-NAME>
            <REQUEST-REF ID-REF="REQ_F405" />
            <POS-RESPONSE-REF ID-REF="RESP_F405" />
          </DIAG-SERVICE>
          <COMPARAM><SHORT-NAME>CP_REQUEST_ID</SHORT-NAME><VALUE>2016</VALUE></COMPARAM>
          <COMPARAM><SHORT-NAME>CP_RESPONSE_ID</SHORT-NAME><VALUE>2024</VALUE></COMPARAM>
        </ODX>
        """;

    private const string Trace = """
        date Tue Jul 07 10:00:00.000 2026
        base hex  timestamps absolute
        internal events logged
        0.010000 1 7E0 Rx d 8 03 22 F4 05 00 00 00 00
        0.020000 1 7E8 Rx d 8 04 62 F4 05 32 00 00 00
        """;

    [Fact]
    public void AFieldSpanningFirstAndConsecutiveFramesLetsUserChooseEitherSource()
    {
        using var ui = new GuiSession();
        var wideOdx = Odx.Replace(
            "BASE-DATA-TYPE=\"A_UINT8\"",
            "BASE-DATA-TYPE=\"A_UINT64\"",
            StringComparison.Ordinal);
        // Find the DOP's own coded length, not the two 8-bit protocol
        // header lengths. Whitespace is intentionally irrelevant.
        var dopOffset = wideOdx.IndexOf("BASE-DATA-TYPE=\"A_UINT64\"",
            StringComparison.Ordinal);
        Assert.True(dopOffset >= 0);
        const string oldLength = "<BIT-LENGTH>8</BIT-LENGTH>";
        var lengthOffset = wideOdx.IndexOf(oldLength, dopOffset,
            StringComparison.Ordinal);
        Assert.True(lengthOffset >= 0);
        wideOdx = wideOdx[..lengthOffset] + "<BIT-LENGTH>64</BIT-LENGTH>" +
                  wideOdx[(lengthOffset + oldLength.Length)..];
        const string multiFrameTrace = """
            date Tue Jul 07 10:00:00.000 2026
            base hex  timestamps absolute
            internal events logged
            0.010000 1 7E0 Rx d 8 03 22 F4 05 00 00 00 00
            0.020000 1 7E8 Rx d 8 10 0B 62 F4 05 00 00 00
            0.030000 1 7E8 Rx d 8 21 00 00 00 00 28 00 00
            """;
        Assert.Contains("<BIT-LENGTH>64</BIT-LENGTH>", wideOdx);
        File.WriteAllText(ui.OdxPath, wideOdx);
        File.WriteAllText(ui.TracePath, multiFrameTrace);

        ui.Navigate("nav.mapping");
        ui.Click("mapping.loadCatalog");
        ui.ChooseFile(ui.OdxPath);
        ui.AwaitMappingStatus("from 1 file");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.uds");
        ui.Click("uds.viewTransactions");
        ui.Until(() => ui.Visible("uds.transactionsGrid").FindFirstDescendant(
            cf => cf.ByControlType(ControlType.DataItem)),
            "multi-frame response transaction").Press();
        ui.Until(() => ui.Visible("uds.decodedFields").FindFirstDescendant(
            cf => cf.ByControlType(ControlType.ListItem)),
            "wide DID source field").Press();

        var selector = ui.Visible("uds.fieldSourceFrames").AsComboBox();
        Assert.Equal(2, selector.Items.Length);
        selector.Select(1); // choose the real Consecutive Frame, not FF.
        Assert.True(ui.Visible("uds.openFieldSource").IsEnabled);
        ui.Click("uds.openFieldSource");
        Assert.False(ui.Visible("trace.grid").IsOffscreen);
        ui.Until(() => ui.Visible("trace.navigationStatus").Name.Contains(
            "frame 3 / 3", StringComparison.Ordinal)
            ? "consecutive frame #3 actually selected" : null,
            "open selected CF instead of merely switching to Trace");
        ui.Until(() => ui.Visible("trace.sourceByteEvidence").Name.Contains(
            "B1..B5", StringComparison.Ordinal)
            ? "actual CF bytes are highlighted and explicitly named" : null,
            "selected CF original payload range is visible");
        ui.Until(() => ui.Visible("trace.grid").FindFirstDescendant(
            cf => cf.ByName("Verified DID source byte B1")),
            "source byte B1 has an accessible highlight in raw CAN detail");
        Assert.Equal(multiFrameTrace, File.ReadAllText(ui.TracePath));
        Assert.Equal(wideOdx, File.ReadAllText(ui.OdxPath));
    }

    [Fact]
    public void IncompleteSecondDidIsWarnedWhileVerifiedFirstDidKeepsItsSourceLink()
    {
        using var ui = new GuiSession();
        File.WriteAllText(ui.OdxPath, Odx);
        // Request two distinct DIDs, but the positive response carries
        // only one. The first one is independently source-backed.
        var partialTrace = Trace.Replace(
            "03 22 F4 05 00 00 00 00",
            "05 22 F4 05 F4 06 00 00",
            StringComparison.Ordinal);
        Assert.NotEqual(Trace, partialTrace);
        File.WriteAllText(ui.TracePath, partialTrace);

        ui.Navigate("nav.mapping");
        ui.Click("mapping.loadCatalog");
        ui.ChooseFile(ui.OdxPath);
        ui.AwaitMappingStatus("from 1 file");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        ui.Navigate("nav.uds");
        ui.Click("uds.viewTransactions");

        var row = ui.Until(() => ui.Visible("uds.transactionsGrid")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.DataItem)),
            "one partially decoded positive UDS response");
        row.Press();
        ui.Until(() => ui.Visible("uds.partialDidWarning").Name.Contains(
            "Partial DID layout", StringComparison.Ordinal)
            ? "incomplete DID warning visible and accessible" : null,
            "partially decoded positive response must not look complete");
        ui.Until(() => ui.Visible("uds.decodedFields")
            .FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
            "first independently source-backed DID").Press();
        Assert.True(ui.Visible("uds.openFieldSource").IsEnabled);
        ui.Click("uds.openFieldSource");
        ui.Until(() => ui.Visible("trace.navigationStatus").Name.Contains(
            "frame 2 / 2", StringComparison.Ordinal)
            ? "verified first DID raw response selected" : null,
            "partial-DID warning must not block correct raw source navigation");
        Assert.Equal(partialTrace, File.ReadAllText(ui.TracePath));
    }

    [Fact]
    public void LoadingDifferentCaptureWithSameFrameNumbersClearsOldDidSourceHighlight()
    {
        using var ui = new GuiSession();
        File.WriteAllText(ui.OdxPath, Odx);
        File.WriteAllText(ui.TracePath, Trace);

        ui.Navigate("nav.mapping");
        ui.Click("mapping.loadCatalog");
        ui.ChooseFile(ui.OdxPath);
        ui.AwaitMappingStatus("from 1 file");
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.uds");
        ui.Click("uds.viewTransactions");
        ui.Until(() => ui.Visible("uds.transactionsGrid").FindFirstDescendant(
            cf => cf.ByControlType(ControlType.DataItem)),
            "first trace UDS transaction").Press();
        ui.Until(() => ui.Visible("uds.decodedFields").FindFirstDescendant(
            cf => cf.ByControlType(ControlType.ListItem)),
            "first trace original DID field").Press();
        ui.Click("uds.openFieldSource");
        ui.Until(() => ui.Visible("trace.sourceByteEvidence").Name.Contains(
            "byte B4", StringComparison.Ordinal)
            ? "initial source byte overlay" : null,
            "show the original first trace byte");

        // Reuse the exact same path and CAN IDs with different immutable
        // raw data. A matching frame index alone must not be enough to
        // transfer the previous analysis/source badge to the new trace.
        var replacement = Trace.Replace(
            "04 62 F4 05 32 00 00 00",
            "04 62 F4 05 64 00 00 00", StringComparison.Ordinal);
        Assert.NotEqual(Trace, replacement);
        File.WriteAllText(ui.TracePath, replacement);
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        ui.Until(() =>
        {
            var source = ui.Main.FindFirstDescendant(
                cf => cf.ByAutomationId("trace.sourceByteEvidence"));
            return source is null || source.IsOffscreen ||
                   string.IsNullOrEmpty(source.Name)
                ? "old raw source-byte overlay cleared" : null;
        }, "replacing the trace must remove the old DID source badge");

        ui.Navigate("nav.uds");
        ui.Click("uds.viewTransactions");
        ui.Until(() => ui.Visible("uds.transactionsGrid").FindFirstDescendant(
            cf => cf.ByControlType(ControlType.DataItem)),
            "new trace UDS transaction").Press();
        ui.Until(() => ui.Visible("uds.decodedFields").FindFirstDescendant(
            cf => cf.ByControlType(ControlType.ListItem)),
            "new trace source-backed DID field").Press();
        Assert.True(ui.Visible("uds.openFieldSource").IsEnabled);
        ui.Click("uds.openFieldSource");
        ui.Until(() => ui.Visible("trace.sourceByteEvidence").Name.Contains(
            "frame #2", StringComparison.Ordinal)
            ? "new capture source navigation is available" : null,
            "only the new trace analysis may link to original CAN bytes");
        Assert.Equal(replacement, File.ReadAllText(ui.TracePath));
        Assert.Equal(Odx, File.ReadAllText(ui.OdxPath));
    }

    [Fact]
    public void OneClickOnVerifiedDidFieldOpensItsOriginalRawCanResponse()
    {
        using var ui = new GuiSession();
        File.WriteAllText(ui.OdxPath, Odx);
        File.WriteAllText(ui.TracePath, Trace);

        ui.Navigate("nav.mapping");
        ui.Click("mapping.loadCatalog");
        ui.ChooseFile(ui.OdxPath);
        ui.AwaitMappingStatus("from 1 file");

        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.uds");
        ui.Click("uds.viewTransactions");
        var transactions = ui.Visible("uds.transactionsGrid");
        var first = ui.Until(() => transactions.FindFirstDescendant(
            cf => cf.ByControlType(ControlType.DataItem)), "decoded UDS transaction");
        first.Press();

        var fields = ui.Visible("uds.decodedFields");
        var decoded = ui.Until(() => fields.FindFirstDescendant(
            cf => cf.ByControlType(ControlType.ListItem)), "source-backed CDD/ODX subparameter");
        decoded.Press();
        ui.Until(() => fields.FindAllDescendants(
                cf => cf.ByControlType(ControlType.Text))
            .Any(item => item.Name.Contains("CAN frame #2, byte 4",
                StringComparison.Ordinal))
            ? "exact second source frame and payload offset shown" : null,
            "correct source field byte offset must be visible");

        Assert.True(ui.Visible("uds.openFieldSource").IsEnabled);
        Assert.NotNull(ui.Visible("uds.fieldSourceFrames"));
        ui.Click("uds.openFieldSource");
        Assert.False(ui.Visible("trace.grid").IsOffscreen);
        ui.Until(() => ui.Visible("trace.navigationStatus").Name.Contains(
            "frame 2 / 2", StringComparison.Ordinal)
            ? "original response frame #2 actually selected" : null,
            "source link must select recorded response, not only change workspaces");
        ui.Until(() => ui.Visible("trace.sourceByteEvidence").Name.Contains(
            "byte B4", StringComparison.Ordinal)
            ? "exact source byte B4 is shown" : null,
            "source byte is explicit after one-click raw frame navigation");
        ui.Until(() => ui.Visible("trace.grid").FindFirstDescendant(
            cf => cf.ByName("Verified DID source byte B4")),
            "raw byte B4 has an accessible source evidence marker");

        // Moving away must not leave a convincing but stale DID byte
        // overlay attached to another selected raw CAN frame.
        ui.ShowTraceReplayControls();
        ui.Click("trace.head");
        ui.Until(() => ui.Visible("trace.navigationStatus").Name.Contains(
            "frame 1 / 2", StringComparison.Ordinal)
            ? "the real request frame is now selected" : null,
            "Trace Head navigates off the recorded response");
        ui.Until(() =>
        {
            var source = ui.Main.FindFirstDescendant(
                cf => cf.ByAutomationId("trace.sourceByteEvidence"));
            return source is null || source.IsOffscreen ||
                   string.IsNullOrEmpty(source.Name)
                ? "old DID evidence overlay cleared" : null;
        }, "source highlight is cleared on normal timeline navigation");
        Assert.Equal(Trace, File.ReadAllText(ui.TracePath));
        Assert.Equal(Odx, File.ReadAllText(ui.OdxPath));
    }
}
