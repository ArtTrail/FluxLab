using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SimpleFitsViewer.ViewModels;

namespace SimpleFitsViewer.Views;

public partial class MainWindow : Window
{
    private bool _isPanning;
    private Point _panStartPointer;
    private Vector _panStartOffset;

    private bool _draggingHistogramLine;
    private bool _draggingBlackLine;   // true = dragging the black line, false = white line

    // Left-click-on-image gesture: a plain click re-centroids; a real drag (past a small screen-
    // pixel threshold, so it works consistently regardless of zoom) moves the existing
    // aperture/annulus set instead, without resizing it. The decision is made lazily, since a
    // press alone can't tell which one the user means.
    private bool _leftButtonDown;       // true only for the duration of an actual left-button press on the image
    private bool _leftDragCandidate;    // an aperture already existed when the left button went down
    private bool _isDraggingAperture;   // the drag threshold has been crossed for this press
    private Point _leftPressWindowPos;  // screen-space press point, for the zoom-independent threshold check
    private Point _leftPressImagePos;   // image-pixel press point, for the move delta and the plain-click centroid call
    private (double X, double Y) _apertureCenterAtPress;
    private const double DragThresholdPx = 4.0;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.PropertyChanged += OnViewModelPropertyChanged;
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.BlackLevel) or nameof(MainWindowViewModel.WhiteLevel))
            UpdateHistogramLines();
    }

    // ── File open / Fit ──────────────────────────────────────────────────────

    private async void OnOpenClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open FITS File",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("FITS files") { Patterns = ["*.fits", "*.fit", "*.fts", "*.FITS", "*.FIT", "*.FTS"] },
                new("All files") { Patterns = ["*.*"] },
            },
        });

        if (files.Count > 0 && DataContext is MainWindowViewModel vm)
        {
            var path = files[0].TryGetLocalPath();
            if (path is not null)
            {
                vm.LoadFile(path);
                FitToWindow();
                UpdateHistogramLines();
            }
        }
    }

    private async void OnOpenDirectoryClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Directory — FITS Sequence",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && DataContext is MainWindowViewModel vm)
        {
            var path = folders[0].TryGetLocalPath();
            if (path is not null)
            {
                vm.LoadDirectory(path);
                FitToWindow();
                UpdateHistogramLines();
            }
        }
    }

    private void OnFitClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => FitToWindow();

    private void FitToWindow()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var viewport = ImageScroller.Viewport;
        vm.ZoomScale = vm.ComputeFitScale(viewport.Width, viewport.Height);
    }

    // ── Aperture click / pan / zoom-at-cursor on the image ──────────────────

    private void OnImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Image image || DataContext is not MainWindowViewModel vm) return;

        var point = e.GetCurrentPoint(image);
        if (point.Properties.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _panStartPointer = e.GetPosition(ImageScroller);
            _panStartOffset = ImageScroller.Offset;
            e.Pointer.Capture(ImageScroller);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        _leftButtonDown = true;
        _leftPressWindowPos = e.GetPosition(this);
        _leftPressImagePos = e.GetPosition(image);
        _isDraggingAperture = false;
        _leftDragCandidate = false;
        if (vm.ApertureVisible && vm.GetApertureCenter() is { } c)
        {
            _apertureCenterAtPress = c;
            _leftDragCandidate = true;
        }
        e.Pointer.Capture(image);
    }

    private void OnImagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Image image || DataContext is not MainWindowViewModel vm) return;
        if (!_leftDragCandidate) return;
        if (!e.GetCurrentPoint(image).Properties.IsLeftButtonPressed) return;

        if (!_isDraggingAperture)
        {
            var moved = e.GetPosition(this) - _leftPressWindowPos;
            if (Math.Sqrt(moved.X * moved.X + moved.Y * moved.Y) < DragThresholdPx) return;
            _isDraggingAperture = true;
        }

        var imagePos = e.GetPosition(image);
        double dx = imagePos.X - _leftPressImagePos.X;
        double dy = imagePos.Y - _leftPressImagePos.Y;
        vm.MoveApertureTo(_apertureCenterAtPress.X + dx, _apertureCenterAtPress.Y + dy);
    }

    private void OnImagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_leftButtonDown) return;

        if (!_isDraggingAperture && DataContext is MainWindowViewModel vm)
        {
            // No real drag happened -- treat as a plain click: re-centroid at the press point.
            vm.OnImageClicked(_leftPressImagePos.X, _leftPressImagePos.Y);
        }
        _leftButtonDown = false;
        _leftDragCandidate = false;
        _isDraggingAperture = false;
        e.Pointer.Capture(null);
    }

    private void OnScrollerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning) return;
        var cur = e.GetPosition(ImageScroller);
        var delta = cur - _panStartPointer;
        ImageScroller.Offset = new Vector(
            Math.Clamp(_panStartOffset.X - delta.X, 0, Math.Max(0, ImageScroller.Extent.Width - ImageScroller.Viewport.Width)),
            Math.Clamp(_panStartOffset.Y - delta.Y, 0, Math.Max(0, ImageScroller.Extent.Height - ImageScroller.Viewport.Height)));
    }

    private void OnScrollerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
        }
    }

    private void OnScrollerPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var viewportPos = e.GetPosition(ImageScroller);
        double oldScale = vm.ZoomScale;
        double factor = e.Delta.Y > 0 ? 1.15 : 1.0 / 1.15;
        double newScale = Math.Clamp(oldScale * factor, 0.02, 20.0);
        if (Math.Abs(newScale - oldScale) < 1e-9) { e.Handled = true; return; }

        var offset = ImageScroller.Offset;
        // Content-space (raw image pixel) point currently under the cursor, using the OLD scale.
        double contentX = (offset.X + viewportPos.X) / oldScale;
        double contentY = (offset.Y + viewportPos.Y) / oldScale;

        vm.ZoomScale = newScale;

        // LayoutTransformControl needs a layout pass to report the new (scaled) size to the
        // ScrollViewer before Extent reflects it -- defer the offset fix-up until then, so the
        // same content point stays under the cursor instead of the view drifting.
        Dispatcher.UIThread.Post(() =>
        {
            double newX = contentX * newScale - viewportPos.X;
            double newY = contentY * newScale - viewportPos.Y;
            ImageScroller.Offset = new Vector(
                Math.Clamp(newX, 0, Math.Max(0, ImageScroller.Extent.Width - ImageScroller.Viewport.Width)),
                Math.Clamp(newY, 0, Math.Max(0, ImageScroller.Extent.Height - ImageScroller.Viewport.Height)));
        }, DispatcherPriority.Loaded);

        e.Handled = true;
    }

    // ── Histogram black/white line dragging ─────────────────────────────────

    private void OnHistogramOverlaySizeChanged(object? sender, SizeChangedEventArgs e) => UpdateHistogramLines();

    private void UpdateHistogramLines()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        double w = HistogramOverlay.Bounds.Width;
        double h = HistogramOverlay.Bounds.Height;
        if (w <= 0 || h <= 0) return;

        double bx = vm.BlackLevel * w;
        double wx = vm.WhiteLevel * w;
        BlackLine.StartPoint = new Point(bx, 0);
        BlackLine.EndPoint = new Point(bx, h);
        WhiteLine.StartPoint = new Point(wx, 0);
        WhiteLine.EndPoint = new Point(wx, h);
    }

    private void OnHistogramPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        double w = HistogramOverlay.Bounds.Width;
        if (w <= 0) return;

        var x = e.GetPosition(HistogramOverlay).X;
        double blackX = vm.BlackLevel * w;
        double whiteX = vm.WhiteLevel * w;
        _draggingBlackLine = Math.Abs(x - blackX) <= Math.Abs(x - whiteX);
        _draggingHistogramLine = true;
        e.Pointer.Capture(HistogramOverlay);
        DragHistogramLineTo(x, w);
    }

    private void OnHistogramPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_draggingHistogramLine) return;
        double w = HistogramOverlay.Bounds.Width;
        if (w <= 0) return;
        DragHistogramLineTo(e.GetPosition(HistogramOverlay).X, w);
    }

    private void OnHistogramPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_draggingHistogramLine)
        {
            _draggingHistogramLine = false;
            e.Pointer.Capture(null);
        }
    }

    private void DragHistogramLineTo(double x, double w)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        double frac = Math.Clamp(x / w, 0.0, 1.0);
        if (_draggingBlackLine) vm.BlackLevel = Math.Min(frac, vm.WhiteLevel);
        else vm.WhiteLevel = Math.Max(frac, vm.BlackLevel);
    }

    // ── Menu: Tools / Help ──────────────────────────────────────────────────

    private void OnFitsHeaderClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (vm.CurrentFilePath is null)
        {
            vm.InstructionText = "Open a FITS file first, then Tools > FITS Header.";
            return;
        }
        var headerVm = new ViewModels.HeaderWindowViewModel(vm.CurrentHeader, vm.CurrentFilePath);
        new HeaderWindow { DataContext = headerVm }.Show();
    }

    private void OnDiagnosticsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => new DiagnosticsWindow().Show();

    private void OnUserGuideClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => new InfoWindow("User Guide", AppText.UserGuide).Show();

    private void OnRevisionHistoryClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => new InfoWindow("Revision History", AppText.RevisionHistory).Show();

    private void OnAboutClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => new InfoWindow("About", AppText.About).Show();
}
