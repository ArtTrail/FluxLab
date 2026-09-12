namespace SimpleFitsViewer.Views;

/// <summary>User Guide / Revision History / About content, kept as plain text constants rather
/// than markup -- these are read-only reference popups, not something needing rich layout.</summary>
public static class AppText
{
    public const string UserGuide = """
        FLUXLAB — USER GUIDE

        1. OPENING FILES
          • Open FITS File... — load a single FITS file (.fits/.fit/.fts)
          • Open Directory... — load every .fits/.fit/.fts file in a folder as
            a steppable sequence (see PLAYBACK below)
          • Drag and drop — drop FITS files or a folder anywhere on the window.
            One file opens on its own; several files (or a dropped folder) open
            as a steppable sequence. Non-FITS items are ignored.

        2. VIEWING
          • Zoom In / Zoom Out / Fit / 1:1 — toolbar buttons
          • Scroll wheel — zoom in/out, centered on the cursor
          • Middle-mouse drag — pan around the image
          • Left-click on a star — auto-centroid and size an aperture (see below)
          • Left-click-drag on an existing aperture — move the whole aperture/annulus
            set without changing its size
          • The readout beside the filename follows the cursor: X, Y, pixel value
            in ADU, plus RA/Dec on a plate-solved frame (see PLATE-SOLVED FRAMES)

        3. HISTOGRAM / STRETCH
          • Auto Stretch — 1st/99th-percentile linear stretch
          • Drag the red (black) / yellow (white) lines directly on the histogram
          • Black/White sliders, with +/- buttons for fine adjustment

        4. APERTURE PHOTOMETRY
          • Click a star to auto-centroid and size an aperture + sky annulus from
            the radius that MAXIMISES SNR, found by walking the curve of growth
            outward from the centroid.
          • Why not 1.5 x FWHM? That rule assumes the star dominates its own
            noise, which is false for any faint target. Past the optimum, each
            extra ring of pixels adds more sky noise than starlight, so a bigger
            aperture collects more signal but gives WORSE precision. On a real
            faint frame the optimum was r=9 (SNR 42.8) where 1.5xFWHM would have
            picked r=17 (SNR 32.3). It also means a star too faint to measure an
            FWHM on no longer has to fall back to a fixed guess -- the curve of
            growth is still measurable.
          • Apertures use fractional pixel overlap: a pixel on the boundary
            contributes the share of its area inside the circle, not all-or-
            nothing. This stops the measured flux stepping as a star drifts
            sub-pixel between frames (measured: 9.8 -> 7.0 mmag of drift scatter).
            "Aperture pixels" is therefore an effective area and can be
            fractional.
          • The sky annulus automatically grows outward until it reaches genuinely
            flat background, rather than trusting a fixed offset from the aperture
            radius -- a bright star's wing can still be declining well past that.
          • Aperture radius / Annulus inner / Annulus outer fields let you type in
            (or spin with the up/down arrows) exact sizes at any time -- useful
            when you want to override the auto-sizing for a particular star
          • Clear Aperture — removes the current aperture

        5. GAIN & SCALING
          • Gain (e-/ADU) — auto-filled from the FITS header's EGAIN keyword, or
            type it in manually. Not every file has EGAIN (MicroObservatory
            frames don't): the previous value is then kept, and "Gain source" in
            Results reads "carried over" rather than claiming it came from this
            file. Watch that when switching between cameras -- a carried-over
            gain from a different camera makes every electron figure wrong
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

        ONE-SHOT COLOUR (OSC) SENSORS
          The "Colour filter" row in Results reports whether the frame is an
          undebayered Bayer mosaic (e.g. "OSC (RGGB)") or mono.

          None of the numbers above change for a colour sensor. Gain, ADU scale
          and full well are properties of the readout electronics and the
          silicon, not of the colour filter sitting above a pixel, so the
          electron conversion is exactly as valid on a mosaic. Raw CFA data is in
          fact the honest place to count electrons -- debayering interpolates,
          which invents values that were never measured.

          What changes is INTERPRETATION. An aperture on a mosaic sums R/G/G/B
          pixels of very different response (on a real red star: R 32%, G 58%,
          B 10% of the flux), so the result is a blended bandpass matching no
          standard filter, and it shifts slightly as the star drifts across the
          CFA. Both are fine for differential photometry as long as comparison
          stars are similar in colour and the same aperture is used throughout.

          Note that 2x2 "superpixel" binning is NOT offered, despite sounding
          like the obvious fix: measured on a real frame it made drift scatter
          worse (17.8 mmag vs 9.8), because halving the resolution coarsens the
          aperture edge more than merging the colour planes helps.

        6. CAMERA PROFILES
          • Save your camera's Gain/ADU scale/Full well/Target electrons under a
            name (e.g. "ASI183MM Pro - gain 111"), then Load it again later
            instead of retyping every session
          • Delete removes the selected saved profile
          • Your last-used values are also remembered automatically between
            sessions, independent of any saved named profile
          • Both are stored in %AppData%\SimpleFitsViewer\ (camera_profile.json
            and camera_profile_library.json), NOT in the app's own folder, so
            they survive upgrades, reinstalls and rebuilds

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

        11. PLATE-SOLVED FRAMES (WCS)
          • If the file carries a TAN world-coordinate solution, the "WCS" row in
            Results shows the projection, whether SIP distortion terms are
            present, and the plate scale in arcsec/pixel. "not plate solved"
            means the header has no usable solution -- solve the frame first
            (StarFix, ASTAP, Astrometry.net...) to enable everything below.
          • The readout beside the filename tracks the cursor: X, Y, the pixel
            value in ADU, and RA/Dec when a solution is present.
          • "Center (RA, Dec)" in Results gives the sky position of the current
            aperture centre.

          FIND TARGET
          • Type a name and press Find (or Enter) to place the aperture on it.
            The box is pre-filled from the frame's OBJECT keyword when there is
            one; a name you type yourself is not overwritten when the next file
            loads.
          • Three catalogues are tried in order: AAVSO VSX (variable stars),
            then the NASA Exoplanet Archive (host star or planet name), then
            SIMBAD. SIMBAD is searched by identifier alias, so any known
            designation works, and spacing/case do not matter ("M13", "m 13").
          • Proper motion is applied. This is not cosmetic: the catalogues quote
            positions at DIFFERENT reference epochs (VSX and SIMBAD at J2000,
            the Exoplanet Archive at J2015.5), and on a real 2026 frame
            TOI-4479's uncorrected Exoplanet Archive position landed 6.75 px
            (1.81") from the star -- far enough that the centroid search had no
            margin left, and a slightly different offset would have settled on
            the star's wing rather than its peak, biasing the aperture centre by
            over a pixel with nothing on screen to reveal it. Propagating to the
            frame's own DATE-OBS cuts the error to 1.95 px (0.52"), which is the
            plate solve's own accuracy floor. The status line always says which
            epoch was used and whether a correction was applied.
          • The status line also reports how far the final lock sits from the
            catalogue position, in pixels and arcsec. That is the number that
            tells a correct lock apart from a snap onto a close neighbour --
            anything past 3" is flagged for a look. If no star is found at all,
            the aperture is placed at the catalogue position with its existing
            geometry, and says so.
          • Tools > Diagnostics records every lookup: which catalogues were
            tried, what each returned, the epoch handling, and the final pixel
            position. Check there first when a name does not resolve.

        Not yet in this version: colormap variety beyond grayscale; pixel data
        for tile-compressed (.fz) files.
        """;
}
