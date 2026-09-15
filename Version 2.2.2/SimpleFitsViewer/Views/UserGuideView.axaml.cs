using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using SimpleFitsViewer.Services;

namespace SimpleFitsViewer.Views;

/// <summary>
/// Sectioned User Guide matching the ecosystem (StarFix/VariLab/TransitLab): a fixed title+search
/// header, a hyperlinked Contents list that scrolls to named sections, case-insensitive Find Next
/// that highlights the current match and brings it into view, and a floating back-to-top button.
/// Search and navigation logic are ported directly from StarFix's UserGuideView.
/// </summary>
public partial class UserGuideView : UserControl
{
    private readonly List<TextBlock> _matches = new();
    private readonly Dictionary<TextBlock, string> _originalText = new();
    private int _matchIndex = -1;
    private string _lastQuery = "";
    private TextBlock? _highlightedBlock;

    public UserGuideView()
    {
        InitializeComponent();
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) RunFind();
    }

    private void FindNext_Click(object? sender, RoutedEventArgs e) => RunFind();

    private void SearchClear_Click(object? sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        _matches.Clear();
        _matchIndex = -1;
        _lastQuery = "";
        SearchStatus.Text = "";
        ClearHighlight();
    }

    private void RunFind()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(query)) { SearchStatus.Text = ""; return; }

        if (!query.Equals(_lastQuery, StringComparison.OrdinalIgnoreCase))
        {
            ClearHighlight();
            _matches.Clear();
            _matchIndex = -1;
            _lastQuery = query;
            CollectMatches(ContentPanel, query, _matches, _originalText);
        }

        if (_matches.Count == 0) { SearchStatus.Text = "No matches"; ClearHighlight(); return; }

        _matchIndex = (_matchIndex + 1) % _matches.Count;
        var match = _matches[_matchIndex];
        match.BringIntoView();
        ApplyHighlight(match, query);
        SearchStatus.Text = $"{_matchIndex + 1} / {_matches.Count}";
    }

    /// <summary>Highlights just the matched substring (first occurrence in the block,
    /// case-insensitive) by swapping the block's plain Text for before/match/after Runs, since
    /// TextBlock.Text alone can't colour part of itself.</summary>
    private void ApplyHighlight(TextBlock block, string query)
    {
        ClearHighlight();

        var original = _originalText.TryGetValue(block, out var saved) ? saved : block.Text ?? "";
        var index = original.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0) { _highlightedBlock = null; return; }

        var before = original[..index];
        var matched = original.Substring(index, query.Length);
        var after = original[(index + query.Length)..];

        var highlightBg = this.TryFindResource("BrushWarn", out var warn) ? warn as IBrush : Brushes.Yellow;
        var highlightFg = this.TryFindResource("BrushBg", out var bg) ? bg as IBrush : Brushes.Black;

        // Clear Text before populating Inlines: in Avalonia 11.3 a TextBlock with BOTH a non-empty
        // Text and non-empty Inlines renders them one after the other -- the sentence appears twice.
        // Setting Text to null leaves Inlines as the sole content; ClearHighlight restores Text.
        block.Text = null;
        block.Inlines ??= new InlineCollection();
        block.Inlines.Clear();
        if (before.Length > 0) block.Inlines.Add(new Run(before));
        block.Inlines.Add(new Run(matched) { Background = highlightBg, Foreground = highlightFg });
        if (after.Length > 0) block.Inlines.Add(new Run(after));

        _highlightedBlock = block;
    }

    private void ClearHighlight()
    {
        if (_highlightedBlock is null) return;
        // Inlines takes rendering precedence over Text when non-empty, so it must be explicitly
        // cleared -- reassigning Text alone isn't guaranteed to revert it.
        _highlightedBlock.Inlines?.Clear();
        if (_originalText.TryGetValue(_highlightedBlock, out var original))
            _highlightedBlock.Text = original;
        _highlightedBlock = null;
    }

    private static void CollectMatches(
        ILogical parent, string query, List<TextBlock> results, Dictionary<TextBlock, string> originalText)
    {
        foreach (var child in parent.LogicalChildren)
        {
            if (child is TextBlock tb && tb.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            {
                results.Add(tb);
                originalText[tb] = tb.Text;
            }
            CollectMatches(child, query, results, originalText);
        }
    }

    private void ScrollToSection(Control target)
    {
        // TranslatePoint gives the target's position relative to the ScrollViewer's own coordinate
        // space (already relative to the current scroll position); adding it to the current offset
        // lands the target at the top of the viewport regardless of where either currently sits.
        var point = target.TranslatePoint(new Point(0, 0), MainScrollViewer);
        if (point.HasValue)
            MainScrollViewer.Offset = new Vector(MainScrollViewer.Offset.X, MainScrollViewer.Offset.Y + point.Value.Y);
    }

    private void GoToSection1(object? sender, RoutedEventArgs e) => ScrollToSection(Section1);
    private void GoToSection2(object? sender, RoutedEventArgs e) => ScrollToSection(Section2);
    private void GoToSection3(object? sender, RoutedEventArgs e) => ScrollToSection(Section3);
    private void GoToSection4(object? sender, RoutedEventArgs e) => ScrollToSection(Section4);
    private void GoToSection5(object? sender, RoutedEventArgs e) => ScrollToSection(Section5);
    private void GoToSection6(object? sender, RoutedEventArgs e) => ScrollToSection(Section6);
    private void GoToSection7(object? sender, RoutedEventArgs e) => ScrollToSection(Section7);
    private void GoToSection8(object? sender, RoutedEventArgs e) => ScrollToSection(Section8);
    private void GoToSection9(object? sender, RoutedEventArgs e) => ScrollToSection(Section9);
    private void GoToSection10(object? sender, RoutedEventArgs e) => ScrollToSection(Section10);
    private void GoToSection11(object? sender, RoutedEventArgs e) => ScrollToSection(Section11);
    private void GoToSection12(object? sender, RoutedEventArgs e) => ScrollToSection(Section12);
    private void GoToSection13(object? sender, RoutedEventArgs e) => ScrollToSection(Section13);
    private void GoToSection14(object? sender, RoutedEventArgs e) => ScrollToSection(Section14);

    private void OnBackToTopClick(object? sender, RoutedEventArgs e) =>
        MainScrollViewer.Offset = new Vector(MainScrollViewer.Offset.X, 0);

    /// <summary>Opens a reference link's Tag URL in the system browser via the platform Launcher.</summary>
    private async void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url)) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        try { await top.Launcher.LaunchUriAsync(new Uri(url)); }
        catch (Exception ex) { DiagnosticsLog.LogException($"Opening link {url}", ex); }
    }
}
