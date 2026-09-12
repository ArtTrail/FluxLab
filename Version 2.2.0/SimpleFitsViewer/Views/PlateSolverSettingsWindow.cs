using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SimpleFitsViewer.Services;

namespace SimpleFitsViewer.Views;

/// <summary>
/// Tools > Plate Solver: view and override where FluxLab finds StarFix's solver and Gaia catalog.
/// Both fields are optional -- left blank, FluxLab auto-discovers an installed StarFix; a path here
/// pins an explicit location. Shows live whether each is currently found so the user gets immediate
/// feedback rather than only discovering a bad path when they press Plate Solve.
/// </summary>
public class PlateSolverSettingsWindow : Window
{
    private readonly SolverConfig _cfg;
    private readonly TextBox _solverBox;
    private readonly TextBox _catalogBox;
    private readonly TextBlock _status;

    public PlateSolverSettingsWindow()
    {
        _cfg = SolverConfig.Load();

        Title = "Plate Solver";
        Width = 680;
        Height = 360;
        CanResize = false;
        Background = new SolidColorBrush(Color.Parse("#2e3440"));

        _solverBox = Field(_cfg.SolverExePath);
        _catalogBox = Field(_cfg.CatalogDir);
        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Arial"), FontSize = 13,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
        };

        var panel = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 6 };
        panel.Children.Add(Heading("Plate Solver paths"));
        panel.Children.Add(Hint("Leave blank to auto-detect an installed StarFix. Set a path to pin "
                              + "an explicit location (e.g. a catalog kept on another drive)."));

        panel.Children.Add(Label("StarFix solver (solve.exe)"));
        panel.Children.Add(PathRow(_solverBox, browseFolder: false));
        panel.Children.Add(Label("Gaia catalog folder"));
        panel.Children.Add(PathRow(_catalogBox, browseFolder: true));
        panel.Children.Add(_status);

        var saveBtn = new Button { Content = "Save", Padding = new Avalonia.Thickness(16, 4) };
        saveBtn.Click += (_, _) => { Save(); RefreshStatus(); };
        var closeBtn = new Button
        {
            Content = "Close", Padding = new Avalonia.Thickness(16, 4), Margin = new Avalonia.Thickness(8, 0, 0, 0),
        };
        closeBtn.Click += (_, _) => Close();
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 12, 0, 0),
        };
        btnRow.Children.Add(saveBtn);
        btnRow.Children.Add(closeBtn);
        panel.Children.Add(btnRow);

        Content = new ScrollViewer { Content = panel };
        RefreshStatus();
    }

    private static TextBlock Heading(string t) => new()
    {
        Text = t, FontFamily = new FontFamily("Arial"), FontSize = 20, FontWeight = FontWeight.Bold,
        Foreground = new SolidColorBrush(Color.Parse("#88c0d0")),
    };
    private static TextBlock Hint(string t) => new()
    {
        Text = t, FontFamily = new FontFamily("Arial"), FontSize = 13, FontStyle = FontStyle.Italic,
        Foreground = new SolidColorBrush(Color.Parse("#8892a0")), TextWrapping = TextWrapping.Wrap,
        Margin = new Avalonia.Thickness(0, 0, 0, 8),
    };
    private static TextBlock Label(string t) => new()
    {
        Text = t, FontFamily = new FontFamily("Arial"), FontSize = 13,
        Foreground = new SolidColorBrush(Color.Parse("#eceff4")), Margin = new Avalonia.Thickness(0, 6, 0, 0),
    };
    private static TextBox Field(string? v) => new()
    {
        Text = v ?? "", Watermark = "(auto-detect)", FontFamily = new FontFamily("Arial"),
    };

    private Control PathRow(TextBox box, bool browseFolder)
    {
        var browse = new Button { Content = "Browse…", Margin = new Avalonia.Thickness(6, 0, 0, 0) };
        browse.Click += async (_, _) => await BrowseInto(box, browseFolder);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(box, 0);
        Grid.SetColumn(browse, 1);
        grid.Children.Add(box);
        grid.Children.Add(browse);
        return grid;
    }

    private async Task BrowseInto(TextBox box, bool folder)
    {
        var top = GetTopLevel(this);
        if (top is null) return;
        if (folder)
        {
            var dirs = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select the Gaia catalog folder", AllowMultiple = false,
            });
            if (dirs.Count > 0) box.Text = dirs[0].TryGetLocalPath() ?? box.Text;
        }
        else
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select StarFix's solver executable", AllowMultiple = false,
            });
            if (files.Count > 0) box.Text = files[0].TryGetLocalPath() ?? box.Text;
        }
    }

    private void Save()
    {
        _cfg.SolverExePath = string.IsNullOrWhiteSpace(_solverBox.Text) ? null : _solverBox.Text.Trim();
        _cfg.CatalogDir = string.IsNullOrWhiteSpace(_catalogBox.Text) ? null : _catalogBox.Text.Trim();
        _cfg.Save();
    }

    private void RefreshStatus()
    {
        var loc = SolverLocatorService.Locate(_cfg);
        string solver = loc.SolverFound ? $"found: {loc.SolverExe}" : "NOT found";
        string catalog = loc.CatalogFound ? $"found: {loc.CatalogDir}" : "NOT found";
        _status.Text = $"Solver: {solver}\nCatalog: {catalog}\n"
                     + (loc.Ready ? "Ready to plate solve." : "Plate solving is unavailable until both are found.");
        _status.Foreground = new SolidColorBrush(Color.Parse(loc.Ready ? "#a3be8c" : "#ebcb8b"));
    }
}
