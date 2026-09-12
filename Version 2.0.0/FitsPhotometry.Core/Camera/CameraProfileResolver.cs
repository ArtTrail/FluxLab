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
        }
    }
}
