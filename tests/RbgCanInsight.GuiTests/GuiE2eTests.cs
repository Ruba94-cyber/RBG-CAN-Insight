using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>The "E2E check" window verifies an AUTOSAR E2E Profile 1 stream from the loaded trace.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiE2eTests
{
    private const int DataId = 0x1A2B;
    private const int FrameCount = 40;

    /// <summary>Independent Profile 1 reference: CRC-8 0x1D, init 0xFF, xor-out 0xFF over data ID (low, high)
    /// then the payload without the CRC byte.</summary>
    private static byte ReferenceCrc(byte[] payload)
    {
        byte crc = 0xFF;
        void Feed(byte b)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ 0x1D) : (byte)(crc << 1);
        }
        Feed((byte)(DataId & 0xFF));
        Feed((byte)(DataId >> 8));
        for (var i = 1; i < payload.Length; i++) Feed(payload[i]);
        return (byte)(crc ^ 0xFF);
    }

    private static string BuildTrace(int corruptIndex)
    {
        var sb = new StringBuilder();
        sb.AppendLine("date Tue Jul 07 10:00:00.000 2026");
        sb.AppendLine("base hex  timestamps absolute");
        sb.AppendLine("internal events logged");
        for (var i = 0; i < FrameCount; i++)
        {
            var counter = i % 15;
            var p = new byte[] { 0, (byte)(counter | ((i * 3 % 16) << 4)), (byte)(i * 7), 0x40, 0, 0, (byte)i, 0x55 };
            p[0] = ReferenceCrc(p);
            if (i == corruptIndex) p[0] ^= 0xFF;
            sb.AppendLine($"{(i * 0.01).ToString("F6", System.Globalization.CultureInfo.InvariantCulture)} 1 1A0 Rx d 8 {string.Join(" ", p.Select(b => b.ToString("X2")))}");
        }
        return sb.ToString();
    }

    private static (GuiSession Ui, FlaUI.Core.AutomationElements.Window Window) OpenChecker(int corruptIndex)
    {
        var ui = new GuiSession();
        File.WriteAllText(ui.TracePath, BuildTrace(corruptIndex));
        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");
        ui.Visible("trace.moreTools").Patterns.ExpandCollapse.Pattern.Expand();
        ui.Click("tools.e2e");
        var window = ui.WindowWithTitle("E2E check");
        return (ui, window);
    }

    private static AutomationElement In(GuiSession ui, FlaUI.Core.AutomationElements.Window w, string id) =>
        ui.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId(id)) is { IsOffscreen: false } e ? e : null,
            "control " + id);

    private static void SelectIdAndCheck(GuiSession ui, FlaUI.Core.AutomationElements.Window window)
    {
        var row = ui.Until(() => In(ui, window, "e2e.idList").FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
            "ID row");
        Assert.Contains("1A0", row.Name, StringComparison.OrdinalIgnoreCase);
        row.Patterns.SelectionItem.Pattern.Select();
        In(ui, window, "e2e.dataId").AsTextBox().Text = DataId.ToString("X");
        In(ui, window, "e2e.cycle").AsTextBox().Text = "10";
        ui.Until(() =>
        {
            var check = In(ui, window, "e2e.check");
            if (!check.IsEnabled) return null;
            check.Press();
            return "pressed";
        }, "Check button");
    }

    [Fact]
    public void ValidProfile1StreamPassesEveryFrame()
    {
        var (ui, window) = OpenChecker(-1);
        using (ui)
        {
            SelectIdAndCheck(ui, window);
            var summary = ui.Until(() =>
            {
                var s = In(ui, window, "e2e.summary");
                return s.Name.Contains("passed", StringComparison.OrdinalIgnoreCase) ? s : null;
            }, "all-passed summary");
            Assert.Contains($"All {FrameCount} frames passed", summary.Name);
            window.Close();
        }
    }

    [Fact]
    public void CorruptedCrcShowsAProblemRowAndTheRedSummary()
    {
        var (ui, window) = OpenChecker(20);
        using (ui)
        {
            SelectIdAndCheck(ui, window);
            var summary = ui.Until(() =>
            {
                var s = In(ui, window, "e2e.summary");
                return s.Name.Contains("Problems found", StringComparison.OrdinalIgnoreCase) ? s : null;
            }, "problem summary");
            Assert.Equal("Red", summary.HelpText);
            In(ui, window, "e2e.problemsOnly").AsCheckBox().IsChecked = true;
            var grid = In(ui, window, "e2e.grid");
            var lastCount = -1;
            AutomationElement[] rows;
            try { rows = ui.Until(() =>
            {
                var items = grid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem));
                // The corrupted frame and the following one (sequence gap) are the only problems.
                lastCount = items.Length;
                return items.Length is >= 1 and <= 2 ? items : null;
            }, "problem rows only"); }
            catch (TimeoutException) { Assert.Fail("rows seen in grid: " + lastCount + " texts: " + string.Join("|", grid.FindAllDescendants().Select(e => e.Name).Where(n => !string.IsNullOrEmpty(n)).Take(30))); return; }
            Assert.Contains(grid.FindAllDescendants().Select(e => e.Name), n => n == "Wrong CRC");
            Assert.InRange(rows.Length, 1, 2);
            window.Close();
        }
    }
}
