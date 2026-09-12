using System;
using System.Collections.Generic;

namespace FitsPhotometry.Core.Photometry;

/// <summary>Raw aperture-photometry measurements, before any gain/electron conversion.
/// Ported from the Python app's _compute_photometry.</summary>
public sealed record ApertureResult(
    int NAperturePixels, double SkyMedian, double SkySigma,
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
    /// Measure a circular aperture and sky annulus on a 2-D pixel buffer (row-major, Width x
    /// Height). Sky level is the sigma-clipped median ADU/pixel in the annulus, subtracted from
    /// the aperture sum (raw_sum - sky_median * N_pixels) -- same convention as the Python app.
    /// Returns null if the aperture contains no valid (finite) pixels.
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
        int nAp = 0;
        var annulusValues = new List<double>();

        for (int y = y0; y < y1; y++)
        {
            double dy = y - ap.CenterY;
            for (int x = x0; x < x1; x++)
            {
                float v = pixels[y * width + x];
                if (!float.IsFinite(v)) continue;

                double dx = x - ap.CenterX;
                double dist2 = dx * dx + dy * dy;

                if (dist2 <= r2)
                {
                    rawSum += v;
                    nAp++;
                    if (v > peak) peak = v;
                }
                else if (dist2 >= rIn2 && dist2 <= rOut2)
                {
                    annulusValues.Add(v);
                }
            }
        }

        if (nAp == 0) return null;

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
