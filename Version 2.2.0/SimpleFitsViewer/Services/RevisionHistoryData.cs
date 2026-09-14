using System.Collections.Generic;

namespace SimpleFitsViewer.Services;

/// <summary>
/// Single source of truth for the Revision History, mirroring TransitLab's
/// <c>Services/RevisionHistoryData.cs</c>: entries are data, rendered by RevisionHistoryView via an
/// ItemsControl, newest first. Keeping it as data (rather than hand-written XAML) means the same
/// list can later feed a first-launch "What's New" popup without the two drifting apart.
/// </summary>
/// <summary>One released version and its bullets. Top-level (not nested) so compiled XAML bindings
/// can name it as a DataType.</summary>
public record RevisionEntry(string Version, string Date, string[] Bullets)
{
    public string Header => $"v{Version}  —  {Date}";
}

public static class RevisionHistoryData
{
    public static IReadOnlyList<RevisionEntry> All { get; } = new[]
    {
        new RevisionEntry("2.2.1", "2026-09-14", new[]
        {
            "Fixed: on a PC set to Windows Light mode, most button labels rendered as near-black text on FluxLab's dark panels and were almost unreadable. The app now always uses its own dark theme, regardless of the Windows light/dark setting.",
            "Fixed: a manually typed Full well could get stuck -- clearing the field left it blank rather than returning to the auto-derived value, and there was no way to un-set a manual value. Clearing the Full well field now immediately re-derives it for the current frame.",
            "Changed: a hand-typed Full well above the ADC-limited ceiling (physically unreachable at that gain and ADU scale, which would understate saturation) is now capped to the ceiling, with a note explaining why and how to reset. A value below the ceiling is a legitimate pixel-limited well and is kept as entered.",
            "Changed: the aperture and sky-annulus rings are thicker, recoloured (a bluer middle ring and a redder outer ring) and now pulse with a soft glow, so they are much easier to spot when the image is zoomed out.",
            "Layout: a little more breathing room between the right-hand panel's controls and its scrollbar.",
        }),

        new RevisionEntry("2.2.0", "2026-09-12", new[]
        {
            "Renamed: the app is now FluxLab (was Simple FITS Viewer). Saved camera profiles are unaffected -- they still live in the same place and carry over automatically.",
            "New look: menu bar, header/logo banner, About, User Guide and Revision History now follow the shared visual style of the sibling apps (StarFix, VariLab, TransitLab) -- the Nord palette, the logo header, and a User Guide with a searchable body, a hyperlinked table of contents, and a back-to-top button.",
            "New: Plate Solve. A button solves the displayed frame in place by driving an installed StarFix headlessly, then fills in the WCS so the cursor RA/Dec, the WCS row and Find Target all work. StarFix is a directed solver, so it uses the frame's own RA/DEC header keywords as the hint, or the Find Target name when the header has none. FluxLab auto-detects a standard StarFix install (solver + Gaia catalog); Tools > Plate Solver allows a custom path and shows whether both are found.",
            "New: drag and drop. Drop FITS files or a folder anywhere on the window to open them -- one file opens on its own, several files or a dropped folder open as a steppable sequence, and non-FITS items are ignored.",
            "New: WCS support. A TAN plate solution in the header (with SIP distortion terms if present) is read and used. The cursor readout gains RA/Dec, Results gains the aperture's sky position and a \"WCS\" row naming the projection and plate scale. Validated against astropy over a 121-point grid on two real frames (one with SIP, one without), agreeing to 0.0002 mas. A header can carry BOTH a CD matrix and CDELT+PC and they can disagree (~3.3 px by the corners on one real frame); PC+CDELT takes precedence.",
            "New: Find Target. Type a name, press Find, and the aperture is placed on it -- looked up in AAVSO VSX, then the NASA Exoplanet Archive, then SIMBAD (by identifier alias). The name box pre-fills from the frame's OBJECT keyword. Requires a plate-solved frame.",
            "Find Target applies proper motion, per source. The three catalogues quote positions at different reference epochs (VSX and SIMBAD at J2000, the Exoplanet Archive at J2015.5 -- measured, not assumed). On a real 2026 frame, TOI-4479's uncorrected Exoplanet Archive position fell 6.75 px from the star, right at the edge of the centroid search where a slightly different offset biases the aperture centre invisibly; propagating to the frame's DATE-OBS brought it to 1.95 px. The status line reports the epoch used and how far the final lock sits from the catalogue position, flagging anything past 3\".",
        }),

        new RevisionEntry("2.1.0", "2026-09-11", new[]
        {
            "Changed: aperture auto-sizing now picks the radius that maximises SNR, walking the curve of growth outward, instead of scaling a measured FWHM by 1.5x. On a real frame the SNR optimum was r=9 (SNR 42.8) while 1.5xFWHM would have chosen r=17 -- twice the signal for worse precision (SNR 32.3). It also removes the old fixed fallback radius for faint stars.",
            "Changed: apertures now use fractional pixel overlap -- an edge pixel contributes the fraction of its area inside the circle, not an all-or-nothing pixel-centre test. Measured across 81 sub-pixel positions spanning +/-1 px of drift, flux scatter fell from 0.90% (9.8 mmag) to 0.64% (7.0 mmag). \"Aperture pixels\" is now the effective area and may show a fractional value.",
            "The SNR search stops once the target's own light runs out, rather than taking the best SNR over the whole range -- without that guard a nearby star entering the aperture raises SNR again further out (seen on a real MObs frame where it returned r=29.5 and measured a second star as part of the target).",
            "Fixed: when a file has no EGAIN keyword (MicroObservatory frames carry none), the Gain field keeps the previous value but now says \"carried over -- no EGAIN in this file\" instead of falsely claiming \"FITS header (EGAIN)\". A new \"Gain source\" row makes this visible.",
            "Fixed: saved camera profiles could vanish -- they were written next to the executable, tied to one build/install folder. They now live in %AppData%\\SimpleFitsViewer\\, and existing beside-the-exe profiles are migrated there automatically on first run.",
            "New: a \"Colour filter\" row reports whether the frame is a one-shot colour mosaic (e.g. \"OSC (RGGB) -- undebayered\") or mono. None of the photometry maths changes for a colour sensor, but an aperture on an undebayered mosaic sums R/G/G/B pixels of very different response (measured 32%/58%/10% of flux on a red star), so it is a blended bandpass rather than any standard filter.",
        }),

        new RevisionEntry("2.0.1", "2026-09-11", new[]
        {
            "Fixed: the Black/White level sliders were effectively unusable on real light frames -- they mapped across the full data range, which is set by a handful of saturated star cores. On a measured example the useful stretch window was only 0.57% of slider travel and one button press moved 653 ADU. Levels now map onto a robust display range (0.1-99.9 percentile, padded), so that window spans about half the slider and a button press moves ~7 ADU.",
            "Fixed: the histogram binned over the full data range too, piling nearly every pixel into the leftmost bin. It now shares the sliders' robust range, so histogram, level lines and image agree.",
            "Changed: Auto Stretch now uses ZScale (the IRAF/DS9 algorithm) instead of the 1st/99th-percentile stand-in the port shipped with. Limits are precomputed at load, so it is now instant.",
            "Fixed: FITS files whose image sits in an extension rather than the primary HDU would not open at all (blank window, no error) -- any file unpacked from .fz has this layout. The reader now walks the HDU chain; table extensions are skipped rather than misread as pixels.",
            "New: Full well is auto-derived on load instead of being a research-it-yourself field. The ceiling that governs saturation is min(physical capacity, ADC ceiling x Gain), and the second term follows from BITPIX, Gain and ADU scale. A \"Full well source\" row shows whether the value was derived or typed.",
            "User Guide: expanded the Gain & Scaling section to explain why Full well is a per-pixel gain-dependent limit while Target electrons is a whole-aperture total that is not capped by it, and why 100,000 is a reasonable default for any camera.",
        }),

        new RevisionEntry("2.0.0", "2026-08-05", new[]
        {
            "Full C# port of the Python v1.4.0 app, split into a shared FitsPhotometry.Core library (FITS reading, aperture photometry, exposure meter) and this Avalonia desktop app, so the same Core can later be referenced by a NINA plugin.",
            "Pan (middle-drag) and scroll-wheel zoom centered on the cursor.",
            "Histogram with Auto Stretch, draggable black/white lines, and +/- fine-adjustment buttons.",
            "Fixed a real bug where Total Electrons was silently overstated on calibrated/float FITS files, because the ADU-scale auto-detector only works on exact-integer data.",
            "Added a safeguard against noise-dominated FWHM measurements on faint stars, and a robust (MAD-based) background estimator that resists contamination from nearby bright stars.",
            "The sky annulus now verifies it has reached flat background and grows outward automatically, rather than trusting a fixed offset from the aperture radius.",
            "Manual control over the aperture/annulus set: type exact radii into the Geometry fields, or left-click-drag an existing set to move it without resizing.",
            "FITS header viewer/editor (Tools > FITS Header...): search, edit, add and delete header cards, then save back into the file in place -- pixel data is copied through byte-for-byte.",
            "Open Directory... loads a folder of FITS files as a steppable sequence: first/prev/play/next/last plus a seek slider. An existing aperture keeps its position and stretch across frames, only re-measuring each new frame.",
            "Named, savable camera profiles (Gain/ADU scale/Full well/Target electrons), in addition to automatic last-used persistence.",
        }),
    };
}
