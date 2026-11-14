using FlaUI.Core.AutomationElements;

namespace RbgCanInsight.GuiTests;

/// <summary>Activates controls through UIA patterns so tests do not depend on
/// which window is on top of the physical desktop; real mouse click is the fallback.</summary>
internal static class UiaInput
{
    public static void Press(this AutomationElement element)
    {
        var patterns = element.Patterns;
        try
        {
            // Moving focus commits pending TextBox bindings (LostFocus), like a real click.
            if (element.Properties.IsKeyboardFocusable.ValueOrDefault) element.Focus();
        }
        catch (Exception ex) when (ex is FlaUI.Core.Exceptions.FlaUIException
            or System.Runtime.InteropServices.COMException) { }
        try
        {
            if (patterns.SelectionItem.IsSupported) { patterns.SelectionItem.Pattern.Select(); return; }
            if (patterns.Invoke.IsSupported) { patterns.Invoke.Pattern.Invoke(); return; }
            if (patterns.Toggle.IsSupported) { patterns.Toggle.Pattern.Toggle(); return; }
            if (patterns.ExpandCollapse.IsSupported && element.ControlType ==
                FlaUI.Core.Definitions.ControlType.ComboBox)
            { patterns.ExpandCollapse.Pattern.Expand(); return; }
        }
        catch (Exception ex) when (ex is FlaUI.Core.Exceptions.FlaUIException
            or System.Runtime.InteropServices.COMException) { }
        element.Click();
    }
}
