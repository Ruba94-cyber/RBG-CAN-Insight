using System.Windows;
using RbgCanInsight.App.Common;

namespace RbgCanInsight.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // One number format everywhere (0.5 s, 50.0 %): the UI mixed culture-formatted and invariant text.
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        // Apply the saved color palette before the main window is constructed so it never
        // flashes with an unthemed/default look.
        ThemeManager.Apply(AppSettingsStore.LoadThemeIndex());
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            FileLog.Error("DispatcherUnhandledException", args.Exception);
            MessageBox.Show(
                args.Exception.Message,
                "RBG CAN Insight — unexpected error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                FileLog.Error("UnhandledException", exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            FileLog.Error("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }
}
