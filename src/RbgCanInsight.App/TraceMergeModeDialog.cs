using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using RbgCanInsight.App.Common;
using RbgCanInsight.TraceIO;

namespace RbgCanInsight.App;

/// <summary>Small modal chooser for how several logs are combined (merge mode).</summary>
public sealed class TraceMergeModeDialog : Window
{
    private readonly RadioButton _byTime = Make("By time", "Interleave all frames by timestamp; channel numbers are kept.", "merge.byTime");
    private readonly RadioButton _sequential = Make("Sequential",
        "Append the files one after another; a file that starts before the previous one ended is shifted to start where it ended.", "merge.sequential");
    private readonly RadioButton _separate = Make("Separate channels",
        "Interleave by time but give each file its own channel range, so equal channel numbers of different files stay apart.", "merge.separate");

    private TraceMergeModeDialog(int fileCount)
    {
        Title = "Merge mode";
        SizeToContent = SizeToContent.Height;
        Width = 520;
        MinWidth = 440;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        TitleBarTheme.Hook(this);
        _byTime.IsChecked = true;
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 12, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80, Margin = new Thickness(0, 12, 0, 0) };
        AutomationProperties.SetAutomationId(ok, "merge.ok");
        ok.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = $"How should the {fileCount} selected logs be combined?",
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        });
        AddOption(panel, _byTime, "Frames are interleaved by their original timestamps; channels keep their numbers.");
        AddOption(panel, _sequential, "Files are appended in order; overlapping timestamps are shifted to follow the previous file.");
        AddOption(panel, _separate, "Frames share a timeline but each file gets a separate channel range to avoid collisions.");
        panel.Children.Add(buttons);
        Content = panel;
    }

    private static void AddOption(Panel panel, RadioButton radio, string description)
    {
        panel.Children.Add(radio);
        var hint = new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(32, 0, 0, 8),
            MaxWidth = 440
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        panel.Children.Add(hint);
    }

    private static RadioButton Make(string text, string tip, string id)
    {
        var button = new RadioButton { Content = text, ToolTip = tip, Margin = new Thickness(0, 2, 0, 2) };
        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, text);
        return button;
    }

    private TraceMergeMode Selected => _sequential.IsChecked == true ? TraceMergeMode.Sequential
        : _separate.IsChecked == true ? TraceMergeMode.SeparateChannels : TraceMergeMode.ByTime;

    /// <summary>Returns the chosen mode, or null when cancelled.</summary>
    public static TraceMergeMode? Ask(Window? owner, int fileCount)
    {
        var dialog = new TraceMergeModeDialog(fileCount) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Selected : null;
    }
}
