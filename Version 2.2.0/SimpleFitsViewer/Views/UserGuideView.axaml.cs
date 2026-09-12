using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace SimpleFitsViewer.Views;

/// <summary>
/// Scrollable User Guide with the ecosystem's shared search: a fixed title+search header, a
/// case-insensitive Find Next that cycles through every match highlighting the current one and
/// scrolling it into view, plus a running "n / total" status. Ported to match StarFix/VariLab.
///
/// The guide text (AppText.UserGuide) is split into one TextBlock per paragraph (blank-line
/// separated). That keeps a single source of truth for the content while giving each match a real
/// control to BringIntoView -- highlighting a run inside one giant TextBlock could not scroll to it.
/// </summary>
public partial class UserGuideView : UserControl
{
    private static readonly IBrush HighlightBg = new SolidColorBrush(Color.Parse("#ebcb8b"));
    private static readonly IBrush HighlightFg = new SolidColorBrush(Color.Parse("#2e3440"));
    private static readonly IBrush BodyFg = new SolidColorBrush(Color.Parse("#eceff4"));

    private readonly List<TextBlock> _paragraphs = new();
    private readonly List<string> _paragraphText = new();

    // Flat list of matches across all paragraphs, in document order.
    private readonly List<(int Para, int Offset)> _matches = new();
    private string _lastQuery = "";
    private int _matchIndex = -1;
    private int _highlightedPara = -1;

    public UserGuideView()
    {
        InitializeComponent();
        BuildParagraphs();
    }

    private void BuildParagraphs()
    {
        // Split on blank lines; the guide already separates every item that way.
        foreach (var block in AppText.UserGuide.Replace("\r\n", "\n").Split("\n\n"))
        {
            string text = block.Trim('\n');
            if (text.Length == 0) continue;

            var tb = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Arial"),
                FontSize = 15,
                Foreground = BodyFg,
                Margin = new Avalonia.Thickness(0, 0, 0, 10),
            };
            ContentPanel.Children.Add(tb);
            _paragraphs.Add(tb);
            _paragraphText.Add(text);
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { RunFind(); e.Handled = true; }
    }

    private void OnFindNextClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => RunFind();

    private void OnClearClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SearchBox.Text = "";
        ClearHighlight();
        _matches.Clear();
        _lastQuery = "";
        _matchIndex = -1;
        SearchStatus.Text = "";
    }

    private void RunFind()
    {
        string query = SearchBox.Text?.Trim() ?? "";
        if (query.Length == 0) { OnClearClick(null, null!); return; }

        if (!string.Equals(query, _lastQuery, StringComparison.OrdinalIgnoreCase))
        {
            CollectMatches(query);
            _lastQuery = query;
            _matchIndex = _matches.Count > 0 ? 0 : -1;
        }
        else if (_matches.Count > 0)
        {
            _matchIndex = (_matchIndex + 1) % _matches.Count;   // cycle
        }

        if (_matches.Count == 0)
        {
            ClearHighlight();
            SearchStatus.Text = "No matches";
            return;
        }

        ApplyHighlight(_matches[_matchIndex], query.Length);
        SearchStatus.Text = $"{_matchIndex + 1} / {_matches.Count}";
    }

    private void CollectMatches(string query)
    {
        _matches.Clear();
        for (int p = 0; p < _paragraphText.Count; p++)
        {
            string hay = _paragraphText[p];
            int from = 0;
            while (true)
            {
                int i = hay.IndexOf(query, from, StringComparison.OrdinalIgnoreCase);
                if (i < 0) break;
                _matches.Add((p, i));
                from = i + query.Length;
            }
        }
    }

    private void ApplyHighlight((int Para, int Offset) match, int length)
    {
        ClearHighlight();

        var tb = _paragraphs[match.Para];
        string text = _paragraphText[match.Para];
        int off = Math.Min(match.Offset, text.Length);
        int len = Math.Min(length, text.Length - off);

        // Build the highlight out of the paragraph's own Inlines collection rather than assigning a
        // fresh one (its setter's null-handling differs across Avalonia versions). Setting Text to
        // "" first stops the plain text from also rendering alongside the runs.
        tb.Text = "";
        tb.Inlines!.Clear();
        tb.Inlines.Add(new Run(text[..off]));
        tb.Inlines.Add(new Run(text.Substring(off, len)) { Background = HighlightBg, Foreground = HighlightFg });
        tb.Inlines.Add(new Run(text[(off + len)..]));
        _highlightedPara = match.Para;

        tb.BringIntoView();
    }

    private void ClearHighlight()
    {
        if (_highlightedPara < 0) return;
        var tb = _paragraphs[_highlightedPara];
        tb.Inlines?.Clear();
        tb.Text = _paragraphText[_highlightedPara];
        _highlightedPara = -1;
    }
}
