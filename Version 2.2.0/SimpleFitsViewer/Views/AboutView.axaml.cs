using Avalonia.Controls;

namespace SimpleFitsViewer.Views;

public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
        // Version is read from the single AppVersion constant rather than hard-coded in XAML, so it
        // can't drift from the title bar / Revision History.
        VersionText.Text = $"Version {AppVersion.Version}";
    }
}
