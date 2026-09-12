using System;
using FitsPhotometry.Core.Fits;
using FitsPhotometry.Core.Photometry;

namespace FitsPhotometry.Core.Camera;

/// <summary>
/// Resolves Gain and ADU-scale from a newly loaded frame's header/pixels, applied to a
/// CameraProfile. Ported from the Python app's file-load logic -- see the ADU-scale bug fix
/// note below, which is the whole reason this is a separate, careful step rather than a naive
/// "detect or default to 1".
/// </summary>
public static class CameraProfileResolver
{
    /// <summary>
    /// Resolve ADU scale for a newly loaded frame, updating <paramref name="profile"/> in place.
    ///
    /// The bitwise-OR detector (AduScaleDetector) is exact for integer sensor data, but
    /// calibration (dark subtraction, flat division) introduces fractional values that destroy
    /// the exact-multiple pattern -- so on calibrated/float files, "nothing detected" does NOT
    /// mean no scaling is needed, it means the check is inconclusive. Overwriting the profile
    /// with a scale of 1 in that case would silently reintroduce the bug that once overstated
    /// Total electrons by ~16x on a calibrated file: only auto-overwrite when either a real
    /// pattern is found, or the data is integer (where "1" is a trustworthy result, not a
    /// guess) -- otherwise leave whatever the profile already holds (typically carried over
    /// from a raw frame shot with the same camera/gain).
    /// </summary>
    public static void ResolveAduScale(CameraProfile profile, FitsHeader header, float[] pixels)
    {
        int detected = AduScaleDetector.Detect(pixels);
        int? bitpix = header.GetInt("BITPIX");
        bool isIntegerData = bitpix is not null && bitpix > 0;

        if (detected > 1 || isIntegerData)
        {
            profile.AduScale = detected;
            profile.AduScaleSource = "auto-detected";
        }
        else if (profile.AduScale is null || profile.AduScale <= 0)
        {
            profile.AduScaleSource = "unverified (calibrated -- check a raw frame)";
        }
        // else: float/calibrated data, nothing detected, profile already has a value
        // (persisted or user-typed) -- leave it alone.
    }

    /// <summary>Resolve Gain (e-/ADU) from the header's EGAIN keyword, if present. Always
    /// overwrites the profile when EGAIN exists, matching the Python app (a fresh EGAIN reading
    /// is trusted per-frame, unlike ADU scale which can't be reliably re-detected on calibrated
    /// files).</summary>
    public static void ResolveGain(CameraProfile profile, FitsHeader header)
    {
        var egain = header.GetDouble("EGAIN");
        if (egain is not null)
        {
            profile.GainEPerAdu = egain;
            profile.GainSource = "FITS header (EGAIN)";
            return;
        }

        // No EGAIN in THIS file, but a gain is already loaded from a previous file or profile.
        // Keep using it -- there is nothing better -- but stop claiming it came from this file's
        // header, which would be a straight falsehood and hides a real hazard: MicroObservatory
        // frames carry no EGAIN, so opening one right after an ASI2600 frame silently measured it
        // with the ASI2600's 0.2429 e-/ADU. Every electron figure downstream is then wrong by
        // whatever the two cameras differ by, with the UI still reporting "FITS header (EGAIN)".
        if (profile.GainEPerAdu is not null && profile.GainSource.StartsWith("FITS header"))
            profile.GainSource = "carried over -- no EGAIN in this file";
    }

    /// <summary>
    /// Describe the colour-filter state of a frame for display, e.g. "OSC (RGGB) -- undebayered"
    /// or "mono / no CFA". Purely informational: none of the photometry maths changes for a
    /// colour sensor, because e-/ADU, ADU scale and full well are all properties of the readout
    /// electronics and the silicon, not of the filter sitting above a pixel.
    ///
    /// It is worth SHOWING, though, because what the number MEANS changes. An aperture on an
    /// undebayered mosaic sums R/G/G/B pixels with very different responses -- measured on a real
    /// red M-dwarf frame the split was R 32%, G 58%, B 10% of the flux, a ~3x per-pixel
    /// sensitivity spread inside one aperture. So the result is a blended bandpass matching no
    /// standard filter, and it shifts slightly as the star drifts across the CFA. Both are fine
    /// for differential photometry against similarly-coloured comparison stars; neither is
    /// obvious unless the app says the data is a mosaic.
    /// </summary>
    public static string DescribeColorFilter(FitsHeader header)
    {
        var pattern = header.Get("BAYERPAT")?.Trim();
        if (string.IsNullOrWhiteSpace(pattern)) return "mono / no CFA";
        return $"OSC ({pattern.ToUpperInvariant()}) -- undebayered";
    }

    /// <summary>
    /// Derive the effective per-pixel saturation ceiling in electrons, updating
    /// <paramref name="profile"/> in place. Call AFTER ResolveAduScale and ResolveGain -- it
    /// depends on both.
    ///
    /// No capture software records full well (verified: an ASI2600 frame's 95 header cards
    /// contained no FULLWELL/SATURATE/MAXADU/MAXLIN, matching an earlier check on an ASI183MM
    /// frame from different software), so it was previously a research-and-type-it field. But the
    /// ceiling that actually governs saturation is
    ///     min(physical pixel capacity, ADC ceiling x gain)
    /// and the ADC term is fully derivable: (2^BITPIX - 1) / AduScale x GainEPerAdu. The AduScale
    /// division matters -- on a sensor that left-shifts a 12-bit ADC into a 16-bit container, the
    /// real ADC ceiling is 2^BITPIX/AduScale counts, and skipping it would inflate the result by
    /// exactly that factor.
    ///
    /// The min() is the whole point, and why this overwrites a larger stored value rather than
    /// deferring to it: a user who researched their camera's headline figure has the PHYSICAL
    /// capacity, which is only binding at the lowest gains. On a 16-bit camera at gain 100
    /// (0.243 e-/ADU) the ADC saturates at ~15,900 e- while the datasheet says 50,000 -- keeping
    /// the datasheet number there would understate saturation by ~3x. A stored value LOWER than
    /// the derived ceiling is kept, because then the pixel really does fill first.
    /// </summary>
    public static void ResolveFullWell(CameraProfile profile, FitsHeader header)
    {
        var bitpix = header.GetInt("BITPIX");
        if (bitpix is null || bitpix <= 0) return;            // float/calibrated: no ADC ceiling
        if (profile.GainEPerAdu is not double gain || gain <= 0) return;

        double scale = profile.AduScale is > 0 ? profile.AduScale.Value : 1.0;
        double adcCeiling = (Math.Pow(2, bitpix.Value) - 1) / scale;
        double derived = adcCeiling * gain;
        if (derived <= 0) return;

        if (profile.FullWellElectrons is double existing && existing > 0 && existing < derived)
        {
            // Pixel well fills before the ADC does -- the stored value is the real limit.
            profile.FullWellSource = "manual entry (pixel-limited)";
            return;
        }

        profile.FullWellElectrons = derived;
        profile.FullWellSource = "derived (ADC ceiling x gain)";
    }
}
