using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitsPhotometry.Core.Fits;
using SimpleFitsViewer.Services;

namespace SimpleFitsViewer.ViewModels;

/// <summary>Searchable, editable view of one FITS file's header, matching the Python app's
/// header viewer/editor: search-filter, double-click-to-edit Value/Comment cells, Add/Delete
/// Keyword, and Save (which rewrites the file in place).</summary>
public partial class HeaderWindowViewModel : ObservableObject
{
    private readonly FitsHeader _header;
    private readonly string? _filePath;

    public HeaderWindowViewModel(FitsHeader header, string? filePath)
    {
        _header = header;
        _filePath = filePath;
        CanSave = !header.IsFromCompressedContainer;
        StatusText = header.IsFromCompressedContainer
            ? "This file's image data is tile-compressed -- header edits can be viewed but not saved."
            : "";
        Refresh();
    }

    public ObservableCollection<HeaderRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private HeaderRowViewModel? _selectedRow;
    [ObservableProperty] private string _statusText;
    [ObservableProperty] private bool _canSave;

    partial void OnSearchTextChanged(string value) => Refresh();

    private void Refresh()
    {
        Rows.Clear();
        var term = SearchText.Trim();
        foreach (var card in _header.Cards)
        {
            if (term.Length > 0 &&
                card.Keyword.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0 &&
                card.Value.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0 &&
                card.Comment.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            Rows.Add(new HeaderRowViewModel(_header, card));
        }
    }

    public void AddCard(string keyword, string value, string comment)
    {
        keyword = keyword.Trim();
        if (keyword.Length == 0) return;
        _header.SetCard(keyword, value, comment);
        Refresh();
    }

    [RelayCommand]
    private void DeleteKeyword()
    {
        if (SelectedRow is null) return;
        string keyword = SelectedRow.Keyword;   // Refresh() clears/rebuilds Rows, which can null out SelectedRow as a side effect of the DataGrid's SelectedItem binding
        if (FitsHeader.ProtectedKeywords.Contains(keyword))
        {
            StatusText = $"\"{keyword}\" is a required FITS keyword and cannot be deleted.";
            return;
        }
        _header.RemoveCard(keyword);
        Refresh();
        StatusText = $"Deleted \"{keyword}\".";
    }

    [RelayCommand]
    private void Save()
    {
        if (_filePath is null)
        {
            StatusText = "No file is loaded.";
            return;
        }
        try
        {
            _header.SaveToFile(_filePath);
            StatusText = "Header saved successfully.";
        }
        catch (Exception ex)
        {
            DiagnosticsLog.LogException("Saving FITS header", ex);
            StatusText = $"Save failed: {ex.Message}";
        }
    }
}
