namespace SimpleFitsViewer.Views;

/// <summary>User Guide / Revision History / About content, kept as plain text constants rather
/// than markup -- these are read-only reference popups, not something needing rich layout.</summary>
public static class AppText
{
    public const string UserGuide = """
        SIMPLE FITS VIEWER — USER GUIDE

        1. OPENING FILES
          • Open FITS File... — load a single FITS file (.fits/.fit/.fts)
          • Open Directory... — load every .fits/.fit/.fts file in a folder as
            a steppable sequence (see PLAYBACK below)

        2. VIEWING
          • Zoom In / Zoom Out / Fit / 1:1 — toolbar buttons
          • Scroll wheel — zoom in/out, centered on the cursor
          • Middle-mouse drag — pan around the image
          • Left-click on a star — auto-centroid and size an aperture (see below)
          • Left-click-drag on an existing aperture — move the whole aperture/annulus
            set without changing its size

        3. HISTOGRAM / STRETCH
          • Auto Stretch — 1st/99th-percentile linear stretch
          • Drag the red (black) / yellow (white) lines directly on the histogram
          • Black/White sliders, with +/- buttons for fine adjustment

        4. APERTURE PHOTOMETRY
          • Click a star to auto-centroid and size an aperture + sky annulus from
            its measured FWHM (radial half-max profile).
          • A safeguard falls back to a fixed, modest aperture when a star is too
            faint to measure reliably (peak-to-background-noise ratio below 15x).
          • The sky annulus automatically grows outward until it reaches genuinely
            flat background, rather than trusting a fixed offset from the aperture
            radius -- a bright star's wing can still be declining well past that.
          • Aperture radius / Annulus inner / Annulus outer fields let you type in
            (or spin with the up/down arrows) exact sizes at any time -- useful
            when you want to override the auto-sizing for a particular star
          • Clear Aperture — removes the current aperture

        5. GAIN & SCALING
          • Gain (e-/ADU) — auto-filled from the FITS header's EGAIN keyword, or
            type it in manually
          • ADU scale — corrects for sensors that pack a lower-bit-depth ADC
            reading into a wider file (e.g. 12-bit into 16-bit). Auto-detected on
            raw sensor files; on calibrated/float files, detection is unreliable,
            so the field is left as-is instead of silently assuming no scaling is
            needed -- type in the value from a raw frame shot with the same
            camera/gain
          • Full well (e-) — the per-PIXEL saturation ceiling; enables a true
            saturation check in electrons that also works on calibrated files.
            Auto-derived on load, with "Full well source" in Results showing
            where the value came from. No capture software records full well as
            a keyword, but the ceiling that matters is
                min(physical pixel capacity, ADC ceiling x Gain)
            and the right-hand term follows from BITPIX, Gain and ADU scale.
            Above the lowest gains the ADC saturates first, so the derived value
            IS the answer; at gain 0 the two coincide. A 16-bit camera at
            0.24 e-/ADU tops out near 15,900 e-, NOT its headline gain-0 figure --
            entering the datasheet number there would understate saturation ~3x,
            so a stored value above the derived ceiling is replaced. Type in a
            LOWER value and it is kept (shown as "pixel-limited"), since then the
            pixel really does fill first
          • Target electrons — the TOTAL electron count summed over the whole
            aperture, not a per-pixel value. It is therefore not capped by Full
            well, and normally exceeds it by a wide margin

        WHY 100,000 IS A SENSIBLE DEFAULT FOR ANY CAMERA
          Unlike Gain/ADU scale/Full well, Target electrons is not a property of
          your sensor -- an electron is an electron. It follows from photon noise
          alone: SNR = sqrt(N), so precision ~ 1.0857/sqrt(N).

            50,000 e-  -> SNR  224  -> 4.9 mmag
            100,000 e- -> SNR  316  -> 3.4 mmag
            1,000,000  -> SNR 1000  -> 1.1 mmag

          100,000 sits in the useful range for exoplanet transits (~1-5 mmag).
          Raise it for shallow transits or precise variable work; the number to
          change is driven by the precision you need, not by the camera.

          Two caveats. Those figures are a best case: sky background, read noise
          and scintillation add on top, so hitting the target does not guarantee
          that precision. And on a bright, sharply-focused star the peak pixel can
          saturate before the aperture sum ever reaches the target -- if the meter
          alternates between NEAR SATURATION and LOW SIGNAL as you adjust exposure,
          that is it telling you one frame cannot do both. Shorten the exposure and
          stack, or defocus slightly to spread the flux.

        6. CAMERA PROFILES
          • Save your camera's Gain/ADU scale/Full well/Target electrons under a
            name (e.g. "ASI183MM Pro - gain 111"), then Load it again later
            instead of retyping every session
          • Delete removes the selected saved profile
          • Your last-used values are also remembered automatically between
            sessions, independent of any saved named profile

        7. EXPOSURE METER
          • GOOD / LOW SIGNAL / EXCESS SIGNAL / TOO FAINT / NEAR SATURATION, with
            a plain-language recommendation and a suggested new exposure time
            when the FITS header has EXPTIME or EXPOSURE

        8. SEQUENCE PLAYBACK (Open Directory)
          • ⏮ / |◀ / ▶| / ⏭ — first / previous / next / last frame
          • ▶ / ⏸ — play or pause a 1fps loop through the sequence (wraps
            around at the end)
          • Slider — drag to jump to any frame (loads after a short pause)
          • An aperture you've placed stays put as you step through frames
            from the same field of view, so its photometry re-measures
            automatically frame to frame -- handy for watching how Total
            Electrons/Exposure Meter evolve across a session. The current
            black/white stretch is kept too; only a fresh Open FITS File or
            Open Directory resets the aperture and re-auto-stretches.

        9. FITS HEADER VIEWER / EDITOR
          • Tools > FITS Header... — search, view, and edit every card in the
            loaded file's header
          • Double-click a Value or Comment cell to edit it
          • Add Keyword... / Delete Keyword add or remove cards (required
            keywords like SIMPLE/BITPIX/NAXIS can't be deleted)
          • Save to File writes your changes back into the FITS file in place
            (with a confirmation first) -- pixel data is never touched. Not
            available for tile-compressed (.fz-style) files.

        10. DIAGNOSTICS
          • Tools > Diagnostics — a log of errors/events encountered this
            session, with Save and Clear

        Not yet in this version: WCS/RA-Dec readout, colormap variety beyond
        grayscale.
        """;

    public const string RevisionHistory = """
        SIMPLE FITS VIEWER — REVISION HISTORY (C# / Avalonia port)

        v2.0.1 — 2026-09-11

          • Fixed: the Black/White level sliders were effectively unusable on real
            light frames. They mapped across the full data range, which on a
            typical frame is set by a handful of saturated star cores -- on a
            measured example the data spanned 256-65535 ADU while 99.99% of
            pixels sat below 5127, so the entire useful stretch window was only
            0.57% of slider travel, and one +/- button press moved 653 ADU when
            the whole useful range was just 375 ADU wide. Levels now map onto a
            robust display range (0.1-99.9 percentile, padded 25% each side), so
            that same window spans about half the slider and a button press moves
            ~7 ADU. The practical symptom was an image stuck at pure black or
            pure white with no realistic way to recover it by hand.

          • Fixed: the histogram binned over the full data range too, which put
            nearly every pixel in the leftmost bin or two and left the rest of the
            plot a flat empty strip. It now shares the same robust range as the
            sliders, so the histogram, the level lines and the image agree.

          • Changed: Auto Stretch now uses ZScale (the IRAF/DS9 algorithm the
            Python app used as its default stretch) instead of the 1st/99th
            percentile stand-in the port shipped with. Limits are precomputed
            once at file load, so Auto Stretch is now instant -- it previously
            re-sorted the entire pixel array on every click.

          • Fixed: FITS files whose image sits in an extension rather than the
            primary HDU would not open at all -- the window simply stayed blank,
            with no error. Any file unpacked from .fz has this layout. The reader
            now walks the HDU chain to find the first real image. Table
            extensions are skipped rather than misread as pixels, so a
            tile-compressed .fz (whose data this reader still cannot decode)
            opens as empty instead of rendering noise.

          • New: Full well is now auto-derived on file load instead of being a
            research-it-yourself field. No capture software writes a full-well
            keyword, but the ceiling that governs saturation is
            min(physical capacity, ADC ceiling x Gain), and the second term
            follows from BITPIX, Gain and ADU scale. A new "Full well source" row
            shows whether the value was derived or typed in. This matters most at
            high gain: on a 16-bit camera at gain 100 the ADC saturates near
            15,900 e- while the datasheet says 50,000, so using the datasheet
            figure understated saturation by about 3x.

          • User Guide: expanded the Gain & Scaling section to explain why Full
            well is a per-pixel, gain-dependent limit while Target electrons is a
            whole-aperture total that is not capped by it -- and why 100,000 is a
            reasonable default for any camera.

        v2.0.0 — 2026-08-05

          • Full C# port of the Python v1.4.0 app, split into a shared
            FitsPhotometry.Core library (FITS reading, aperture photometry,
            exposure meter) and this Avalonia desktop app, designed so the same
            Core can later be referenced by a NINA plugin without re-implementing
            the math.

          • Pan (middle-drag) and scroll-wheel zoom centered on the cursor.

          • Histogram with Auto Stretch, draggable black/white lines, and +/-
            fine-adjustment buttons.

          • Fixed a real bug where Total Electrons was silently overstated on
            calibrated/float FITS files, because the ADU-scale auto-detector only
            works on exact-integer data and calibration destroys that pattern.

          • Added a safeguard against noise-dominated FWHM measurements on faint
            stars, and a robust (MAD-based) background estimator that resists
            contamination from nearby bright stars -- both confirmed against
            real crowded-field data, not just synthetic tests.

          • The sky annulus now verifies it has reached flat background and
            grows outward automatically, rather than trusting a fixed offset
            from the aperture radius.

          • Manual control over the aperture/annulus set: type exact radii into
            the Geometry fields (with up/down spinner arrows), or left-click-drag
            an existing set to move it without resizing (a plain click still
            re-centroids and re-sizes).

          • FITS header viewer/editor (Tools > FITS Header...): search, edit,
            add, and delete header cards, then save back into the file in
            place. Preserves card order and comments, and is verified safe
            against real files -- pixel data is copied through byte-for-byte
            regardless of how the edited header's length changes.

          • Open Directory... loads a folder of FITS files as a steppable
            sequence: first/prev/play/next/last controls plus a seek slider.
            An existing aperture keeps its position and stretch across
            frames, only re-measuring against each new frame's data --
            confirmed against a real 48-frame sequence during testing.

          • Named, savable camera profiles (Gain/ADU scale/Full well/Target
            electrons), in addition to automatic last-used persistence.

          • Fixed a bug where ADU scale and Full well silently failed to persist
            across sessions (a startup sync was inadvertently triggering a save
            of not-yet-synced fields).

          • Tools > Diagnostics error log with Save/Clear; Help menu with this
            Revision History, a User Guide, and About.
        """;

    public const string About = """
        Simple FITS Viewer
        Version 2.0.1 (C# / Avalonia port)

        A desktop viewer and aperture-photometry tool for FITS astronomy images,
        built around determining the right exposure time for exoplanet transit
        and variable-star photometry.

        github.com/ArtTrail/Simple-FITS-Viewer

        art.trail@icloud.com

        © 2026 Art Trail.  All rights reserved.
        """;
}
