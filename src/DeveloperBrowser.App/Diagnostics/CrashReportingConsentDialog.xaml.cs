using System.Windows;

namespace DeveloperBrowser.App.Diagnostics;

public partial class CrashReportingConsentDialog : Window
{
    public CrashReportingConsentDialog(bool? currentChoice)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeWindowStyle.ApplyModernDarkChrome(this);
        EnableReportingOption.IsChecked = currentChoice == true;
        DisableReportingOption.IsChecked = currentChoice == false;
    }

    public bool ReportingEnabled { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (EnableReportingOption.IsChecked != true && DisableReportingOption.IsChecked != true)
        {
            ValidationText.Text = "Choose Yes or No.";
            return;
        }

        ReportingEnabled = EnableReportingOption.IsChecked == true;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
