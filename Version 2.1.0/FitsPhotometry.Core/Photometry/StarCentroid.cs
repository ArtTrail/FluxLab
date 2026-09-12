using System;
using System.Collections.Generic;

namespace FitsPhotometry.Core.Photometry;

public static class StarCentroid
{
    private const int Box = 30;
    private const int PeakSearch = 6;
    private const int Win = 6;
    private const double SignificanceThreshold = 8.0;

    // Below this peak-over-background-sigma ratio, the half-max radial-profile crossing sits
    // too close to the noise floor to trust: a faint star's half-max level is only modestly
    // above background, so random ring-to-ring fluctuation -- not real stellar structure --
    // ends up determining where the profile "crosses," inflating the measured FWHM. Verified
    // against a real frame: stars around SNR~9-13 (barely past SignificanceThreshold) measured
    // FWHM 8-12px, in the same range as a genuinely bright, cleanly-profiled star 70x brighter,
    // while several independent stars at SNR>=15 converged consistently on a much smaller,
    // more plausible ~6px. Below this threshold, fall back to a fixed, modest default radius
    // instead of trusting a noise-dominated measurement.
    private const double MinSnrForReliableFwhm = 15.0;
    private const double DefaultRadiusWhenUnreliable = 6.0;

    public readonly record struct Result(double CenterX, double CenterY, double Radius, double AnnulusInner, double AnnulusOuter);

    /// <summary>
    /// Centroid on the star nearest (seedX, seedY) and size an aperture/annulus from its FWHM
    /// (radial half-max profile). Ported from the Python app's _auto_size_aperture.
    ///
    /// The seed point is deliberately generic -- a mouse click in the desktop app, or a computed
    /// position (e.g. from a plate solve + known target RA/Dec) for unattended use -- the
    /// function just searches a small window around whatever point it's given, so both callers
    /// use the exact same refinement logic.
    ///
    /// Returns null if no star-like signal is found near the seed (e.g. blank sky); the caller
    /// should keep its existing radius/annulus and just recenter to the raw seed point in that
    /// case, matching the Python app's behavior.
    /// </summary>
    /// <param name="gainEPerAdu">Optional e-/ADU. Lets the radius search weight shot noise against
    /// sky noise correctly; without it the search falls back to a sky-limited approximation.</param>
    public static Result? TryCentroid(
        float[] pixels, int width, int height, double seedX, double seedY, double? gainEPerAdu = null)
    {
        int x0 = Math.Max(0, (int)seedX - Box), x1 = Math.Min(width, (int)seedX + Box + 1);
        int y0 = Math.Max(0, (int)seedY - Box), y1 = Math.Min(height, (int)seedY + Box + 1);
        if (x1 <= x0 || y1 <= y0) return null;

        var boxValues = new List<double>();
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                float v = pixels[y * width + x];
                if (float.IsFinite(v)) boxValues.Add(v);
            }
        if (boxValues.Count == 0) return null;

        // Robust (MAD-based) estimator, not the naive squared-deviation one -- this box is wide
        // enough (Box=30, ~60px across) that a nearby bright star can fall inside it even though
        // it's well outside the actual aperture/annulus, and a squared-deviation sigma is easily
        // dragged up by that contamination. See ApertureStats.RobustMedianSigma's comment.
        var (bgMedian, bgSigmaRaw) = ApertureStats.RobustMedianSigma(boxValues);
        double bgSigma = Math.Max(bgSigmaRaw, 1e-6);

        // Peak search in a small window around the seed
        int pxC = (int)seedX, pyC = (int)seedY;
        int sx0 = Math.Max(x0, pxC - PeakSearch), sx1 = Math.Min(x1 - 1, pxC + PeakSearch);
        int sy0 = Math.Max(y0, pyC - PeakSearch), sy1 = Math.Min(y1 - 1, pyC + PeakSearch);

        double peakVal = double.NegativeInfinity;
        int peakX = pxC, peakY = pyC;
        for (int y = sy0; y <= sy1; y++)
            for (int x = sx0; x <= sx1; x++)
            {
                float v = pixels[y * width + x];
                if (!float.IsFinite(v)) continue;
                if (v > peakVal) { peakVal = v; peakX = x; peakY = y; }
            }
        if (double.IsNegativeInfinity(peakVal)) return null;

        // Significance test: mean of a small 3x3 patch around the peak against the background,
        // not just the single brightest pixel -- rejects lone noise spikes / cosmic-ray hits.
        // (Minor, deliberate divergence from the Python version: this skips non-finite pixels
        // when averaging the patch rather than including them, which only differs when NaNs
        // fall inside the 3x3 patch -- a rare edge case.)
        int ppx0 = Math.Max(x0, peakX - 1), ppx1 = Math.Min(x1 - 1, peakX + 1);
        int ppy0 = Math.Max(y0, peakY - 1), ppy1 = Math.Min(y1 - 1, peakY + 1);
        double patchSum = 0.0; int patchCount = 0;
        for (int y = ppy0; y <= ppy1; y++)
            for (int x = ppx0; x <= ppx1; x++)
            {
                float v = pixels[y * width + x];
                if (!float.IsFinite(v)) continue;
                patchSum += v; patchCount++;
            }
        if (patchCount == 0) return null;
        double patchMean = patchSum / patchCount;
        double significance = (patchMean - bgMedian) / (bgSigma / Math.Sqrt(patchCount));
        if (significance < SignificanceThreshold) return null;   // nothing star-like here

        // Flux-weighted centroid in a window around the peak (sub-pixel)
        int wx0 = Math.Max(x0, peakX - Win), wx1 = Math.Min(x1 - 1, peakX + Win);
        int wy0 = Math.Max(y0, peakY - Win), wy1 = Math.Min(y1 - 1, peakY + Win);
        double wsum = 0.0, wxSum = 0.0, wySum = 0.0;
        for (int y = wy0; y <= wy1; y++)
            for (int x = wx0; x <= wx1; x++)
            {
                float v = pixels[y * width + x];
                if (!float.IsFinite(v)) continue;
                double weight = Math.Max(0.0, v - bgMedian);
                wsum += weight;
                wxSum += x * weight;
                wySum += y * weight;
            }
        double cenX, cenY;
        if (wsum > 0) { cenX = wxSum / wsum; cenY = wySum / wsum; }
        else { cenX = peakX; cenY = peakY; }

        // v2.1.0: size the aperture by maximising SNR directly, rather than scaling a measured
        // FWHM. This supersedes BOTH the 1.5xFWHM rule and the fixed DefaultRadiusWhenUnreliable
        // fallback -- the curve of growth is measurable even when FWHM is not, so a faint star no
        // longer has to fall back to a guess. See ApertureStats.FindSnrOptimalRadius for the
        // measured comparison that motivated it.
        double peakSnr = (peakVal - bgMedian) / bgSigma;
        double snrRadius = ApertureStats.FindSnrOptimalRadius(
            pixels, width, height, cenX, cenY, bgMedian, bgSigma, gainEPerAdu);
        if (snrRadius > 0)
        {
            double r = Math.Max(2.0, Math.Round(snrRadius, 1));
            var (snrInner, snrOuter) = ApertureStats.FindStableAnnulus(pixels, width, height, cenX, cenY, r + 6.0);
            return new Result(cenX, cenY, r, snrInner, snrOuter);
        }

        // Only if the SNR walk found nothing usable (e.g. no positive signal at any radius) do
        // we fall back to the old FWHM path below, and its low-SNR guard.
        if (peakSnr < MinSnrForReliableFwhm)
        {
            double defaultRadius = DefaultRadiusWhenUnreliable;
            var (defInner, defOuter) = ApertureStats.FindStableAnnulus(pixels, width, height, cenX, cenY, defaultRadius + 6.0);
            return new Result(cenX, cenY, defaultRadius, defInner, defOuter);
        }

        // Radial profile from the centroid outward -- find the half-max radius, over the full
        // search box (not just the small peak/centroid windows above).
        double halfLevel = bgMedian + 0.5 * (peakVal - bgMedian);

        double maxDist = 0.0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                double dx = x - cenX, dy = y - cenY;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d > maxDist) maxDist = d;
            }
        double maxR = Math.Min(Box, maxDist);

        double? halfR = null;
        for (double r = 1.0; r <= maxR; r += 1.0)
        {
            var ring = new List<double>();
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    float v = pixels[y * width + x];
                    if (!float.IsFinite(v)) continue;
                    double dx = x - cenX, dy = y - cenY;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d >= r - 0.5 && d < r + 0.5) ring.Add(v);
                }
            if (ring.Count > 0)
            {
                ring.Sort();
                int n = ring.Count;
                double med = n % 2 == 1 ? ring[n / 2] : (ring[n / 2 - 1] + ring[n / 2]) / 2.0;
                if (med <= halfLevel) { halfR = r; break; }
            }
        }
        double halfRFinal = halfR ?? maxR * 0.5;

        double fwhm = 2.0 * halfRFinal;
        double radius = Math.Max(3.0, Math.Round(1.5 * fwhm, 1));

        // Verify the annulus has actually reached flat background rather than trusting the
        // fixed radius+6/+16 offset -- a sufficiently bright/broad star's wing can still be
        // declining well past that. See ApertureStats.FindStableAnnulus's comment.
        var (inner, outer) = ApertureStats.FindStableAnnulus(pixels, width, height, cenX, cenY, radius + 6.0);
        return new Result(cenX, cenY, radius, inner, outer);
    }
}
