using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SimpleFitsViewer.Services;

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

    /// <summary>Opens a link's Tag URL in the system browser, via the platform Launcher (the
    /// cross-platform way; the sandboxed StorageProvider/Process.Start dance isn't needed here).</summary>
    private async void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url)) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        try { await top.Launcher.LaunchUriAsync(new Uri(url)); }
        catch (Exception ex) { DiagnosticsLog.LogException($"Opening link {url}", ex); }
    }
}
