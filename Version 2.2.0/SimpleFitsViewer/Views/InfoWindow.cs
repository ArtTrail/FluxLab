using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SimpleFitsViewer.Views;

/// <summary>Plain scrollable-text popup, shared by the User Guide / Revision History / About
/// items under Help -- they're all "a title bar and some text," so one reusable window avoids
/// three near-identical XAML files.</summary>
public class InfoWindow : Window
{
    public InfoWindow(string title, string content)
    {
        Title = title;
        Width = 600;
        Height = 520;
        Background = new SolidColorBrush(Color.Parse("#1e1e2e"));

        var titleBar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1b3f6b")),
            Padding = new Avalonia.Thickness(0, 8),
            Child = new TextBlock
            {
                Text = title,
                Foreground = Brushes.White,
                FontWeight = Avalonia.Media.FontWeight.Bold,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };

        var textBlock = new TextBlock
        {
            Text = content,
            Foreground = new SolidColorBrush(Color.Parse("#d8d8f0")),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(16),
            FontFamily = new FontFamily("Consolas,Menlo,monospace"),
            FontSize = 12,
        };

        var scroller = new ScrollViewer { Content = textBlock };

        var closeButton = new Button
        {
            Content = "Close",
            Background = new SolidColorBrush(Color.Parse("#1a3060")),
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 8, 0, 12),
            Padding = new Avalonia.Thickness(16, 4),
        };
        closeButton.Click += (_, _) => Close();

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(scroller, 1);
        Grid.SetRow(closeButton, 2);
        root.Children.Add(titleBar);
        root.Children.Add(scroller);
        root.Children.Add(closeButton);

        Content = root;
    }
}
