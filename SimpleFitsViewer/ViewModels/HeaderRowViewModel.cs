using CommunityToolkit.Mvvm.ComponentModel;
using FitsPhotometry.Core.Fits;

namespace SimpleFitsViewer.ViewModels;

/// <summary>Wraps one FitsCard for grid display/editing. Keyword is shown but not editable
/// inline (renaming is Delete + Add Keyword instead) -- avoids the ambiguity of what "renaming"
/// should do to the underlying card identity. Value/Comment edits go through
/// FitsHeader.UpdateCard (identity-based, not keyword-based) so COMMENT/HISTORY rows -- which
/// have no unique keyword -- update the exact card being edited rather than some other one
/// sharing the same keyword, while still keeping the header's fast Get/Has lookup in sync.</summary>
public partial class HeaderRowViewModel : ObservableObject
{
    private readonly FitsHeader _header;

    public FitsCard Card { get; }
    public string Keyword => Card.Keyword;
    public bool IsProtected => FitsHeader.ProtectedKeywords.Contains(Card.Keyword);

    [ObservableProperty] private string _value;
    [ObservableProperty] private string _comment;

    public HeaderRowViewModel(FitsHeader header, FitsCard card)
    {
        _header = header;
        Card = card;
        _value = card.Value;
        _comment = card.Comment;
    }

    partial void OnValueChanged(string value) => _header.UpdateCard(Card, value, Comment);
    partial void OnCommentChanged(string value) => _header.UpdateCard(Card, Value, value);
}
