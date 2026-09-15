using System;
using System.Collections.Generic;

namespace FitsPhotometry.Core.Photometry;

/// <summary>Raw aperture-photometry measurements, before any gain/electron conversion.
/// Ported from the Python app's _compute_photometry.</summary>
/// <summary>NAperturePixels is the EFFECTIVE aperture area in pixels, and is fractional as of
/// v2.1.0 -- edge pixels contribute the fraction of their area that falls inside the circle,
/// rather than counting 0 or 1 on a pixel-center test. See ApertureStats.Compute.</summary>
public sealed record ApertureResult(
    double NAperturePixels, double SkyMedian, double SkySigma,
    double RawSum, double SubSum, double Peak);

public static class ApertureStats
{
    /// <summary>Iteratively sigma-clip a list of values, returning (median, stddev) of what
    /// remains. Ported verbatim from the Python app's _sigma_clipped_median.</summary>
    public static (double Median, double Std) SigmaClippedMedianStd(List<double> values, double sigma = 3.0, int iters = 3)
    {
        var arr = values;
        for (int i = 0; i < iters; i++)
        {
            if (arr.Count == 0) break;
            double med = Median(arr);
            double std = StdDev(arr, med);
            if (std == 0) break;
            var kept = new List<double>(arr.Count);
            foreach (var v in arr)
                if (Math.Abs(v - med) <= sigma * std) kept.Add(v);
            if (kept.Count == arr.Count) break;
            arr = kept;
        }
        if (arr.Count == 0) return (0.0, 0.0);
        return (Median(arr), StdDev(arr, Median(arr)));
    }

    /// <summary>
    /// Iteratively clip a list of values using a robust (median absolute deviation) sigma
    /// estimator, rather than SigmaClippedMedianStd's mean-squared-deviation one. MAD-based
    /// sigma has a much higher breakdown point (up to 50% contamination before it's thrown off,
    /// versus effectively 0% for a squared-deviation estimator) -- confirmed against a real
    /// case where a bright neighbor ~34px away, well outside the actual aperture/annulus but
    /// still inside StarCentroid's wider background-sampling box, inflated the naive estimator's
    /// sigma by ~2.4x even though it was only a modest fraction of the box's pixels. Used for
    /// StarCentroid's box background estimate, where a nearby star falling in a wide search
    /// region is a real, not hypothetical, risk; not used for the aperture's own sky annulus,
    /// which is deliberately thin and post-centroid, and hasn't shown this failure mode.
    /// </summary>
    public static (double Median, double Sigma) RobustMedianSigma(List<double> values, double clipSigma = 3.0, int iters = 3)
    {
        var arr = values;
        for (int i = 0; i < iters; i++)
        {
            if (arr.Count == 0) break;
            double med = Median(arr);
            double sigma = MadSigma(arr, med);
            if (sigma == 0) break;
            var kept = new List<double>(arr.Count);
            foreach (var v in arr)
                if (Math.Abs(v - med) <= clipSigma * sigma) kept.Add(v);
            if (kept.Count == arr.Count) break;
            arr = kept;
        }
        if (arr.Count == 0) return (0.0, 0.0);
        double finalMedian = Median(arr);
        return (finalMedian, MadSigma(arr, finalMedian));
    }

    /// <summary>Median absolute deviation from <paramref name="median"/>, scaled by 1.4826 (the
    /// standard factor that makes MAD a sigma-equivalent estimator for a Gaussian distribution).</summary>
    private static double MadSigma(List<double> values, double median)
    {
        if (values.Count == 0) return 0.0;
        var deviations = new List<double>(values.Count);
        foreach (var v in values) deviations.Add(Math.Abs(v - median));
        return Median(deviations) * 1.4826;
    }

    /// <summary>
    /// Starting from a candidate annulus (startInner to startInner+bandWidth), grow it outward
    /// in steps until the robust median stops dropping by a statistically significant amount
    /// between consecutive steps -- rather than trusting a single fixed offset from the
    /// aperture radius, which a sufficiently bright/broad star's extended wing can still occupy.
    /// Confirmed against a real case: a star with peak 25,063 ADU still showed a declining wing
    /// profile all the way through its "radius+6 to radius+16" annulus (167.7 median vs a true
    /// far-field sky of 122.7, a ~37% bias), because the fixed offset didn't scale with how far
    /// this particular star's wing actually extended.
    ///
    /// "Statistically significant" is judged against the standard error of the median (roughly
    /// 1.253*sigma/sqrt(n)), not sigma itself -- each band typically samples hundreds to
    /// thousands of pixels, so the median is known far more precisely than any individual
    /// pixel's noise, and comparing against sigma directly would stop growing too early on a
    /// real, systematic decline (as happened when first trying this with a naive threshold).
    /// </summary>
    public static (double Inner, double Outer) FindStableAnnulus(
        float[] pixels, int width, int height, double centerX, double centerY,
        double startInner, double bandWidth = 10.0, double step = 8.0, int maxIterations = 8)
    {
        double inner = startInner;
        double outer = startInner + bandWidth;
        var (prevMedian, _, _) = SampleBandRobust(pixels, width, height, centerX, centerY, inner, outer);

        for (int i = 0; i < maxIterations; i++)
        {
            double nextInner = inner + step;
            double nextOuter = outer + step;
            var (nextMedian, nextSigma, nextCount) =
                SampleBandRobust(pixels, width, height, centerX, centerY, nextInner, nextOuter);
            if (nextCount == 0) break;

            double standardErrorOfMedian = 1.253 * Math.Max(nextSigma, 1e-6) / Math.Sqrt(nextCount);
            double drop = prevMedian - nextMedian;
            if (drop < 3.0 * standardErrorOfMedian)
                break;   // no longer a statistically significant decline -- background reached

            inner = nextInner;
            outer = nextOuter;
            prevMedian = nextMedian;
        }

        return (inner, outer);
    }

    /// <summary>
    /// Choose the aperture radius that maximises SNR, by walking the curve of growth outward and
    /// evaluating SNR at each step rather than scaling a measured FWHM by a fixed factor.
    ///
    /// Why this replaces the 1.5 x FWHM rule: that rule assumes the star dominates its own
    /// noise, which fails in the sky-limited regime every faint target lives in. On a real
    /// frame (peak only 8.4x sky sigma) the true optimum was r=8 px at SNR 41.6, while 1.5xFWHM
    /// would have chosen r=17 -- capturing twice the signal for WORSE precision (SNR 30.7),
    /// because each added annulus brought in more sky noise than starlight. It also sidesteps
    /// having to measure FWHM at all, which is unreliable on a faint star (the caller's own
    /// MinSnrForReliableFwhm guard exists precisely because of that) and noisier still on an
    /// undebayered colour mosaic, where per-plane fits of the same star ranged 6.6-17.5 px.
    ///
    /// SNR = S / sqrt(S + n * sigma^2) with everything in electrons, so <paramref name="gain"/>
    /// converts. Without a known gain the shot-noise term cannot be formed correctly (ADU are
    /// not electrons), so it falls back to the sky-limited form S / (sigma * sqrt(n)) rather
    /// than silently mis-weighting the two noise sources.
    /// </summary>
    public static double FindSnrOptimalRadius(
        float[] pixels, int width, int height, double centerX, double centerY,
        double skyMedian, double skySigma, double? gain,
        double minRadius = 2.0, double maxRadius = 30.0, double step = 0.5)
    {
        if (skySigma <= 0) return minRadius;

        double bestR = minRadius, bestSnr = double.NegativeInfinity;

        // Curve-of-growth convergence tracking. The SNR peak alone is NOT a safe stopping rule:
        // it is a global maximum over the scanned range, and a neighbouring star entering the
        // aperture raises SNR again further out, so an unguarded search will happily grow until
        // it swallows the neighbour. Seen on a real MObs frame -- the target's own flux had
        // flattened by r~10 (marginal 0.24 ADU/px), then marginal flux ROSE again to 0.88 ADU/px
        // at r=25 as stars 18-29 px away came inside, and the search returned r=29.5 with another
        // star measured as part of the target.
        //
        // So stop once the star's own profile has ended -- marginal flux per newly added pixel
        // falling to a small fraction of the sky noise -- and take the best SNR found up to that
        // point. Requiring two consecutive flat steps avoids stopping on a single noisy ring.
        const double ConvergedFracOfSigma = 0.25;
        int flatSteps = 0;
        double prevSignal = 0.0, prevArea = 0.0;

        for (double r = minRadius; r <= maxRadius; r += step)
        {
            var geom = new ApertureGeometry(centerX, centerY, r, r + 2, r + 10);
            double r2 = r * r;
            int x0 = Math.Max(0, (int)Math.Floor(centerX - r - 1));
            int x1 = Math.Min(width, (int)Math.Ceiling(centerX + r + 1));
            int y0 = Math.Max(0, (int)Math.Floor(centerY - r - 1));
            int y1 = Math.Min(height, (int)Math.Ceiling(centerY + r + 1));
            if (x1 <= x0 || y1 <= y0) break;

            double sum = 0.0, n = 0.0;
            for (int y = y0; y < y1; y++)
            {
                double dy = y - centerY;
                for (int x = x0; x < x1; x++)
                {
                    float v = pixels[y * width + x];
                    if (!float.IsFinite(v)) continue;
                    double dx = x - centerX;
                    if (dx * dx + dy * dy <= r2) { sum += v; n += 1.0; }
                }
            }
            if (n <= 0) continue;

            double signal = sum - skyMedian * n;
            if (signal <= 0) continue;

            double snr;
            if (gain is double g && g > 0)
            {
                double sE = signal * g, sigE = skySigma * g;
                snr = sE / Math.Sqrt(sE + n * sigE * sigE);
            }
            else
            {
                snr = signal / (skySigma * Math.Sqrt(n));   // sky-limited fallback
            }

            if (snr > bestSnr) { bestSnr = snr; bestR = r; }

            // Has the star's own light run out? Compare flux newly enclosed by this step
            // against the sky noise of the pixels it added.
            double addedArea = n - prevArea;
            if (addedArea > 0 && prevArea > 0)
            {
                double marginalPerPixel = (signal - prevSignal) / addedArea;
                if (marginalPerPixel < ConvergedFracOfSigma * skySigma)
                {
                    if (++flatSteps >= 2) break;
                }
                else flatSteps = 0;
            }
            prevSignal = signal;
            prevArea = n;
        }

        return bestR;
    }

    private static (double Median, double Sigma, int Count) SampleBandRobust(
        float[] pixels, int width, int height, double cx, double cy, double rIn, double rOut)
    {
        double rIn2 = rIn * rIn, rOut2 = rOut * rOut;
        int x0 = Math.Max(0, (int)(cx - rOut - 1));
        int x1 = Math.Min(width, (int)(cx + rOut + 1) + 1);
        int y0 = Math.Max(0, (int)(cy - rOut - 1));
        int y1 = Math.Min(height, (int)(cy + rOut + 1) + 1);

        var vals = new List<double>();
        for (int y = y0; y < y1; y++)
        {
            double dy = y - cy;
            for (int x = x0; x < x1; x++)
            {
                double dx = x - cx;
                double d2 = dx * dx + dy * dy;
                if (d2 >= rIn2 && d2 <= rOut2)
                {
                    float v = pixels[y * width + x];
                    if (float.IsFinite(v)) vals.Add(v);
                }
            }
        }
        var (med, sigma) = RobustMedianSigma(vals);
        return (med, sigma, vals.Count);
    }

    private static double Median(List<double> values)
    {
        var sorted = new List<double>(values);
        sorted.Sort();
        int n = sorted.Count;
        if (n == 0) return 0.0;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    private static double StdDev(List<double> values, double mean)
    {
        if (values.Count == 0) return 0.0;
        double sumSq = 0.0;
        foreach (var v in values) sumSq += (v - mean) * (v - mean);
        return Math.Sqrt(sumSq / values.Count);
    }

    /// <summary>
    /// Fraction of a unit pixel centred at (dx, dy) relative to the aperture centre that falls
    /// inside a circle of the given radius, by 8x8 supersampling (64 sub-samples, ~1.5% area
    /// quantisation -- far finer than the 0/1 quantisation it replaces, and cheap because only
    /// the thin boundary band ever calls it).
    ///
    /// Analytic circle-rectangle overlap would be exact, but is fiddly to get right across all
    /// the corner cases and this is not the accuracy-limiting step: supersampling's residual
    /// error is already well below the sky noise on any real frame.
    /// </summary>
    private static double PixelOverlapFraction(double dx, double dy, double radius)
    {
        const int S = 8;
        double r2 = radius * radius;
        int inside = 0;
        for (int j = 0; j < S; j++)
        {
            double sy = dy - 0.5 + (j + 0.5) / S;
            double sy2 = sy * sy;
            for (int i = 0; i < S; i++)
            {
                double sx = dx - 0.5 + (i + 0.5) / S;
                if (sx * sx + sy2 <= r2) inside++;
            }
        }
        return inside / (double)(S * S);
    }

    /// <summary>
    /// Measure a circular aperture and sky annulus on a 2-D pixel buffer (row-major, Width x
    /// Height). Sky level is the sigma-clipped median ADU/pixel in the annulus, subtracted from
    /// the aperture sum (raw_sum - sky_median * effective_area) -- same convention as the Python
    /// app. Returns null if the aperture contains no valid (finite) pixels.
    ///
    /// v2.1.0: the aperture uses FRACTIONAL pixel overlap rather than a pixel-centre in/out
    /// test. The hard-edged version made the measured flux jump as the star drifted sub-pixel
    /// between frames, because whole pixels flicked in and out of the aperture at once --
    /// measured on a real frame by re-measuring one star across 81 sub-pixel offsets spanning
    /// +/-1 px: scatter fell from 0.90% (9.8 mmag) to 0.64% (7.0 mmag), a ~30% reduction, with
    /// no change to the underlying data. That is a pure systematic, so it matters most exactly
    /// where it hurts: a time series of a drifting star.
    /// </summary>
    public static ApertureResult? Compute(float[] pixels, int width, int height, ApertureGeometry aperture)
    {
        var ap = aperture.Normalized();
        double r2 = ap.Radius * ap.Radius;
        double rIn2 = ap.AnnulusInner * ap.AnnulusInner;
        double rOut2 = ap.AnnulusOuter * ap.AnnulusOuter;

        int x0 = Math.Max(0, (int)Math.Floor(ap.CenterX - ap.AnnulusOuter - 1));
        int x1 = Math.Min(width, (int)Math.Ceiling(ap.CenterX + ap.AnnulusOuter + 1));
        int y0 = Math.Max(0, (int)Math.Floor(ap.CenterY - ap.AnnulusOuter - 1));
        int y1 = Math.Min(height, (int)Math.Ceiling(ap.CenterY + ap.AnnulusOuter + 1));
        if (x1 <= x0 || y1 <= y0) return null;

        double rawSum = 0.0;
        double peak = double.NegativeInfinity;
        double nAp = 0.0;
        var annulusValues = new List<double>();

        // Edge pixels straddling the aperture boundary: a pixel is "inside" only if its whole
        // area is, "outside" only if none of it is, and anything between contributes its actual
        // overlap fraction. These bounds bracket the boundary band -- a unit pixel's centre can
        // be at most half its diagonal (sqrt(2)/2) from the circle and still be partly in/out.
        const double Half = 0.70710678; // sqrt(2)/2
        double rInner = Math.Max(0.0, ap.Radius - Half), rOuter = ap.Radius + Half;
        double rInner2 = rInner * rInner, rOuter2 = rOuter * rOuter;

        for (int y = y0; y < y1; y++)
        {
            double dy = y - ap.CenterY;
            for (int x = x0; x < x1; x++)
            {
                float v = pixels[y * width + x];
                if (!float.IsFinite(v)) continue;

                double dx = x - ap.CenterX;
                double dist2 = dx * dx + dy * dy;

                if (dist2 <= rInner2)
                {
                    rawSum += v;                 // wholly inside
                    nAp += 1.0;
                    if (v > peak) peak = v;
                }
                else if (dist2 <= rOuter2)
                {
                    double w = PixelOverlapFraction(dx, dy, ap.Radius);
                    if (w > 0.0)
                    {
                        rawSum += v * w;
                        nAp += w;
                        if (v > peak) peak = v;  // saturation cares about the pixel, not its weight
                    }
                    else if (dist2 >= rIn2 && dist2 <= rOut2)
                    {
                        annulusValues.Add(v);
                    }
                }
                else if (dist2 >= rIn2 && dist2 <= rOut2)
                {
                    annulusValues.Add(v);
                }
            }
        }

        if (nAp <= 0.0) return null;

        // Robust, not naive: confirmed necessary in practice, not just in principle -- a sky
        // annulus grown outward by FindStableAnnulus to escape its own star's wing can end up
        // reaching into a *different* nearby star instead (found on a real pair only 34px
        // apart). The naive estimator's sigma was thrown off by ~2.5x in that exact case; the
        // median was also measurably biased. FindStableAnnulus already uses this same robust
        // estimator internally to decide where to place the annulus -- this keeps the finally
        // reported SkyMedian/SkySigma consistent with that decision instead of re-measuring the
        // chosen annulus with a less robust method at the last step.
        var (skyMedian, skySigma) = RobustMedianSigma(annulusValues);
        double skyTotal = skyMedian * nAp;
        double subSum = rawSum - skyTotal;

        return new ApertureResult(nAp, skyMedian, skySigma, rawSum, subSum, peak);
    }
}
