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
/// Minimal v2.2.0 viewer/aperture-photometry ViewModel -- proves the FitsPhotometry.Core
/// pipeline end to end (open -> display -> click aperture -> Exposure Meter). Feature parity
/// with the Python v1.4.0 app (histogram editing, WCS readout, header editor, sequence
/// playback, help-icon popups) is intentionally deferred to follow-up passes.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    /// <summary>
    /// v2.1.0: camera profiles live in %AppData%\SimpleFitsViewer\, NOT next to the executable.
    ///
    /// They used to be written to AppContext.BaseDirectory, i.e. the app's own bin folder. That
    /// silently tied a user's saved calibration to one build output directory: installing or
    /// building a new version created a fresh empty store and the profiles "disappeared" (they
    /// were still on disk, stranded under the old version's bin\). It also meant a clean/rebuild
    /// or reinstall could take real user data with it -- user data does not belong in the install
    /// directory.
    ///
    /// MigrateLegacyProfiles below moves any old beside-the-exe files across on first run, so
    /// nobody has to notice this happened.
    /// </summary>
    private static readonly string AppDataDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimpleFitsViewer");

    private static readonly string ProfilePath =
        System.IO.Path.Combine(AppDataDir, "camera_profile.json");
    private static readonly string LibraryPath =
        System.IO.Path.Combine(AppDataDir, "camera_profile_library.json");

    private static readonly string LegacyProfilePath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "camera_profile.json");
    private static readonly string LegacyLibraryPath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "camera_profile_library.json");

    /// <summary>Copies pre-v2.1.0 beside-the-exe profile files into %AppData% if nothing is there
    /// yet. Copy, not move: leaving the originals in place means an older build run afterwards
    /// still finds its own data, and nothing is destroyed if this misfires.</summary>
    private static void MigrateLegacyProfiles()
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppDataDir);
            if (!System.IO.File.Exists(ProfilePath) && System.IO.File.Exists(LegacyProfilePath))
                System.IO.File.Copy(LegacyProfilePath, ProfilePath);
            if (!System.IO.File.Exists(LibraryPath) && System.IO.File.Exists(LegacyLibraryPath))
                System.IO.File.Copy(LegacyLibraryPath, LibraryPath);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.LogException("Migrating camera profiles to AppData", ex);
        }
    }

    private readonly List<NamedCameraProfile> _library;

    private float[] _pixels = [];
    private int _width;
    private int _height;
    private double _dataMin, _dataMax;

    // v2.0.1: Black/White levels are fractions of [_stretchMin, _stretchMax] -- a ROBUST
    // display range -- not of the raw [_dataMin, _dataMax]. On a real frame the raw range is
    // dominated by a handful of saturated star cores: measured on a 6248x4176 light frame,
    // data spanned 256..65535 ADU while 99.99% of pixels sat below 5127, so the entire useful
    // stretch window (ZScale 528..903, 375 ADU wide) occupied just 0.574% of slider travel and
    // one LevelStep keypress moved 653 ADU -- overshooting the whole useful range in a single
    // press, with no practical way to dial it back by hand. Mapping onto the robust range
    // instead puts that same window across ~50% of slider travel at ~7 ADU per keypress.
    private double _stretchMin, _stretchMax;
    private double _zScaleLo, _zScaleHi;   // precomputed at load; used by AutoStretch

    private bool _suppressStretchRender;   // true while auto-stretch sets both levels at once
    private bool _suppressProfileSync;     // true while SyncProfileTextFromResolved is pushing loaded/resolved values into the Text properties
    private bool _suppressGeometrySync;    // true while SyncGeometryTextFromCurrent is pushing centroid/drag-derived values into the Text properties
    private FitsHeader _header = new(new());
    private WcsSolution? _wcs;   // null when the frame carries no usable TAN solution
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
        DiagnosticsLog.Log($"[App] FluxLab v{AppVersion.Version} started.");
        MigrateLegacyProfiles();   // must run before either load below
        _profile = LoadProfile();
        _library = LoadLibrary();
        foreach (var p in _library) ProfileNames.Add(p.Name);
        DiagnosticsLog.Log($"[App] Loaded {_library.Count} saved camera profile(s).");
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
        try
        {
            System.IO.Directory.CreateDirectory(AppDataDir);
            CameraProfileLibrary.SaveToJson(LibraryPath, _library);
        }
        catch (Exception ex) { DiagnosticsLog.LogException("Saving camera profile library", ex); }
    }

    [RelayCommand]
    private void LoadSelectedProfile()
    {
        var p = _library.FirstOrDefault(x => x.Name == SelectedProfileName);
        if (p is null) return;

        _profile.GainEPerAdu = p.GainEPerAdu;
        _profile.AduScale = p.AduScale;
        _profile.TargetElectrons = p.TargetElectrons;
        _profile.GainSource = "manual entry";
        _profile.AduScaleSource = "manual entry";

        _profile.FullWellElectrons = p.FullWellElectrons;
        if (p.FullWellElectrons is not null)
        {
            _profile.FullWellSource = "manual entry";   // the profile carries an explicit value
        }
        else
        {
            // The profile deliberately leaves full well blank so it auto-derives per frame (e.g. a
            // camera where the ADC ceiling, not the pixel well, sets saturation). Re-derive for the
            // currently-open frame; leave it blank if nothing is loaded. Without this, switching
            // from a profile that HAD a full well left the previous camera's value in place.
            _profile.FullWellSource = "";
            if (_pixels.Length > 0)
                CameraProfileResolver.ResolveFullWell(_profile, _header);
        }

        SyncProfileTextFromResolved();
        SaveProfile();
        SaveAsNameText = p.Name;
        Recompute();
        DiagnosticsLog.Log($"[UI] Loaded profile '{p.Name}': gain {GainText}, ADU scale {AduScaleText}, "
                         + $"full well {FullWellText} ({FullWellSourceText}).");
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
        DiagnosticsLog.Log($"[UI] Saved profile '{name}' (gain {GainText}, ADU scale {AduScaleText}, "
                         + $"full well {FullWellText}, target {TargetElectronsText}).");
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
        DiagnosticsLog.Log($"[UI] Deleted profile '{p.Name}'.");
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
        try
        {
            System.IO.Directory.CreateDirectory(AppDataDir);
            _profile.SaveToJson(ProfilePath);
        }
        catch (Exception ex) { DiagnosticsLog.LogException("Saving camera profile", ex); }
    }

    [ObservableProperty] private Bitmap? _displayImage;
    [ObservableProperty] private string _fileNameText = "No file loaded";
    [ObservableProperty] private string _instructionText = "Open a FITS file, then click a star to place an aperture.";
    [ObservableProperty] private double _zoomScale = 1.0;

    // Histogram / stretch. Fractions of [_stretchMin, _stretchMax] (the robust display range
    // computed at load -- see the field declarations above for why this is not the raw data range).
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
    [ObservableProperty] private string _fullWellSourceText = "—";
    [ObservableProperty] private string _colorFilterText = "—";
    [ObservableProperty] private string _wcsStatusText = "—";
    [ObservableProperty] private string _cursorText = "";
    [ObservableProperty] private string _apertureRaDecText = "—";

    /// <summary>Live X/Y + ADU under the cursor, with RA/Dec when the frame is plate solved.
    /// Called on every pointer move over the image.</summary>
    public void UpdateCursorReadout(double x, double y)
    {
        if (_pixels.Length == 0 || x < 0 || y < 0 || x >= _width || y >= _height)
        {
            CursorText = "";
            return;
        }

        int ix = (int)x, iy = (int)y;
        float v = _pixels[iy * _width + ix];
        string s = $"X {ix}  Y {iy}   {v:N0} ADU";

        if (_wcs is not null)
        {
            var (ra, dec) = _wcs.PixelToWorld(x, y);
            s += $"   {WcsSolution.FormatRa(ra)}  {WcsSolution.FormatDec(dec)}";
        }
        CursorText = s;
    }

    private void UpdateApertureRaDec()
    {
        if (_wcs is null || _apertureCenter is null) { ApertureRaDecText = "—"; return; }
        var (ra, dec) = _wcs.PixelToWorld(_apertureCenter.Value.X, _apertureCenter.Value.Y);
        ApertureRaDecText = $"{WcsSolution.FormatRa(ra)}  {WcsSolution.FormatDec(dec)}";
    }

    // ---- Find Target by name (VSX / NEA / SIMBAD -> WCS -> aperture) ----

    [ObservableProperty] private string _targetNameText = "";
    [ObservableProperty] private string _targetStatusText = "";
    [ObservableProperty] private IBrush _targetStatusColor = Brushes.Gray;
    [ObservableProperty] private bool _isResolvingTarget;

    /// <summary>The OBJECT value this class last auto-filled into TargetNameText. Kept so the next
    /// file can refill the box without silently overwriting a name the user typed themselves --
    /// only an empty box or one still holding the previous file's OBJECT gets replaced.</summary>
    private string _seededTargetName = "";

    private void SeedTargetNameFromHeader()
    {
        string obj = _header.Get("OBJECT").Trim();
        if (obj.Length == 0) return;
        if (TargetNameText.Trim().Length == 0 || TargetNameText.Trim() == _seededTargetName)
        {
            TargetNameText = obj;
            _seededTargetName = obj;
        }
    }

    private void SetTargetStatus(string message, IBrush color)
    {
        TargetStatusText = message;
        TargetStatusColor = color;
    }

    /// <summary>
    /// Resolves the typed target name to RA/Dec, converts that through the frame's WCS to a pixel
    /// position, and places the aperture there (re-centroiding and auto-sizing exactly as a manual
    /// click does, so the measurement path is identical -- resolution only supplies the seed).
    /// </summary>
    [RelayCommand]
    private async Task FindTargetAsync()
    {
        string name = TargetNameText.Trim();
        if (name.Length == 0) { SetTargetStatus("Type a target name first.", Brushes.Orange); return; }
        if (_pixels.Length == 0) { SetTargetStatus("Open a FITS file first.", Brushes.Orange); return; }
        if (_wcs is null)
        {
            SetTargetStatus("This frame has no WCS -- a name can't be turned into a pixel position. "
                          + "Plate solve it first.", Brushes.Orange);
            return;
        }

        IsResolvingTarget = true;
        try
        {
            var progress = new Progress<string>(m => SetTargetStatus(m, Brushes.Gray));
            var hit = await TargetResolverService.ResolveAsync(name, progress);

            if (hit is null)
            {
                SetTargetStatus($"'{name}' not found in VSX, NEA, or SIMBAD. "
                              + "See Tools > Diagnostics for what each service returned.", Brushes.Orange);
                return;
            }

            // Propagate the catalogue position to this frame's own epoch. Each source quotes its
            // coordinates at a different reference epoch (see ResolveResult) -- on TOI-4479 this
            // step moves the seed from 6.75 px off the star, right at the edge of the centroid
            // search window, to 1.95 px well inside it.
            double ra = hit.Ra, dec = hit.Dec;
            string pmNote;
            double? frameEpoch = Astrometry.TryGetObservationEpochJyear(_header);
            if (hit.PmRaMasPerYr is { } pmRa && hit.PmDecMasPerYr is { } pmDec && frameEpoch is { } epoch)
            {
                (ra, dec) = Astrometry.ApplyProperMotion(ra, dec, pmRa, pmDec, hit.EpochJyear, epoch);
                pmNote = $"proper motion applied, J{hit.EpochJyear:F1} -> {epoch:F2}";
            }
            else if (frameEpoch is null)
            {
                pmNote = $"J{hit.EpochJyear:F1} position, no proper-motion correction "
                       + "(frame has no DATE-OBS)";
            }
            else
            {
                pmNote = $"J{hit.EpochJyear:F1} position, no proper-motion correction "
                       + $"({hit.Source} lists no proper motion)";
            }

            var (px, py) = _wcs.WorldToPixel(ra, dec);
            string coords = $"{WcsSolution.FormatRa(ra)} {WcsSolution.FormatDec(dec)}";

            if (px < 0 || py < 0 || px >= _width || py >= _height)
            {
                // Report how far outside, in pixels -- a near miss (a few px off an edge) reads
                // very differently from a wrong field entirely, and the number distinguishes them.
                double dx = px < 0 ? -px : (px >= _width ? px - (_width - 1) : 0);
                double dy = py < 0 ? -py : (py >= _height ? py - (_height - 1) : 0);
                SetTargetStatus($"{hit.MatchedName} ({hit.Source}) is at {coords}, which falls "
                              + $"outside this frame -- {Math.Max(dx, dy):F0} px past the edge "
                              + $"(x={px:F1}, y={py:F1} in a {_width}x{_height} image).", Brushes.Orange);
                DiagnosticsLog.Log($"[FindTarget] '{name}' -> {hit.MatchedName} at {coords} maps to "
                                 + $"x={px:F2}, y={py:F2}, outside {_width}x{_height}.");
                return;
            }

            // A catalogue seed carries more positional error than a mouse click, so the peak search
            // is widened -- in arcsec, since that's the unit the error is actually budgeted in,
            // which keeps it sensible across wildly different plate scales (at 0.27"/px this is
            // ~22 px; on a 1.46"/px MObs frame the floor of 6 px already covers 8.7").
            const double SeedSearchArcsec = 6.0;
            int searchPx = (int)Math.Round(SeedSearchArcsec / Math.Max(_wcs.PixelScaleArcsec, 1e-6));
            searchPx = Math.Clamp(searchPx, 6, 25);

            bool centroided = PlaceApertureAt(px, py, searchPx);

            string note;
            IBrush color;
            if (!centroided)
            {
                note = "no star-like signal within " + $"{searchPx} px -- aperture placed at the "
                     + "catalogue position with its existing geometry";
                color = Brushes.Orange;
            }
            else
            {
                // Always report how far the lock moved from the catalogue position. This is the one
                // number that distinguishes a correct lock from a snap onto the wrong star, so it's
                // shown every time rather than only when something looks wrong.
                var c = _apertureCenter!.Value;
                double moved = Math.Sqrt((c.X - px) * (c.X - px) + (c.Y - py) * (c.Y - py));
                double movedArcsec = moved * _wcs.PixelScaleArcsec;
                note = $"locked on a star {moved:F1} px ({movedArcsec:F2}\") away and auto-sized";
                // Past ~3" the lock is far enough from the catalogue position to be a different
                // star rather than residual astrometric error, so it's flagged for a look.
                color = movedArcsec <= 3.0 ? Brushes.LightGreen : Brushes.Orange;
                if (movedArcsec > 3.0) note += " -- check this is the right star";
            }

            SetTargetStatus($"{hit.MatchedName} ({hit.Source}) at {coords}, {pmNote}. "
                          + $"Frame position x={px:F1}, y={py:F1}; {note}.", color);
            DiagnosticsLog.Log($"[FindTarget] '{name}' -> {hit.MatchedName} ({hit.Source}) {coords} "
                             + $"[{pmNote}] -> x={px:F2}, y={py:F2}; searchPx={searchPx}, "
                             + $"centroided={centroided}"
                             + (centroided ? $", center=({_apertureCenter!.Value.X:F2}, {_apertureCenter!.Value.Y:F2})" : ""));
        }
        catch (Exception ex)
        {
            DiagnosticsLog.LogException($"Finding target '{name}'", ex);
            SetTargetStatus($"Lookup failed: {ex.Message}", Brushes.Orange);
        }
        finally
        {
            IsResolvingTarget = false;
        }
    }

    // ---- Plate solve (drives an installed StarFix headlessly) ----

    [ObservableProperty] private bool _isSolving;
    [ObservableProperty] private string _solveStatusText = "";
    [ObservableProperty] private IBrush _solveStatusColor = Brushes.Gray;

    private void SetSolveStatus(string message, IBrush color)
    {
        SolveStatusText = message;
        SolveStatusColor = color;
    }

    /// <summary>
    /// Plate-solves the displayed frame by shelling out to an installed StarFix's solver, then
    /// re-reads the freshly-written WCS so the cursor RA/Dec, the WCS row and the aperture's sky
    /// position light up. StarFix is a directed solver, so it needs a position hint: the frame's
    /// own RA/DEC header keywords when present, otherwise the name in the Find Target box (resolved
    /// to coordinates). Solves in place -- the WCS is written straight into the file, like ASTAP.
    /// </summary>
    [RelayCommand]
    private async Task SolveAsync()
    {
        if (_pixels.Length == 0 || _currentFilePath is null)
        {
            SetSolveStatus("Open a FITS file first.", Brushes.Orange);
            return;
        }

        var cfg = SolverConfig.Load();
        var loc = SolverLocatorService.Locate(cfg);
        if (!loc.SolverFound)
        {
            SetSolveStatus("StarFix's solver wasn't found. Install StarFix, or set its path in "
                         + "Tools > Plate Solver.", Brushes.Orange);
            return;
        }
        if (!loc.CatalogFound)
        {
            SetSolveStatus("StarFix's Gaia catalog wasn't found. Download it in StarFix, or set its "
                         + "path in Tools > Plate Solver.", Brushes.Orange);
            return;
        }

        // Position hint: prefer the header's own RA/DEC (the solver reads them itself), else fall
        // back to resolving the Find Target name. Bail with a clear message if neither is available.
        double? raHint = null, decHint = null;
        bool headerHasPosition = _header.GetDouble("RA") is not null && _header.GetDouble("DEC") is not null;
        if (!headerHasPosition)
        {
            string name = TargetNameText.Trim();
            if (name.Length == 0)
            {
                SetSolveStatus("This frame has no RA/DEC in its header, so the solver has no "
                             + "starting point. Type the target name in the Find Target box below, "
                             + "then press Plate Solve again (not Find).", Brushes.Orange);
                return;
            }
            SetSolveStatus($"No header position -- resolving '{name}' for a solve hint…", Brushes.Gray);
            var hit = await TargetResolverService.ResolveAsync(name);
            if (hit is null)
            {
                SetSolveStatus($"No header position and '{name}' didn't resolve, so there's no solve "
                             + "hint. See Tools > Diagnostics.", Brushes.Orange);
                return;
            }
            raHint = hit.Ra; decHint = hit.Dec;
        }

        IsSolving = true;
        SetSolveStatus("Plate solving…", Brushes.Gray);
        try
        {
            string path = _currentFilePath;
            var result = await PlateSolveService.SolveAsync(
                path, raHint, decHint, loc.SolverExe!, loc.CatalogDir!, radiusDeg: 0.5, CancellationToken.None);

            if (!result.Ok)
            {
                SetSolveStatus(result.Message, Brushes.Orange);
                return;
            }

            // Solved in place: pixels are untouched, only header WCS cards were added, so re-read
            // just the header and refresh the WCS-derived readouts -- no need to reload the image.
            if (path == _currentFilePath)
            {
                _header = FitsHeader.Read(path);
                _wcs = WcsSolution.TryParse(_header);
                WcsStatusText = _wcs is null
                    ? "not plate solved"
                    : $"TAN{(_wcs.HasSip ? " + SIP" : "")}, {_wcs.PixelScaleArcsec:F3}\"/px";
                UpdateApertureRaDec();
                CursorText = "";
            }

            SetSolveStatus(_wcs is not null ? result.Message
                : result.Message + " (but the written WCS didn't parse back -- see Diagnostics).",
                _wcs is not null ? Brushes.LightGreen : Brushes.Orange);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.LogException($"Plate solving {_currentFilePath}", ex);
            SetSolveStatus($"Solve failed: {ex.Message}", Brushes.Orange);
        }
        finally
        {
            IsSolving = false;
        }
    }

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

    /// <summary>The file extensions the viewer treats as FITS, for directory scans and drag-drop.</summary>
    private static readonly string[] FitsExtensions = [".fits", ".fit", ".fts"];

    private static bool IsFitsFile(string path)
        => FitsExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Opens every .fits/.fit/.fts file in a directory (sorted by name) as a steppable
    /// sequence, matching the Python app's "Open Directory (Sequence)" -- resets the aperture and
    /// auto-stretches on the first frame, same as opening a single file fresh.</summary>
    public void LoadDirectory(string directory)
    {
        List<string> files;
        try
        {
            files = System.IO.Directory.EnumerateFiles(directory)
                .Where(IsFitsFile)
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

        StartSequence(files);
    }

    /// <summary>
    /// Opens whatever was dropped onto the window: any mix of FITS files and folders. Folders are
    /// expanded to their FITS contents; a single resulting file opens like Open File, several open
    /// as a steppable sequence (sorted by name, de-duplicated). Non-FITS items are ignored, with a
    /// clear message if nothing usable was dropped.
    /// </summary>
    public void LoadDropped(IReadOnlyList<string> paths)
    {
        var files = new List<string>();
        try
        {
            foreach (var p in paths)
            {
                if (System.IO.Directory.Exists(p))
                    files.AddRange(System.IO.Directory.EnumerateFiles(p).Where(IsFitsFile));
                else if (System.IO.File.Exists(p) && IsFitsFile(p))
                    files.Add(p);
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.LogException("Opening dropped items", ex);
            InstructionText = $"Error opening dropped items: {ex.Message}";
            return;
        }

        files = files.Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                     .ToList();

        if (files.Count == 0)
        {
            InstructionText = "Nothing to open -- drop FITS files (.fits/.fit/.fts) or a folder containing them.";
            return;
        }
        if (files.Count == 1) { LoadFile(files[0]); return; }
        StartSequence(files);
    }

    /// <summary>Shared tail for opening a non-empty, already-filtered/sorted list of FITS files as a
    /// sequence: resets aperture + stretch on the first frame. Used by LoadDirectory and LoadDropped
    /// so both behave identically once the file list is known.</summary>
    private void StartSequence(List<string> files)
    {
        DiagnosticsLog.Log($"[File] Opened a {files.Count}-frame sequence.");
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
        if (_apertureCenter is not null) { Recompute(); LogMeasurement($"frame {index + 1}/{_sequenceFiles.Count}"); }
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
        ComputeStretchRange(finite);

        CameraProfileResolver.ResolveAduScale(_profile, _header, _pixels);
        CameraProfileResolver.ResolveGain(_profile, _header);
        CameraProfileResolver.ResolveFullWell(_profile, _header);   // depends on both of the above
        ColorFilterText = CameraProfileResolver.DescribeColorFilter(_header);

        _wcs = WcsSolution.TryParse(_header);
        WcsStatusText = _wcs is null
            ? "not plate solved"
            : $"TAN{(_wcs.HasSip ? " + SIP" : "")}, {_wcs.PixelScaleArcsec:F3}\"/px";
        CursorText = "";
        SeedTargetNameFromHeader();
        SyncProfileTextFromResolved();

        FileNameText = System.IO.Path.GetFileName(path);
        DiagnosticsLog.Log($"[File] Loaded {FileNameText} ({_width}x{_height}); WCS: {WcsStatusText}; "
                         + $"gain {GainText} ({GainSourceText}), ADU scale {AduScaleText}, "
                         + $"full well {FullWellText} ({FullWellSourceText}).");
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

    /// <summary>v2.0.1: computes the robust display range the Black/White levels map onto, plus
    /// the ZScale limits Auto Stretch uses. Both come from one sorted subsample (~200k pixels,
    /// uniformly strided) rather than the full array -- percentiles from that many samples are
    /// far more precision than a display stretch needs, and it avoids sorting ~26M floats on
    /// every file load (the old AutoStretch sorted the whole array on every single click).
    ///
    /// The range is the 0.1/99.9 percentile window padded 25% on each side and clamped to the
    /// real data extent. The padding matters: without it the white point could never be pushed
    /// above the 99.9th percentile, so saturated star cores could not be isolated at all.</summary>
    private void ComputeStretchRange(float[] finite)
    {
        var samples = Subsample(finite, 200_000);
        if (samples.Length == 0)
        {
            _stretchMin = _dataMin; _stretchMax = _dataMax;
            _zScaleLo = _dataMin; _zScaleHi = _dataMax;
            return;
        }
        Array.Sort(samples);

        double lo = Percentile(samples, 0.001);
        double hi = Percentile(samples, 0.999);
        if (hi <= lo) { lo = _dataMin; hi = _dataMax; }

        double pad = 0.25 * (hi - lo);
        _stretchMin = Math.Max(_dataMin, lo - pad);
        _stretchMax = Math.Min(_dataMax, hi + pad);
        if (_stretchMax <= _stretchMin) { _stretchMin = _dataMin; _stretchMax = _dataMax; }

        // ZScale gets its own ~1000-pixel sample taken from the image in SPATIAL order and then
        // sorted -- exactly how astropy samples -- rather than being handed the big subsample
        // above. Both details matter and were confirmed empirically, not assumed:
        //   * the algorithm is genuinely sample-size sensitive (its slope fit is per-sample-
        //     index), so feeding it 200k points instead of 1000 shifts the result materially
        //     (astropy itself returns 528.0/902.6 at its default nsamples=1000 but 465.0/865.7
        //     at 200704 on the same frame);
        //   * decimating an already-sorted array is not the same sample as sorting a spatial
        //     stride, and produced a different answer in testing.
        var zSamples = Subsample(finite, 1000);
        Array.Sort(zSamples);
        (_zScaleLo, _zScaleHi) = ZScale(zSamples);
    }

    private static float[] Subsample(float[] src, int target)
    {
        if (src.Length <= target) return (float[])src.Clone();
        int stride = src.Length / target;
        var outp = new float[src.Length / stride];
        for (int i = 0; i < outp.Length; i++) outp[i] = src[i * stride];
        return outp;
    }

    /// <summary>Percentile of an already-sorted array. q is a fraction (0.999 = 99.9th).</summary>
    private static double Percentile(float[] sorted, double q)
    {
        if (sorted.Length == 0) return 0.0;
        int i = (int)(q * (sorted.Length - 1));
        return sorted[Math.Clamp(i, 0, sorted.Length - 1)];
    }

    /// <summary>IRAF/DS9 ZScale on an already-sorted sample -- the algorithm astropy's
    /// ZScaleInterval implements, and what the Python viewer used as its default stretch
    /// (the C# port shipped a 1/99-percentile stand-in instead, noted in the original
    /// RenderImage comment as deferred to a follow-up pass; this is that pass).
    ///
    /// Fits a line to the sorted sample with iterative sigma-clipped rejection, then divides
    /// the slope by the contrast factor to open the window around the median. Verified against
    /// astropy on a real light frame -- see the version notes.</summary>
    private static (double lo, double hi) ZScale(
        float[] sorted, double contrast = 0.25, double krej = 2.5, int maxIterations = 5)
    {
        int npix = sorted.Length;
        if (npix < 5) return (sorted[0], sorted[^1]);

        double zmin = sorted[0], zmax = sorted[^1];
        int centerPixel = (npix - 1) / 2;
        double median = (npix % 2 == 1)
            ? sorted[centerPixel]
            : 0.5 * (sorted[npix / 2 - 1] + sorted[npix / 2]);

        int minpix = Math.Max(5, (int)(npix * 0.5));
        int ngrow = Math.Max(1, (int)(npix * 0.01));
        var bad = new bool[npix];
        int ngood = npix, lastNgood = npix + 1;
        double slope = 0.0, intercept = 0.0;

        for (int it = 0; it < maxIterations; it++)
        {
            if (ngood >= lastNgood || ngood < minpix) break;

            // Least-squares line fit over surviving points (x = index). numpy's polyfit with
            // 0/1 weights is equivalent to simply excluding the zero-weight points.
            double sx = 0, sy = 0, sxx = 0, sxy = 0; int n = 0;
            for (int i = 0; i < npix; i++)
            {
                if (bad[i]) continue;
                sx += i; sy += sorted[i]; sxx += (double)i * i; sxy += (double)i * sorted[i]; n++;
            }
            if (n < 2) break;
            double denom = n * sxx - sx * sx;
            if (Math.Abs(denom) < 1e-12) break;
            slope = (n * sxy - sx * sy) / denom;
            intercept = (sy - slope * sx) / n;

            // Population sigma (ddof=0, matching numpy's default) of the surviving residuals.
            double ss = 0;
            for (int i = 0; i < npix; i++)
            {
                if (bad[i]) continue;
                double r = sorted[i] - (i * slope + intercept);
                ss += r * r;
            }
            double sigma = Math.Sqrt(ss / n);
            if (sigma <= 0) break;

            // Rejection is CUMULATIVE -- a point once rejected is never reinstated. Resetting
            // the mask each pass instead (the obvious-looking alternative) gives a visibly
            // different answer; this mirrors astropy.
            double thresh = krej * sigma;
            for (int i = 0; i < npix; i++)
            {
                double r = sorted[i] - (i * slope + intercept);
                if (Math.Abs(r) > thresh) bad[i] = true;
            }

            // Grow the reject mask, equivalent to astropy's
            // np.convolve(badpix, np.ones(ngrow), mode="same"): for a kernel of length M the
            // "same" window around i spans [i - (M-1-half), i + half] with half = (M-1)/2.
            int half = (ngrow - 1) / 2;
            int lowSpan = ngrow - 1 - half;
            var grown = new bool[npix];
            for (int i = 0; i < npix; i++)
            {
                int from = Math.Max(0, i - lowSpan);
                int to = Math.Min(npix - 1, i + half);
                for (int k = from; k <= to; k++)
                {
                    if (bad[k]) { grown[i] = true; break; }
                }
            }
            bad = grown;

            lastNgood = ngood;
            ngood = 0;
            for (int i = 0; i < npix; i++) if (!bad[i]) ngood++;
        }

        if (ngood >= minpix)
        {
            double s = contrast > 0 ? slope / contrast : slope;
            zmin = Math.Max(zmin, median - (centerPixel - 1) * s);
            zmax = Math.Min(zmax, median + (npix - centerPixel) * s);
        }
        if (zmax <= zmin) { zmin = sorted[0]; zmax = sorted[^1]; }
        return (zmin, zmax);
    }

    /// <summary>ZScale stretch -- matches the Python app's default (and its Auto Stretch button).
    /// Limits are precomputed at load, so this is now instant instead of re-sorting every click.</summary>
    [RelayCommand]
    private void AutoStretch()
    {
        if (_pixels.Length == 0) return;
        double range = Math.Max(_stretchMax - _stretchMin, 1e-10);

        _suppressStretchRender = true;
        BlackLevel = Math.Clamp((_zScaleLo - _stretchMin) / range, 0.0, 1.0);
        _suppressStretchRender = false;
        WhiteLevel = Math.Clamp((_zScaleHi - _stretchMin) / range, 0.0, 1.0);
        RenderImage();
        DiagnosticsLog.Log($"[UI] Auto Stretch (ZScale) -> black {BlackLevel:F3}, white {WhiteLevel:F3}.");
    }

    /// <summary>Called by the view when the user clicks on the displayed image, in native
    /// image-pixel coordinates (0,0 = top-left).</summary>
    public void OnImageClicked(double px, double py)
    {
        bool found = PlaceApertureAt(px, py);
        LogMeasurement(found ? "click (centroided)" : "click (no star, recentred)");
    }

    /// <summary>Logs the current aperture + its measurement to the diagnostics log. Called on
    /// discrete actions (a click, a drag release, a frame step) rather than on every drag tick, so
    /// the log stays a readable activity trail instead of flooding.</summary>
    public void LogMeasurement(string trigger)
    {
        if (_apertureCenter is not { } c) return;
        string radec = _wcs is null ? "" : $" [{ApertureRaDecText}]";
        DiagnosticsLog.Log($"[Photometry] {trigger}: centre ({c.X:F1}, {c.Y:F1}){radec} "
                         + $"r={_apertureRadius:F1} ap-px={ApertureCountText} sky={SkyMedianText} "
                         + $"peak={PeakText} e-={TotalElectronsText} meter={MeterStateText}");
    }

    /// <summary>
    /// Centroids near (px, py), places and auto-sizes the aperture set there, and re-measures.
    /// Returns true if a star was actually found -- false means the centroid failed and the
    /// aperture was simply recentred on the given point with its existing geometry (matching the
    /// Python app's behavior). Shared by manual clicks and by Find Target, so a name-resolved
    /// placement goes through the exact same path as a click and can't drift from it.
    /// </summary>
    /// <param name="peakSearchRadius">How far from (px, py) to hunt for the star, in pixels. Null
    /// keeps StarCentroid's click-tuned default; Find Target passes a wider value because a
    /// catalogue position has a larger error budget than a mouse click.</param>
    private bool PlaceApertureAt(double px, double py, int? peakSearchRadius = null)
    {
        if (_pixels.Length == 0) return false;

        var found = peakSearchRadius is { } searchPx
            ? StarCentroid.TryCentroid(_pixels, _width, _height, px, py, _profile.GainEPerAdu, searchPx)
            : StarCentroid.TryCentroid(_pixels, _width, _height, px, py, _profile.GainEPerAdu);
        if (found is { } r)
        {
            _apertureCenter = (r.CenterX, r.CenterY);
            _apertureRadius = r.Radius;
            _annulusInner = r.AnnulusInner;
            _annulusOuter = r.AnnulusOuter;
        }
        else
        {
            _apertureCenter = (px, py);
        }

        SyncGeometryTextFromCurrent();
        UpdateApertureOverlay();
        Recompute();
        return found is not null;
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
    private void ZoomIn() { ZoomScale = Math.Min(ZoomScale * 1.25, 20.0); DiagnosticsLog.Log($"[UI] Zoom In -> {ZoomScale:P0}."); }

    [RelayCommand]
    private void ZoomOut() { ZoomScale = Math.Max(ZoomScale * 0.8, 0.02); DiagnosticsLog.Log($"[UI] Zoom Out -> {ZoomScale:P0}."); }

    [RelayCommand]
    private void ZoomOneToOne() { ZoomScale = 1.0; DiagnosticsLog.Log("[UI] Zoom 1:1."); }

    // Fine-adjustment step for the black/white level +/- buttons -- finer than a typical
    // slider-drag increment (1% of the robust [_stretchMin, _stretchMax] range per click).
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
        FullWellText = _profile.FullWellElectrons?.ToString("G6") ?? "";   // clear when null, don't keep a stale value
        TargetElectronsText = _profile.TargetElectrons.ToString("G6");
        GainSourceText = string.IsNullOrEmpty(_profile.GainSource) ? "manual entry" : _profile.GainSource;
        AduScaleSourceText = string.IsNullOrEmpty(_profile.AduScaleSource) ? "manual entry" : _profile.AduScaleSource;
        FullWellSourceText = string.IsNullOrEmpty(_profile.FullWellSource) ? "manual entry" : _profile.FullWellSource;
        _suppressProfileSync = false;
    }

    partial void OnBlackLevelChanged(double value) { if (!_suppressStretchRender) RenderImage(); }
    partial void OnWhiteLevelChanged(double value) { if (!_suppressStretchRender) RenderImage(); }

    partial void OnGainTextChanged(string value) { if (_suppressProfileSync) return; ApplyProfileFieldsFromText(); Recompute(); }
    partial void OnAduScaleTextChanged(string value) { if (_suppressProfileSync) return; ApplyProfileFieldsFromText(); Recompute(); }
    partial void OnFullWellTextChanged(string value)
    {
        if (_suppressProfileSync) return;
        // Typing over a derived value makes it the user's number -- label it honestly, and stop
        // claiming it came from the header. The next file load re-derives and may supersede it
        // (see CameraProfileResolver.ResolveFullWell for when it defers to a manual value).
        _profile.FullWellSource = "manual entry";
        FullWellSourceText = "manual entry";
        ApplyProfileFieldsFromText();
        Recompute();
    }
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
        UpdateApertureRaDec();
        ApertureCountText = result.Aperture.NAperturePixels.ToString("F1");
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
        ApertureRaDecText = "—";
        MeterStateText = "—";
        MeterRecommendationText = "";
        MeterColor = Brushes.White;
        UpdateApertureOverlay();
    }

    /// <summary>Grayscale linear stretch between BlackLevel/WhiteLevel (fractions of the robust
    /// [_stretchMin, _stretchMax] range). ZScale itself now lives in AutoStretch, closing out the
    /// "deferred to a follow-up pass" note this comment used to carry; per-colormap variety from
    /// the Python app is still not ported. Runs the per-pixel loop in parallel across rows: at full
    /// camera resolution (e.g. 5496x3672) the scalar version blocked the UI thread for ~140ms on
    /// every slider drag tick and every playback frame, long enough to visibly stick hover/focus
    /// highlights on whatever button the pointer happened to be over (confirmed via timing, not
    /// just suspected -- this is the same bug behind the faded control-area rectangles).</summary>
    private void RenderImage()
    {
        if (_pixels.Length == 0) return;

        double range = Math.Max(_stretchMax - _stretchMin, 1e-10);
        double vmin = _stretchMin + BlackLevel * range;
        double vmax = _stretchMin + WhiteLevel * range;
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
        // v2.0.1: bin over the same robust range the Black/White levels map onto, so the
        // histogram, the sliders and the red/yellow level lines all share one X axis. Binning
        // over the raw data range instead put ~99.99% of an astro frame's pixels inside the
        // leftmost bin or two, leaving the rest of the histogram a flat empty strip.
        // Out-of-range pixels pile into the end bins, which is the honest thing to show: they
        // really are clipped at the current display range.
        double range = Math.Max(_stretchMax - _stretchMin, 1e-10);
        foreach (var v in _pixels)
        {
            if (!float.IsFinite(v)) continue;
            int b = (int)((v - _stretchMin) / range * bins);
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
        DiagnosticsLog.Log("[UI] Clear Aperture.");
    }
}
