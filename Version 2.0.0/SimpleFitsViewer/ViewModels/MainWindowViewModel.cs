using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitsPhotometry.Core;
using FitsPhotometry.Core.Camera;
using FitsPhotometry.Core.Fits;
using FitsPhotometry.Core.Meter;
using FitsPhotometry.Core.Photometry;
using SimpleFitsViewer.Services;

namespace SimpleFitsViewer.ViewModels;

/// <summary>
/// Minimal v2.0.0 viewer/aperture-photometry ViewModel -- proves the FitsPhotometry.Core
/// pipeline end to end (open -> display -> click aperture -> Exposure Meter). Feature parity
/// with the Python v1.4.0 app (histogram editing, WCS readout, header editor, sequence
/// playback, help-icon popups) is intentionally deferred to follow-up passes.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private static readonly string ProfilePath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "camera_profile.json");
    private static readonly string LibraryPath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "camera_profile_library.json");

    private readonly List<NamedCameraProfile> _library;

    private float[] _pixels = [];
    private int _width;
    private int _height;
    private double _dataMin, _dataMax;
    private bool _suppressStretchRender;   // true while auto-stretch sets both levels at once
    private bool _suppressProfileSync;     // true while SyncProfileTextFromResolved is pushing loaded/resolved values into the Text properties
    private bool _suppressGeometrySync;    // true while SyncGeometryTextFromCurrent is pushing centroid/drag-derived values into the Text properties
    private FitsHeader _header = new(new());
    private string? _currentFilePath;
    private readonly CameraProfile _profile;

    // Directory sequence playback -- a single opened file is just a length-1 "sequence".
    private List<string> _sequenceFiles = [];
    private int _currentFrameIndex;
    private bool _suppressFrameSliderSync;
    private CancellationTokenSource? _frameSliderDebounceCts;
    private DispatcherTimer? _playTimer;
    private (double X, double Y)? _apertureCenter;
    private double _apertureRadius = 12.0;
    private double _annulusInner = 18.0;
    private double _annulusOuter = 28.0;

    public MainWindowViewModel()
    {
        _profile = LoadProfile();
        _library = LoadLibrary();
        foreach (var p in _library) ProfileNames.Add(p.Name);
        SyncProfileTextFromResolved();
        SyncGeometryTextFromCurrent();
    }

    /// <summary>Saved camera setups the user can pick from, e.g. "ASI183MM Pro - gain 111" --
    /// complements (doesn't replace) the automatic last-used persistence above. Kept as a plain
    /// ObservableCollection, not [ObservableProperty]: the ComboBox reacts to its own
    /// Add/Remove/collection-changed notifications directly.</summary>
    public ObservableCollection<string> ProfileNames { get; } = [];

    [ObservableProperty] private string? _selectedProfileName;
    [ObservableProperty] private string _saveAsNameText = "";

    private static List<NamedCameraProfile> LoadLibrary()
    {
        try { return CameraProfileLibrary.LoadFromJson(LibraryPath); }
        catch (Exception ex) { DiagnosticsLog.LogException("Loading camera profile library", ex); return []; }
    }

    private void SaveLibrary()
    {
        try { CameraProfileLibrary.SaveToJson(LibraryPath, _library); }
        catch (Exception ex) { DiagnosticsLog.LogException("Saving camera profile library", ex); }
    }

    [RelayCommand]
    private void LoadSelectedProfile()
    {
        var p = _library.FirstOrDefault(x => x.Name == SelectedProfileName);
        if (p is null) return;

        _profile.GainEPerAdu = p.GainEPerAdu;
        _profile.AduScale = p.AduScale;
        _profile.FullWellElectrons = p.FullWellElectrons;
        _profile.TargetElectrons = p.TargetElectrons;
        _profile.GainSource = "manual entry";
        _profile.AduScaleSource = "manual entry";
        SyncProfileTextFromResolved();
        SaveProfile();
        SaveAsNameText = p.Name;
        Recompute();
    }

    [RelayCommand]
    private void SaveAsProfile()
    {
        var name = SaveAsNameText.Trim();
        if (name.Length == 0) return;

        var existing = _library.FirstOrDefault(x => x.Name == name);
        var entry = existing ?? new NamedCameraProfile { Name = name };
        entry.GainEPerAdu = _profile.GainEPerAdu;
        entry.AduScale = _profile.AduScale;
        entry.FullWellElectrons = _profile.FullWellElectrons;
        entry.TargetElectrons = _profile.TargetElectrons;

        if (existing is null)
        {
            _library.Add(entry);
            ProfileNames.Add(name);
        }
        SaveLibrary();
        SelectedProfileName = name;
    }

    [RelayCommand]
    private void DeleteSelectedProfile()
    {
        var p = _library.FirstOrDefault(x => x.Name == SelectedProfileName);
        if (p is null) return;
        _library.Remove(p);
        ProfileNames.Remove(p.Name);
        SaveLibrary();
        SelectedProfileName = null;
    }

    /// <summary>Gain/ADU-scale/Full-well/Target-electrons persisted across app restarts --
    /// the same CameraProfile JSON format Core exposes for eventual NINA-plugin sharing (see
    /// PhotometryEngine's design notes), used here just to remember the desktop app's own
    /// last-entered values.</summary>
    private static CameraProfile LoadProfile()
    {
        try
        {
            if (System.IO.File.Exists(ProfilePath))
                return CameraProfile.LoadFromJson(ProfilePath);
        }
        catch (Exception ex)
        {
            // Corrupt or unreadable profile file -- fall back to defaults rather than crash.
            DiagnosticsLog.LogException("Loading camera profile", ex);
        }
        return new CameraProfile();
    }

    private void SaveProfile()
    {
        try { _profile.SaveToJson(ProfilePath); }
        catch (Exception ex) { DiagnosticsLog.LogException("Saving camera profile", ex); }
    }

    [ObservableProperty] private Bitmap? _displayImage;
    [ObservableProperty] private string _fileNameText = "No file loaded";
    [ObservableProperty] private string _instructionText = "Open a FITS file, then click a star to place an aperture.";
    [ObservableProperty] private double _zoomScale = 1.0;

    // Histogram / stretch. Fractions of [_dataMin, _dataMax], matching the Python app's
    // Black/White level slider convention.
    [ObservableProperty] private Bitmap? _histogramImage;
    [ObservableProperty] private double _blackLevel = 0.0;
    [ObservableProperty] private double _whiteLevel = 1.0;

    // Aperture/annulus overlay geometry (image pixel coords -- the view applies the same zoom
    // transform to the overlay canvas as the image itself, so these don't need to know the
    // current zoom level).
    [ObservableProperty] private bool _apertureVisible;
    [ObservableProperty] private double _apertureLeft, _apertureTop, _apertureSize;
    [ObservableProperty] private double _annulusInnerLeft, _annulusInnerTop, _annulusInnerSize;
    [ObservableProperty] private double _annulusOuterLeft, _annulusOuterTop, _annulusOuterSize;

    // Manual aperture/annulus size entry -- mirrors Python's old "Geometry (pixels)" spinbox
    // fields. Editing any of these (by typing or via the NumericUpDown's spinner arrows)
    // re-renders the overlay and recomputes photometry immediately, without moving the aperture
    // center or re-running auto-centroid.
    [ObservableProperty] private decimal? _apertureRadiusValue;
    [ObservableProperty] private decimal? _annulusInnerValue;
    [ObservableProperty] private decimal? _annulusOuterValue;

    // Directory sequence playback
    [ObservableProperty] private string _frameLabelText = "Frame: -/-";
    [ObservableProperty] private int _frameSliderValue;
    [ObservableProperty] private int _frameSliderMaximum;
    [ObservableProperty] private bool _hasSequence;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string _playButtonGlyph = "▶";   // ▶

    // Gain & Scaling
    [ObservableProperty] private string _gainText = "";
    [ObservableProperty] private string _aduScaleText = "";
    [ObservableProperty] private string _fullWellText = "";
    [ObservableProperty] private string _targetElectronsText = "100000";

    // Results
    [ObservableProperty] private string _centerText = "—";
    [ObservableProperty] private string _apertureCountText = "—";
    [ObservableProperty] private string _skyMedianText = "—";
    [ObservableProperty] private string _skySigmaText = "—";
    [ObservableProperty] private string _peakText = "—";
    [ObservableProperty] private string _aduScaleSourceText = "—";
    [ObservableProperty] private string _gainSourceText = "—";
    [ObservableProperty] private string _totalElectronsText = "—";

    // Exposure Meter
    [ObservableProperty] private string _meterStateText = "—";
    [ObservableProperty] private string _meterDetailText = "";
    [ObservableProperty] private string _meterRecommendationText = "";
    [ObservableProperty] private IBrush _meterColor = Brushes.White;

    /// <summary>Opens a single file fresh -- resets the aperture and sequence state, then
    /// auto-stretches. Use LoadDirectory to open a folder as a steppable sequence instead.</summary>
    public void LoadFile(string path)
    {
        StopPlayback();
        _sequenceFiles = [path];
        _currentFrameIndex = 0;
        HasSequence = false;
        FrameSliderMaximum = 0;

        if (!LoadFileData(path)) return;

        _apertureCenter = null;
        RenderHistogram();
        AutoStretch();
        ClearResults();
        SyncFrameSliderTo(0);
        UpdateFrameLabel();
    }

    /// <summary>Opens every .fits/.fit/.fts file in a directory (sorted by name) as a steppable
    /// sequence, matching the Python app's "Open Directory (Sequence)" -- resets the aperture and
    /// auto-stretches on the first frame, same as opening a single file fresh.</summary>
    public void LoadDirectory(string directory)
    {
        string[] exts = [".fits", ".fit", ".fts"];
        List<string> files;
        try
        {
            files = System.IO.Directory.EnumerateFiles(directory)
                .Where(f => exts.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.LogException($"Opening directory: {directory}", ex);
            InstructionText = $"Error opening directory: {ex.Message}";
            return;
        }
        if (files.Count == 0)
        {
            InstructionText = $"No FITS files found in: {directory}";
            return;
        }

        StopPlayback();
        _sequenceFiles = files;
        _currentFrameIndex = 0;
        HasSequence = files.Count > 1;
        FrameSliderMaximum = Math.Max(0, files.Count - 1);

        if (!LoadFileData(files[0])) return;

        _apertureCenter = null;
        RenderHistogram();
        AutoStretch();
        ClearResults();
        SyncFrameSliderTo(0);
        UpdateFrameLabel();
    }

    /// <summary>Steps to another frame within the current sequence, keeping the aperture and the
    /// current black/white stretch fractions -- so an already-placed aperture keeps re-measuring
    /// automatically as you step through frames from the same field of view, matching the Python
    /// app's design (see its _apply_loaded_fits comment on this exact point).</summary>
    private void GoToFrame(int index)
    {
        if (index < 0 || index >= _sequenceFiles.Count) return;
        _currentFrameIndex = index;
        if (!LoadFileData(_sequenceFiles[index])) return;

        RenderHistogram();
        RenderImage();
        if (_apertureCenter is not null) Recompute();
        SyncFrameSliderTo(index);
        UpdateFrameLabel();
    }

    /// <summary>Reads pixel/header data and resolves gain/ADU-scale for one file, with no side
    /// effects on aperture, stretch, or sequence state -- the piece LoadFile/LoadDirectory/
    /// GoToFrame all share. Returns false (and leaves prior state untouched) on failure.</summary>
    private bool LoadFileData(string path)
    {
        FitsImage.Loaded loaded;
        try
        {
            loaded = FitsImage.Load(path);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.LogException($"Loading FITS file: {path}", ex);
            InstructionText = $"Error loading {System.IO.Path.GetFileName(path)}: {ex.Message}";
            return false;
        }
        if (loaded.Width == 0 || loaded.Height == 0)
        {
            InstructionText = $"No image data in: {path}";
            return false;
        }

        _pixels = loaded.Pixels;
        _width = loaded.Width;
        _height = loaded.Height;
        _header = FitsHeader.Read(path);
        _currentFilePath = path;

        var finite = Array.FindAll(_pixels, float.IsFinite);
        if (finite.Length == 0) finite = _pixels;
        _dataMin = finite.Length > 0 ? finite.Min() : 0.0;
        _dataMax = finite.Length > 0 ? finite.Max() : 1.0;

        CameraProfileResolver.ResolveAduScale(_profile, _header, _pixels);
        CameraProfileResolver.ResolveGain(_profile, _header);
        SyncProfileTextFromResolved();

        FileNameText = System.IO.Path.GetFileName(path);
        return true;
    }

    private void SyncFrameSliderTo(int index)
    {
        _suppressFrameSliderSync = true;
        FrameSliderValue = index;
        _suppressFrameSliderSync = false;
    }

    private void UpdateFrameLabel()
        => FrameLabelText = _sequenceFiles.Count == 0 ? "Frame: -/-" : $"Frame: {_currentFrameIndex + 1}/{_sequenceFiles.Count}";

    partial void OnFrameSliderValueChanged(int value)
    {
        if (_suppressFrameSliderSync) return;
        if (value < 0 || value >= _sequenceFiles.Count) return;
        StopPlayback();
        FrameLabelText = $"Frame: {value + 1}/{_sequenceFiles.Count}";

        // Debounce: only actually load the frame after dragging pauses for 150ms, matching the
        // Python app's slider -- otherwise every tick of a fast drag triggers a full file load.
        _frameSliderDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _frameSliderDebounceCts = cts;
        _ = DebouncedSeekAsync(value, cts.Token);
    }

    private async Task DebouncedSeekAsync(int index, CancellationToken token)
    {
        try { await Task.Delay(150, token); }
        catch (OperationCanceledException) { return; }
        if (!token.IsCancellationRequested) GoToFrame(index);
    }

    [RelayCommand] private void FrameFirst() { StopPlayback(); GoToFrame(0); }
    [RelayCommand] private void FrameLast() { StopPlayback(); GoToFrame(_sequenceFiles.Count - 1); }
    [RelayCommand] private void FramePrev() { StopPlayback(); GoToFrame(_currentFrameIndex - 1); }
    [RelayCommand] private void FrameNext() { StopPlayback(); GoToFrame(_currentFrameIndex + 1); }

    /// <summary>Play/pause a 1fps loop through the sequence, wrapping around at the end --
    /// matches the Python app's fixed playback rate and wrap-around behavior.</summary>
    [RelayCommand]
    private void TogglePlay()
    {
        if (IsPlaying) { StopPlayback(); return; }
        if (_sequenceFiles.Count < 2) return;

        IsPlaying = true;
        PlayButtonGlyph = "⏸";
        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _playTimer.Tick += (_, _) => GoToFrame((_currentFrameIndex + 1) % _sequenceFiles.Count);
        _playTimer.Start();
    }

    private void StopPlayback()
    {
        _playTimer?.Stop();
        _playTimer = null;
        IsPlaying = false;
        PlayButtonGlyph = "▶";
    }

    /// <summary>1st/99th-percentile stretch, matching the Python app's Auto Stretch button.</summary>
    [RelayCommand]
    private void AutoStretch()
    {
        if (_pixels.Length == 0) return;
        var finite = Array.FindAll(_pixels, float.IsFinite);
        if (finite.Length == 0) return;
        Array.Sort(finite);
        double lo = finite[(int)(finite.Length * 0.01)];
        double hi = finite[(int)(finite.Length * 0.99)];
        double range = Math.Max(_dataMax - _dataMin, 1e-10);

        _suppressStretchRender = true;
        BlackLevel = Math.Clamp((lo - _dataMin) / range, 0.0, 1.0);
        _suppressStretchRender = false;
        WhiteLevel = Math.Clamp((hi - _dataMin) / range, 0.0, 1.0);
        RenderImage();
    }

    /// <summary>Called by the view when the user clicks on the displayed image, in native
    /// image-pixel coordinates (0,0 = top-left).</summary>
    public void OnImageClicked(double px, double py)
    {
        if (_pixels.Length == 0) return;

        var found = StarCentroid.TryCentroid(_pixels, _width, _height, px, py);
        if (found is { } r)
        {
            _apertureCenter = (r.CenterX, r.CenterY);
            _apertureRadius = r.Radius;
            _annulusInner = r.AnnulusInner;
            _annulusOuter = r.AnnulusOuter;
        }
        else
        {
            // No star-like signal near the click -- just recenter, keep current geometry,
            // matching the Python app's behavior.
            _apertureCenter = (px, py);
        }

        SyncGeometryTextFromCurrent();
        UpdateApertureOverlay();
        Recompute();
    }

    /// <summary>Current aperture center, for the view's click-vs-drag gesture handling on the
    /// image (it needs a starting point to compute a drag delta from).</summary>
    public (double X, double Y)? GetApertureCenter() => _apertureCenter;

    /// <summary>The currently loaded file's header and path, for the FITS Header viewer/editor
    /// window -- null path means no file is loaded (the header is an empty placeholder then).</summary>
    public FitsHeader CurrentHeader => _header;
    public string? CurrentFilePath => _currentFilePath;

    /// <summary>Repositions the existing aperture/annulus set to a new center, keeping all three
    /// radii unchanged -- called by the view while the user drags an already-placed aperture,
    /// as opposed to a plain click which re-centroids and re-sizes.</summary>
    public void MoveApertureTo(double newCenterX, double newCenterY)
    {
        if (_apertureCenter is null) return;
        _apertureCenter = (newCenterX, newCenterY);
        UpdateApertureOverlay();
        Recompute();
    }

    private void UpdateApertureOverlay()
    {
        if (_apertureCenter is not { } c)
        {
            ApertureVisible = false;
            return;
        }
        ApertureVisible = true;
        (ApertureLeft, ApertureTop, ApertureSize) = CircleBounds(c.X, c.Y, _apertureRadius);
        (AnnulusInnerLeft, AnnulusInnerTop, AnnulusInnerSize) = CircleBounds(c.X, c.Y, _annulusInner);
        (AnnulusOuterLeft, AnnulusOuterTop, AnnulusOuterSize) = CircleBounds(c.X, c.Y, _annulusOuter);
    }

    private static (double Left, double Top, double Size) CircleBounds(double cx, double cy, double radius)
        => (cx - radius, cy - radius, radius * 2.0);

    /// <summary>Fit-to-window scale for the current image given the available viewport size, or
    /// 1.0 if there's no image loaded yet. Called from the view's code-behind, which is the one
    /// side that knows the actual viewport dimensions.</summary>
    public double ComputeFitScale(double viewportWidth, double viewportHeight)
    {
        if (_width == 0 || _height == 0 || viewportWidth <= 0 || viewportHeight <= 0) return 1.0;
        return Math.Min(viewportWidth / _width, viewportHeight / _height);
    }

    [RelayCommand]
    private void ZoomIn() => ZoomScale = Math.Min(ZoomScale * 1.25, 20.0);

    [RelayCommand]
    private void ZoomOut() => ZoomScale = Math.Max(ZoomScale * 0.8, 0.02);

    [RelayCommand]
    private void ZoomOneToOne() => ZoomScale = 1.0;

    // Fine-adjustment step for the black/white level +/- buttons -- finer than a typical
    // slider-drag increment (1% of the full [_dataMin, _dataMax] range per click).
    private const double LevelStep = 0.01;

    [RelayCommand]
    private void BlackDown() => BlackLevel = Math.Clamp(BlackLevel - LevelStep, 0.0, WhiteLevel);

    [RelayCommand]
    private void BlackUp() => BlackLevel = Math.Clamp(BlackLevel + LevelStep, 0.0, WhiteLevel);

    [RelayCommand]
    private void WhiteDown() => WhiteLevel = Math.Clamp(WhiteLevel - LevelStep, BlackLevel, 1.0);

    [RelayCommand]
    private void WhiteUp() => WhiteLevel = Math.Clamp(WhiteLevel + LevelStep, BlackLevel, 1.0);

    /// <summary>
    /// Pushes the resolved/loaded CameraProfile values into their Text properties. Guarded by
    /// _suppressProfileSync: without it, setting GainText here (first) fires
    /// OnGainTextChanged -> ApplyProfileFieldsFromText, which re-reads AduScaleText/FullWellText
    /// at their *pre-sync* values (still "" at startup, or whatever was displayed for the
    /// previous file) and immediately saves -- wiping out the very AduScale/FullWellElectrons
    /// values this method is about to display, before their own lines below ever run. Confirmed
    /// as the actual cause of ADU scale/Full well silently failing to persist: it fires any time
    /// GainText's displayed value actually changes, which is most file opens across a session.
    /// </summary>
    private void SyncProfileTextFromResolved()
    {
        _suppressProfileSync = true;
        GainText = _profile.GainEPerAdu?.ToString("G6") ?? GainText;
        AduScaleText = _profile.AduScale?.ToString("G6") ?? "";
        FullWellText = _profile.FullWellElectrons?.ToString("G6") ?? FullWellText;
        TargetElectronsText = _profile.TargetElectrons.ToString("G6");
        GainSourceText = string.IsNullOrEmpty(_profile.GainSource) ? "manual entry" : _profile.GainSource;
        AduScaleSourceText = string.IsNullOrEmpty(_profile.AduScaleSource) ? "manual entry" : _profile.AduScaleSource;
        _suppressProfileSync = false;
    }

    partial void OnBlackLevelChanged(double value) { if (!_suppressStretchRender) RenderImage(); }
    partial void OnWhiteLevelChanged(double value) { if (!_suppressStretchRender) RenderImage(); }

    partial void OnGainTextChanged(string value) { if (_suppressProfileSync) return; ApplyProfileFieldsFromText(); Recompute(); }
    partial void OnAduScaleTextChanged(string value) { if (_suppressProfileSync) return; ApplyProfileFieldsFromText(); Recompute(); }
    partial void OnFullWellTextChanged(string value) { if (_suppressProfileSync) return; ApplyProfileFieldsFromText(); Recompute(); }
    partial void OnTargetElectronsTextChanged(string value) { if (_suppressProfileSync) return; ApplyProfileFieldsFromText(); Recompute(); }

    private void ApplyProfileFieldsFromText()
    {
        _profile.GainEPerAdu = NumericParse.TryParse(GainText, out var g) ? g : null;
        _profile.AduScale = NumericParse.TryParse(AduScaleText, out var a) ? a : null;
        _profile.FullWellElectrons = NumericParse.TryParse(FullWellText, out var fw) ? fw : null;
        _profile.TargetElectrons = NumericParse.TryParse(TargetElectronsText, out var t) ? t : 100_000;
        SaveProfile();
    }

    partial void OnApertureRadiusValueChanged(decimal? value) { if (_suppressGeometrySync) return; ApplyGeometryFieldsFromValues(); }
    partial void OnAnnulusInnerValueChanged(decimal? value) { if (_suppressGeometrySync) return; ApplyGeometryFieldsFromValues(); }
    partial void OnAnnulusOuterValueChanged(decimal? value) { if (_suppressGeometrySync) return; ApplyGeometryFieldsFromValues(); }

    private void ApplyGeometryFieldsFromValues()
    {
        if (ApertureRadiusValue is { } r && r > 0) _apertureRadius = (double)r;
        if (AnnulusInnerValue is { } ri && ri > 0) _annulusInner = (double)ri;
        if (AnnulusOuterValue is { } ro && ro > 0) _annulusOuter = (double)ro;
        UpdateApertureOverlay();
        Recompute();
    }

    /// <summary>Pushes the current radii into their bound Value properties, guarded the same way
    /// as SyncProfileTextFromResolved -- otherwise setting ApertureRadiusValue here would fire
    /// OnApertureRadiusValueChanged -> ApplyGeometryFieldsFromValues, which re-reads
    /// AnnulusInnerValue/AnnulusOuterValue at their stale pre-sync values.</summary>
    private void SyncGeometryTextFromCurrent()
    {
        _suppressGeometrySync = true;
        ApertureRadiusValue = (decimal)_apertureRadius;
        AnnulusInnerValue = (decimal)_annulusInner;
        AnnulusOuterValue = (decimal)_annulusOuter;
        _suppressGeometrySync = false;
    }

    private void Recompute()
    {
        if (_apertureCenter is not { } c || _pixels.Length == 0)
        {
            ClearResults();
            return;
        }

        var aperture = new ApertureGeometry(c.X, c.Y, _apertureRadius, _annulusInner, _annulusOuter);
        var result = PhotometryEngine.Analyze(_pixels, _width, _height, _header, aperture, _profile);
        if (result is null)
        {
            ClearResults();
            return;
        }

        CenterText = $"{c.X:F1}, {c.Y:F1}";
        ApertureCountText = result.Aperture.NAperturePixels.ToString();
        SkyMedianText = result.Aperture.SkyMedian.ToString("F3");
        SkySigmaText = result.Aperture.SkySigma.ToString("F3");
        PeakText = result.Aperture.Peak.ToString("F2");
        TotalElectronsText = result.Electrons is double e ? e.ToString("F1") : "— (set gain)";

        var m = result.Meter;
        MeterStateText = string.IsNullOrEmpty(m.Detail) ? StateLabel(m.State) : $"{StateLabel(m.State)}  ({m.Detail})";
        MeterRecommendationText = m.Recommendation;
        MeterColor = new SolidColorBrush(StateColor(m.State));
    }

    private static string StateLabel(ExposureMeterState s) => s switch
    {
        ExposureMeterState.TooFaint => "TOO FAINT",
        ExposureMeterState.NearSaturation => "NEAR SATURATION",
        ExposureMeterState.LowSignal => "LOW SIGNAL",
        ExposureMeterState.ExcessSignal => "EXCESS SIGNAL",
        _ => "GOOD",
    };

    private static Color StateColor(ExposureMeterState s) => s switch
    {
        ExposureMeterState.TooFaint or ExposureMeterState.NearSaturation => Color.Parse("#ff5555"),
        ExposureMeterState.LowSignal => Color.Parse("#ffcc55"),
        ExposureMeterState.ExcessSignal => Color.Parse("#66aaff"),
        _ => Color.Parse("#55dd77"),
    };

    private void ClearResults()
    {
        CenterText = ApertureCountText = SkyMedianText = SkySigmaText = PeakText = TotalElectronsText = "—";
        MeterStateText = "—";
        MeterRecommendationText = "";
        MeterColor = Brushes.White;
        UpdateApertureOverlay();
    }

    /// <summary>Grayscale linear stretch between BlackLevel/WhiteLevel (fractions of
    /// [_dataMin, _dataMax]) -- a stand-in for the Python app's ZScale/colormap variety,
    /// deferred to a follow-up pass. Runs the per-pixel loop in parallel across rows: at full
    /// camera resolution (e.g. 5496x3672) the scalar version blocked the UI thread for ~140ms on
    /// every slider drag tick and every playback frame, long enough to visibly stick hover/focus
    /// highlights on whatever button the pointer happened to be over (confirmed via timing, not
    /// just suspected -- this is the same bug behind the faded control-area rectangles).</summary>
    private void RenderImage()
    {
        if (_pixels.Length == 0) return;

        double range = Math.Max(_dataMax - _dataMin, 1e-10);
        double vmin = _dataMin + BlackLevel * range;
        double vmax = _dataMin + WhiteLevel * range;
        if (vmax <= vmin) vmax = vmin + 1e-6;
        double scale = 255.0 / (vmax - vmin);

        var bmp = new WriteableBitmap(
            new Avalonia.PixelSize(_width, _height), new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);

        using var fb = bmp.Lock();
        nint baseAddress = fb.Address;
        int rowBytes = fb.RowBytes;
        int width = _width, height = _height;
        var pixels = _pixels;

        Parallel.For(0, height, y =>
        {
            unsafe
            {
                byte* row = (byte*)baseAddress + y * rowBytes;
                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    float v = pixels[rowOffset + x];
                    byte g = (byte)Math.Clamp((v - vmin) * scale, 0.0, 255.0);
                    int o = x * 4;
                    row[o + 0] = g; row[o + 1] = g; row[o + 2] = g; row[o + 3] = 255;
                }
            }
        });

        DisplayImage = bmp;
    }

    /// <summary>Renders a 256-bin histogram of the current image's finite pixel values as a
    /// small bitmap. Bar heights use log(1+count) since astro frames are dominated by a huge
    /// sky-background spike that would otherwise swamp everything else on a linear scale.</summary>
    private void RenderHistogram()
    {
        const int bins = 256;
        const int w = 512, h = 70;

        var counts = new long[bins];
        double range = Math.Max(_dataMax - _dataMin, 1e-10);
        foreach (var v in _pixels)
        {
            if (!float.IsFinite(v)) continue;
            int b = (int)((v - _dataMin) / range * bins);
            if (b < 0) b = 0;
            if (b >= bins) b = bins - 1;
            counts[b]++;
        }

        double maxLog = 0.0;
        foreach (var c in counts) maxLog = Math.Max(maxLog, Math.Log(1 + c));
        if (maxLog <= 0) maxLog = 1.0;

        var bmp = new WriteableBitmap(
            new Avalonia.PixelSize(w, h), new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);

        using var fb = bmp.Lock();
        unsafe
        {
            byte* ptr = (byte*)fb.Address;
            for (int y = 0; y < h; y++)
            {
                byte* row = ptr + y * fb.RowBytes;
                for (int x = 0; x < w; x++)
                {
                    int bin = x * bins / w;
                    double barHeight = Math.Log(1 + counts[bin]) / maxLog * h;
                    bool lit = (h - y) <= barHeight;
                    byte r = lit ? (byte)0x55 : (byte)0x14;
                    byte g = lit ? (byte)0xaa : (byte)0x14;
                    byte bl = lit ? (byte)0xff : (byte)0x20;
                    int o = x * 4;
                    row[o + 0] = bl; row[o + 1] = g; row[o + 2] = r; row[o + 3] = 255;
                }
            }
        }

        HistogramImage = bmp;
    }

    [RelayCommand]
    private void ClearAperture()
    {
        _apertureCenter = null;
        ClearResults();
    }
}
