using Avalonia.Controls;
using SimpleFitsViewer.Services;

namespace SimpleFitsViewer.Views;

public partial class RevisionHistoryView : UserControl
{
    public RevisionHistoryView()
    {
        InitializeComponent();
        Entries.ItemsSource = RevisionHistoryData.All;
    }
}
