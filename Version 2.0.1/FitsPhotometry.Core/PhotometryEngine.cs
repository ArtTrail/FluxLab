using FitsPhotometry.Core.Camera;
using FitsPhotometry.Core.Fits;
using FitsPhotometry.Core.Meter;
using FitsPhotometry.Core.Photometry;

namespace FitsPhotometry.Core;

/// <summary>Full result of one photometry analysis: the raw aperture measurement, its electron
/// conversion, and the resulting Exposure Meter evaluation.</summary>
public sealed record PhotometryResult(
    ApertureResult Aperture, double? Electrons, double? PeakElectrons, ExposureMeterResult Meter);

/// <summary>
/// Single entry point for the shared photometry/exposure-meter logic -- deliberately stateless
/// and operating on in-memory data (pixels + header + geometry + profile) rather than a file
/// path, so it works identically whether the caller is the desktop app (reading a file itself)
/// or, eventually, a NINA plugin operating on a frame already in memory.
/// </summary>
public static class PhotometryEngine
{
    /// <summary>Returns null if the aperture contains no valid pixels (e.g. placed off the
    /// edge of the frame).</summary>
    public static PhotometryResult? Analyze(
        float[] pixels, int width, int height,
        FitsHeader header, ApertureGeometry aperture, CameraProfile profile)
    {
        var apResult = ApertureStats.Compute(pixels, width, height, aperture);
        if (apResult is null) return null;

        double divisor = profile.AduScale is > 0 ? profile.AduScale.Value : 1.0;

        double? electrons = profile.GainEPerAdu is double g
            ? (apResult.SubSum / divisor) * g
            : null;
        double? peakElectrons = profile.GainEPerAdu is double g2
            ? (apResult.Peak / divisor) * g2
            : null;

        var meter = ExposureMeter.Evaluate(apResult, peakElectrons, electrons, header, profile);

        return new PhotometryResult(apResult, electrons, peakElectrons, meter);
    }
}
