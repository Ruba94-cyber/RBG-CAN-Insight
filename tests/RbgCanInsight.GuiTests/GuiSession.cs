using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace RbgCanInsight.GuiTests;

/// <summary>Launch the real WPF executable as an independent Windows process.
/// No ViewModel shortcuts, no real user preferences and no external ECU access.</summary>
internal sealed class GuiSession : IDisposable
{
    private readonly FlaUI.Core.Application _app;
    private readonly UIA3Automation _automation;
    private readonly string _root;
    private readonly FileStream _desktopLock;

    public FlaUI.Core.AutomationElements.Window Main { get; }
    public string Root => _root;
    public string TracePath { get; }
    public string DbcPath { get; }
    public string OdxPath { get; }
    public string CddPath { get; }

    public GuiSession(bool runOnboarding = false, int themeIndex = 0)
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
            throw new InvalidOperationException("FlaUI requires a logged-in interactive Windows desktop.");
        _desktopLock = AcquireDesktopLock();
        try
        {
        _root = Path.Combine(Path.GetTempPath(), "rbg-flaui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, "config", "app-settings.json"),
            "{\"ThemeIndex\":" + themeIndex + ",\"OnboardingShown\":" + (runOnboarding ? "false" : "true") +
            ",\"SelectedNavIndex\":0}");
        DbcPath = Path.Combine(_root, "fixture.dbc");
        OdxPath = Path.Combine(_root, "fixture.odx");
        CddPath = Path.Combine(_root, "fixture.cdd");
        TracePath = Path.Combine(_root, "fixture.asc");
        File.WriteAllText(DbcPath, Fixtures.Dbc);
        File.WriteAllText(OdxPath, Fixtures.Odx);
        File.WriteAllText(CddPath, Fixtures.Cdd);
        File.WriteAllText(TracePath, Fixtures.Trace);

        var executable = FindExecutable();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false
        };
        start.Environment["RBG_CAN_INSIGHT_GUI_TEST_ROOT"] = _root;
        _app = FlaUI.Core.Application.Launch(start);
        _automation = new UIA3Automation();
        Main = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(30)) ??
            throw new InvalidOperationException("RBG CAN Insight main WPF window was not created.");
        Main.SetForeground();
        Visible("nav.dashboard");
        }
        catch
        {
            _desktopLock.Dispose(); // a failed launch must not block every later scenario
            throw;
        }
    }

    /// <summary>Resize needs a restored window: on small displays the app can start maximized and Transform.Resize then throws.</summary>
    public void ResizeTo(double width, double height)
    {
        if (Main.Patterns.Window.PatternOrDefault is { } window &&
            window.WindowVisualState.ValueOrDefault != WindowVisualState.Normal)
        {
            window.SetWindowVisualState(WindowVisualState.Normal);
            Until(() => window.WindowVisualState.ValueOrDefault == WindowVisualState.Normal ? window : null,
                "window restored to normal state before resize");
        }
        Main.Patterns.Transform.Pattern.Resize(width, height);
    }

    private static string FindExecutable()
    {
        var explicitPath = Environment.GetEnvironmentVariable("RBG_CAN_INSIGHT_GUI_APP_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) :
                throw new FileNotFoundException("Configured UI-test executable does not exist.", explicitPath);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "RbgCanInsight.sln")))
            directory = directory.Parent;
        if (directory is null)
            throw new FileNotFoundException("Could not find RbgCanInsight.sln above GUI-test binaries.");
        var preferred = AppContext.BaseDirectory.Contains("Debug", StringComparison.OrdinalIgnoreCase)
            ? new[] { "Debug", "Release" } : new[] { "Release", "Debug" };
        foreach (var configuration in preferred)
        {
            var exe = Path.Combine(directory.FullName, "src", "RbgCanInsight.App", "bin",
                configuration, "net10.0-windows", "RbgCanInsight.exe");
            if (File.Exists(exe)) return exe;
        }
        throw new FileNotFoundException("Build the app first: dotnet build RbgCanInsight.sln -c Release");
    }

    public T Until<T>(Func<T?> probe, string operation, int timeoutSeconds = 25) where T : class
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            try
            {
                if (probe() is { } item) return item;
            }
            catch (System.Runtime.InteropServices.COMException) { /* re-created UIA peer */ }
            Thread.Sleep(125);
        }
        throw new TimeoutException("FlaUI timed out: " + operation);
    }

    public AutomationElement? Find(string id)
    {
        var inMain = Main.FindFirstDescendant(cf => cf.ByAutomationId(id));
        if (inMain is not null || id == "dbc.openInspector")
            return inMain;

        // The DBC signal inspector lives in its own pop-up window; tests address its
        // controls by AutomationId exactly as before, opening the window on demand.
        var inspector = ProcessWindows().Any(w => w.Title.Contains("DBC signal inspector", StringComparison.Ordinal))
            ? WindowWithTitle("DBC signal inspector") : null;
        if (inspector is null &&
            Main.FindFirstDescendant(cf => cf.ByAutomationId("dbc.openInspector")) is { IsOffscreen: false, IsEnabled: true } open)
        {
            open.Press();
            inspector = WindowWithTitle("DBC signal inspector");
        }
        var inInspector = inspector?.FindFirstDescendant(cf => cf.ByAutomationId(id));
        if (inInspector is not null) return inInspector;

        // Trigger rule inputs can be detached to a resizable window on small screens.
        // Keep standard UIA selectors stable across docked and pop-out layouts.
        if (id.StartsWith("statistics.trigger", StringComparison.Ordinal))
        {
            var editor = ProcessWindows().Any(w => w.Title.StartsWith("Signal triggers —", StringComparison.Ordinal))
                ? WindowWithTitle("Signal triggers —") : null;
            return editor?.FindFirstDescendant(cf => cf.ByAutomationId(id));
        }
        return null;
    }

    public AutomationElement Visible(string id) =>
        Until(() =>
        {
            var target = Find(id);
            return target is { IsOffscreen: false } ? target : null;
        }, "visible control " + id);

    public void Navigate(string id) => Visible(id).Press();
    public void Click(string id) => Visible(id).Press();
    public void ShowInvestigationAdvanced()
    {
        Navigate("nav.investigation");
        var expander = Visible("investigation.advanced").Patterns.ExpandCollapse.Pattern;
        if (expander.ExpandCollapseState.Value != ExpandCollapseState.Expanded)
            expander.Expand();
        Visible("investigation.layout");
    }
    public void ShowTraceSecondaryControls()
    {
        Navigate("nav.trace");
        if (Find("trace.notebookNew") is not { IsOffscreen: false })
            Click("trace.secondaryToggle");
    }
    public void ShowTraceReplayControls()
    {
        Navigate("nav.trace");
        if (Find("trace.head") is not { IsOffscreen: false })
            Visible("trace.replayTools").Patterns.ExpandCollapse.Pattern.Expand();
    }
    public void Fill(string id, string value) => Visible(id).AsTextBox().Text = value;
    public string Text(string id) => Visible(id).AsTextBox().Text;
    public string Status => Find("app.status")?.Name ?? "";
    public void AwaitStatus(string expected) =>
        Until(() => Status.Contains(expected, StringComparison.OrdinalIgnoreCase) ? "ok" : null,
            "status containing " + expected + " (last status: " + Status + ")", 35);

    public void AwaitMappingStatus(string expected) =>
        Until(() => Find("mapping.status")?.Name?.Contains(expected,
            StringComparison.OrdinalIgnoreCase) == true ? "ready" : null,
            "mapping status containing " + expected + " (last: " + Find("mapping.status")?.Name + " | app: " + Status + ")", 35);

    /// <summary>Visible windows of the app process by Win32 enumeration. UIA calls into
    /// a process showing a modal window can block, so lookups avoid UIA until a handle is known.</summary>
    private IEnumerable<(IntPtr Handle, string Title, string Class)> ProcessWindows()
    {
        var list = new List<(IntPtr, string, string)>();
        var pid = (uint)_app.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != pid || !IsWindowVisible(hwnd)) return true;
            var cls = new System.Text.StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            var title = new System.Text.StringBuilder(256);
            GetWindowText(hwnd, title, title.Capacity);
            list.Add((hwnd, title.ToString(), cls.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Posts a key to the topmost non-main window of the app (focus-independent).</summary>
    public void PressKey(ushort virtualKey)
    {
        var mainHandle = Main.Properties.NativeWindowHandle.Value;
        var target = ProcessWindows().Where(w => w.Class != "#32770" && w.Handle != mainHandle)
            .Select(w => w.Handle).FirstOrDefault();
        if (target == IntPtr.Zero) target = mainHandle;
        PostMessage(target, 0x0100, (IntPtr)virtualKey, IntPtr.Zero);
        PostMessage(target, 0x0101, (IntPtr)virtualKey, IntPtr.Zero);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Answers a standard Win32 MessageBox (IDYES=6, IDOK=1, IDNO=7).</summary>
    public void AnswerMessageBox(string titleContains, int buttonId = 6)
    {
        var handle = Until(() =>
        {
            var match = ProcessWindows().FirstOrDefault(w => w.Class == "#32770" &&
                w.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase));
            return match.Handle == IntPtr.Zero ? null : (object)match.Handle;
        }, "message box " + titleContains);
        var button = GetDlgItem((IntPtr)handle, buttonId);
        SendMessage(button, 0x00F5 /*BM_CLICK*/, IntPtr.Zero, IntPtr.Zero);
        Until(() => IsWindowOpen(titleContains) ? null : "closed", "message box closed");
    }

    public bool IsWindowOpen(string title) =>
        ProcessWindows().Any(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));

    public FlaUI.Core.AutomationElements.Window WindowWithTitle(string title) =>
        Until(() =>
        {
            var match = ProcessWindows().FirstOrDefault(w =>
                w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
            return match.Handle == IntPtr.Zero ? null
                : _automation.FromHandle(match.Handle).AsWindow();
        }, "window " + title);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);
    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc proc, IntPtr lParam);

    private static IntPtr FindDescendantEdit(IntPtr root)
    {
        var found = IntPtr.Zero;
        EnumChildWindows(root, (child, _) =>
        {
            var cls = new System.Text.StringBuilder(32);
            GetClassName(child, cls, cls.Capacity);
            if (cls.ToString() != "Edit") return true;
            found = child;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Enters a multi-select filename list in the standard native Open dialog.</summary>
    public void ChooseFiles(params string[] absolutePaths)
    {
        if (absolutePaths.Length < 2) throw new ArgumentException("Select two or more paths.");
        ChooseFile(string.Join(" ", absolutePaths.Select(path => "\"" + path + "\"")));
    }

    public void ChooseFile(string absolutePath)
    {
        var hwnd = Until(() => { var h = FindFileDialogHandle(); return h == IntPtr.Zero ? null : (object)h; },
            "native Open/Save file dialog");
        var handle = (IntPtr)hwnd;
        // Win32 common dialog: filename box id 0x47C (1148), OK button IDOK (1).
        // Plain window messages are instant and independent of keyboard focus
        // and of the (very large, slow) UIA tree of the file list.
        var combo = GetDlgItem(handle, 1148);
        var edit = combo != IntPtr.Zero ? FindDescendantEdit(combo) : IntPtr.Zero;
        if (edit == IntPtr.Zero) edit = GetDlgItem(handle, 1001);
        if (edit == IntPtr.Zero) edit = FindDescendantEdit(handle);
        if (edit == IntPtr.Zero)
            throw new InvalidOperationException("File dialog filename box not found.");
        SendMessageText(edit, 0x000C /*WM_SETTEXT*/, IntPtr.Zero, absolutePath);
        SendMessage(GetDlgItem(handle, 1), 0x00F5 /*BM_CLICK*/, IntPtr.Zero, IntPtr.Zero);
        Until(() => IsWindow(handle) && FindFileDialogHandle() == handle ? null : "closed",
            "file picker accepted " + Path.GetFileName(absolutePath), 35);
    }

    /// <summary>Finds an open Win32 common dialog (#32770) belonging to the app process.</summary>
    private IntPtr FindFileDialogHandle()
    {
        var found = IntPtr.Zero;
        var pid = (uint)_app.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != pid || !IsWindowVisible(hwnd)) return true;
            var name = new System.Text.StringBuilder(32);
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() != "#32770") return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder text, int max);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool SetDlgItemText(IntPtr dlg, int id, string text);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SendMessageText(IntPtr hwnd, int msg, IntPtr wParam, string lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetDlgItem(IntPtr dlg, int id);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    public void AwaitExport(string destination) =>
        Until(() => File.Exists(destination) && new FileInfo(destination).Length > 512
            ? "exported" : null, "export " + destination, 35);

    public void Dispose()
    {
        try
        {
            var process = Process.GetProcessById(_app.ProcessId);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        finally
        {
            _automation.Dispose();
            _app.Dispose();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _desktopLock.Dispose();
        }
    }

    /// <summary>One desktop, one automation client: a second test run (another terminal or agent
    /// session) waits here instead of fighting for keyboard focus. The lock is an exclusively
    /// opened file, so it is released by the OS even if the test host is killed.</summary>
    private static FileStream AcquireDesktopLock()
    {
        var path = Path.Combine(Path.GetTempPath(), "rbg-can-insight-flaui.lock");
        var waited = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (waited.Elapsed < TimeSpan.FromMinutes(20))
            {
                Thread.Sleep(500);
            }
        }
    }
}
