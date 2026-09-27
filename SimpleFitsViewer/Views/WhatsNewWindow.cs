using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SimpleFitsViewer.Services;

namespace SimpleFitsViewer.Views;

/// <summary>First-launch "What's New" popup listing the current version's Revision History bullets.
/// Shown once per new version (see <see cref="WhatsNewService"/>); code-built to match the other
/// small windows.</summary>
public class WhatsNewWindow : Window
{
    public WhatsNewWindow(RevisionEntry entry)
    {
        Title = "What's New in FluxLab";
        Width = 580;
        MaxHeight = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Background = new SolidColorBrush(Color.Parse("#1e1e2e"));

        var heading = new TextBlock
        {
            Text = $"What's new in FluxLab v{entry.Version}",
            Foreground = new SolidColorBrush(Color.Parse("#66aaff")),
            FontWeight = FontWeight.Bold,
            FontSize = 18,
            Margin = new Avalonia.Thickness(20, 18, 20, 2),
        };
        var dateLine = new TextBlock
        {
            Text = entry.Date,
            Foreground = new SolidColorBrush(Color.Parse("#8888bb")),
            FontStyle = FontStyle.Italic,
            Margin = new Avalonia.Thickness(20, 0, 20, 10),
        };

        var bullets = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(20, 0, 20, 4) };
        foreach (var b in entry.Bullets)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var dot = new TextBlock
            {
                Text = "•  ",
                Foreground = new SolidColorBrush(Color.Parse("#66aaff")),
            };
            var body = new TextBlock
            {
                Text = b,
                Foreground = new SolidColorBrush(Color.Parse("#d8d8f0")),
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(dot, 0);
            Grid.SetColumn(body, 1);
            row.Children.Add(dot);
            row.Children.Add(body);
            bullets.Children.Add(row);
        }

        var scroller = new ScrollViewer
        {
            Content = bullets,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        var gotIt = new Button
        {
            Content = "Got it",
            Background = new SolidColorBrush(Color.Parse("#2a6fb0")),
            Foreground = Brushes.White,
            Padding = new Avalonia.Thickness(18, 5),
        };
        gotIt.Click += (_, _) => Close();

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 8, 20, 14),
            Children = { gotIt },
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        Grid.SetRow(heading, 0);
        Grid.SetRow(dateLine, 1);
        Grid.SetRow(scroller, 2);
        Grid.SetRow(buttonRow, 3);
        root.Children.Add(heading);
        root.Children.Add(dateLine);
        root.Children.Add(scroller);
        root.Children.Add(buttonRow);

        Content = root;
    }
}
