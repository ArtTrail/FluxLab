using Avalonia.Controls;
using Avalonia.Interactivity;
using SimpleFitsViewer.ViewModels;

namespace SimpleFitsViewer.Views;

public partial class HeaderWindow : Window
{
    public HeaderWindow()
    {
        InitializeComponent();
    }

    private async void OnAddKeywordClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not HeaderWindowViewModel vm) return;

        var dialog = new AddCardWindow();
        await dialog.ShowDialog(this);
        if (dialog.Result is { } r) vm.AddCard(r.Keyword, r.Value, r.Comment);
    }

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not HeaderWindowViewModel vm) return;

        var confirm = new ConfirmWindow("Save Header",
            "Save header changes to this file now?\n\nThis modifies the file in place and cannot be undone.");
        if (await confirm.ShowDialog(this))
            vm.SaveCommand.Execute(null);
    }
}
