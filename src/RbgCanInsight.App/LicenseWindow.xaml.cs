using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Navigation;

namespace RbgCanInsight.App;

public partial class LicenseWindow : Window
{
    public LicenseWindow()
    {
        InitializeComponent();
        using var stream = typeof(LicenseWindow).Assembly.GetManifestResourceStream("RbgCanInsight.ApplicationTerms.md");
        if (stream is null)
        {
            TermsText.Text = "The application terms could not be loaded. Contact paolo.ruba.1913@gmail.com.";
            return;
        }

        using var reader = new StreamReader(stream);
        TermsText.Text = reader.ReadToEnd();
    }

    private void OnContactNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, "Could not open your email application. Please write to paolo.ruba.1913@gmail.com.",
                "Commercial licensing", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        e.Handled = true;
    }
}
