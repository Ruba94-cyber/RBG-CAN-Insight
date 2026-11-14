using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RbgCanInsight.App.ViewModels;

namespace RbgCanInsight.App.Controls;

/// <summary>Decoded-signal grid shared by Trace and Byte Matrix (see the remarks in the XAML).
/// Both hosts subscribe to <see cref="OpenSignalDetailRequested"/> to open the dedicated Quick
/// Plot window — the row-level action buttons bind their commands straight through to the owning
/// Window's DataContext via <c>RelativeSource AncestorType=Window</c>, so only the one action that
/// needs code-behind (opening a whole new window) is routed as an event.</summary>
public partial class DecodedSignalGrid : UserControl
{
    public static readonly DependencyProperty SignalRowsProperty =
        DependencyProperty.Register(
            nameof(SignalRows),
            typeof(IReadOnlyList<SignalDetailRow>),
            typeof(DecodedSignalGrid),
            new PropertyMetadata(Array.Empty<SignalDetailRow>()));

    public static readonly DependencyProperty MaxRowsHeightProperty =
        DependencyProperty.Register(
            nameof(MaxRowsHeight),
            typeof(double),
            typeof(DecodedSignalGrid),
            new PropertyMetadata(double.PositiveInfinity));

    public event EventHandler<SignalDetailRow>? OpenSignalDetailRequested;

    public DecodedSignalGrid()
    {
        InitializeComponent();
    }

    public IReadOnlyList<SignalDetailRow> SignalRows
    {
        get => (IReadOnlyList<SignalDetailRow>)GetValue(SignalRowsProperty);
        set => SetValue(SignalRowsProperty, value);
    }

    /// <summary>Optional cap so a host that sits above other content (Trace) can keep the grid
    /// from growing unbounded; a host already bounded by its own resizable row (Byte Matrix) can
    /// leave this at the default of no cap.</summary>
    public double MaxRowsHeight
    {
        get => (double)GetValue(MaxRowsHeightProperty);
        set => SetValue(MaxRowsHeightProperty, value);
    }

    private void OnOpenSignalDetailClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SignalDetailRow signal })
        {
            OpenSignalDetailRequested?.Invoke(this, signal);
        }
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: SignalDetailRow signal })
        {
            OpenSignalDetailRequested?.Invoke(this, signal);
        }
    }
}
