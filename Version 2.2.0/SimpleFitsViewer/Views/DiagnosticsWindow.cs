using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using SimpleFitsViewer.Services;

namespace SimpleFitsViewer.Views;

/// <summary>Tools > Diagnostics: a live view of DiagnosticsLog.Entries with Save/Clear, matching
/// the diagnostics/error-log pattern already used in TransitLab/TransitFinder/VariLab.</summary>
public class DiagnosticsWindow : Window
{
    public DiagnosticsWindow()
    {
        Title = "Diagnostics";
        Width = 720;
        Height = 520;
        Background = new SolidColorBrush(Color.Parse("#1e1e2e"));

        var entryFg = new SolidColorBrush(Color.Parse("#d8d8f0"));
        var entryFont = new FontFamily("Consolas,Menlo,monospace");
        var listBox = new ListBox
        {
            ItemsSource = DiagnosticsLog.Entries,
            Background = new SolidColorBrush(Color.Parse("#141420")),
            Margin = new Avalonia.Thickness(12),
            ItemTemplate = new FuncDataTemplate<string>((text, _) => new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = entryFg,
                FontFamily = entryFont,
                FontSize = 12,
            }, supportsRecycling: true),
        };
        // Forbid horizontal scrolling and stretch each row to the list width, so the wrapping
        // TextBlock wraps at the window width instead of running off the right edge.
        ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled);
        listBox.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) },
        });

        var saveButton = new Button { Content = "Save Log", Padding = new Avalonia.Thickness(12, 4) };
        saveButton.Click += async (_, _) => await SaveLogAsync();

        var clearButton = new Button
        {
            Content = "Clear", Padding = new Avalonia.Thickness(12, 4),
            Background = new SolidColorBrush(Color.Parse("#6a1a1a")), Foreground = Brushes.White,
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
        };
        clearButton.Click += (_, _) => DiagnosticsLog.Entries.Clear();

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(12, 0, 12, 12),
        };
        buttonRow.Children.Add(saveButton);
        buttonRow.Children.Add(clearButton);

        var titleBar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1b3f6b")),
            Padding = new Avalonia.Thickness(0, 8),
            Child = new TextBlock
            {
                Text = "Diagnostics", Foreground = Brushes.White, FontWeight = FontWeight.Bold,
                FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center,
            },
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(listBox, 1);
        Grid.SetRow(buttonRow, 2);
        root.Children.Add(titleBar);
        root.Children.Add(listBox);
        root.Children.Add(buttonRow);

        Content = root;
    }

    private async System.Threading.Tasks.Task SaveLogAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Diagnostics Log",
            SuggestedFileName = $"SimpleFitsViewer_Diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            FileTypeChoices = new List<FilePickerFileType> { new("Text file") { Patterns = ["*.txt"] } },
        });
        if (file is null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            using var writer = new System.IO.StreamWriter(stream);
            foreach (var line in DiagnosticsLog.Entries)
                await writer.WriteLineAsync(line);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.LogException("Saving diagnostics log", ex);
        }
    }
}
