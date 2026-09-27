using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimpleFitsViewer.Views;

public partial class AddCardWindow : Window
{
    public (string Keyword, string Value, string Comment)? Result { get; private set; }

    public AddCardWindow()
    {
        InitializeComponent();
    }

    private void OnAddClicked(object? sender, RoutedEventArgs e)
    {
        Result = (KeywordBox.Text ?? "", ValueBox.Text ?? "", CommentBox.Text ?? "");
        Close();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }
}
