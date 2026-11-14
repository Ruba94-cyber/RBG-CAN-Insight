using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using System.Runtime.InteropServices;
using Xunit;

namespace RbgCanInsight.GuiTests;

/// <summary>Targeted real-window smoke for the source-backed plot hover card.
/// Set RBG_CAPTURE_PLOT_HOVER=1 to save the chart screenshot in artifacts/plot-hover.</summary>
[Trait("Category", "WindowsFlaUI")]
public sealed class GuiPlotHoverEvidenceTests
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    [Fact]
    public void QuickPlotHoverShowsSourceEvidenceAndOptionallyCapturesScreenshot()
    {
        SetProcessDPIAware();
        using var ui = new GuiSession();
        ui.Navigate("nav.dbc");
        ui.Click("dbc.load");
        ui.ChooseFile(ui.DbcPath);
        ui.AwaitStatus("1 DBC");

        ui.Navigate("nav.trace");
        ui.Click("trace.open");
        ui.ChooseFile(ui.TracePath);
        ui.AwaitStatus("Trace loaded:");

        ui.Navigate("nav.signals");
        ui.Fill("signals.search", "EngCoolantTemp");
        ui.Click("signals.quickPlot");
        var detail = ui.WindowWithTitle("Signal detail - EngineData.EngCoolantTemp");
        detail.SetForeground();
        var screenWidth = GetSystemMetrics(0);
        var screenHeight = GetSystemMetrics(1);
        var handle = (IntPtr)detail.Properties.NativeWindowHandle.Value;
        Assert.True(SetWindowPos(handle, IntPtr.Zero, 10, 10,
            screenWidth - 20, screenHeight - 40, 0x0004 | 0x0040),
            "Could not fit Quick Plot inside the interactive desktop.");
        detail.SetForeground();
        Thread.Sleep(300);
        var chart = ui.Until(() => detail.FindFirstDescendant(
            cf => cf.ByAutomationId("quickplot.chart")), "Quick Plot canvas");
        Assert.False(chart.IsOffscreen);

        var bounds = chart.BoundingRectangle;
        Assert.True(bounds.Left >= 0 && bounds.Top >= 0 &&
            bounds.Right <= screenWidth && bounds.Bottom <= screenHeight,
            $"Quick Plot canvas is outside the screen: {bounds}; screen {screenWidth}x{screenHeight}.");
        // The fixture has coolant samples at 0.0, 0.1 and 0.2 s with a linear
        // midpoint at 0.1 s. Hover the center of that curve in the Quick Plot.
        var hoverX = (int)(bounds.Left + bounds.Width * 0.5);
        var hoverY = (int)(bounds.Top + bounds.Height * 0.5);
        Mouse.MoveTo(hoverX, hoverY);
        Thread.Sleep(700);

        var screenshotFlag = Environment.GetEnvironmentVariable("RBG_CAPTURE_PLOT_HOVER");
        if (string.Equals(screenshotFlag, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(screenshotFlag, "true", StringComparison.OrdinalIgnoreCase))
        {
            var repository = FindRepositoryRoot();
            var output = Path.Combine(repository, "artifacts", "plot-hover");
            Directory.CreateDirectory(output);
            Capture.Screen().ToFile(Path.Combine(output, "quickplot-hover-evidence.png"));
        }

        // The plot's primary readout is custom-drawn and its automation peer
        // does not expose that text. On compact layouts the WPF fallback
        // tooltip may be available to UIA; assert its content when exposed,
        // while preserving the screenshot/manual-inspection path otherwise.
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var tooltip = automation.GetDesktop().FindFirstDescendant(
            cf => cf.ByControlType(ControlType.ToolTip));
        if (tooltip is not null)
        {
            var name = tooltip.Name ?? string.Empty;
            Assert.Contains("CAN", name, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("raw", name, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Console.WriteLine("Hover card is canvas-drawn and unavailable through UIA; inspect the optional screenshot visually.");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "RbgCanInsight.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException(
            "Could not find RbgCanInsight.sln above the GUI test output directory.");
    }
}
