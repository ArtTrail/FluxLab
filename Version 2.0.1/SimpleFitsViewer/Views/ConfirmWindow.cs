using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SimpleFitsViewer.Views;

/// <summary>Plain Yes/No confirmation popup -- Avalonia has no built-in MessageBox. Used before
/// irreversible actions like overwriting a FITS file in place with edited header cards.</summary>
public class ConfirmWindow : Window
{
    private bool _result;

    public ConfirmWindow(string title, string message)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Background = new SolidColorBrush(Color.Parse("#1e1e2e"));

        var text = new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(Color.Parse("#d8d8f0")),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(16),
        };

        var yesButton = new Button
        {
            Content = "Yes",
            Background = new SolidColorBrush(Color.Parse("#1a6a30")),
            Foreground = Brushes.White,
            Padding = new Avalonia.Thickness(14, 4),
        };
        yesButton.Click += (_, _) => { _result = true; Close(); };

        var noButton = new Button
        {
            Content = "No",
            Padding = new Avalonia.Thickness(14, 4),
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
        };
        noButton.Click += (_, _) => { _result = false; Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 0, 16, 12),
            Children = { yesButton, noButton },
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        Grid.SetRow(text, 0);
        Grid.SetRow(buttons, 1);
        root.Children.Add(text);
        root.Children.Add(buttons);

        Content = root;
    }

    public new System.Threading.Tasks.Task<bool> ShowDialog(Window owner)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
        Closed += (_, _) => tcs.TrySetResult(_result);
        base.ShowDialog(owner);
        return tcs.Task;
    }
}
