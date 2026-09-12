using FitsPhotometry.Core.Photometry;

namespace FitsPhotometry.Core.Tests;

public class StarCentroidTests
{
    private const int Size = 70;
    private const double Bg = 100.0;
    private const double D = 10.0;

    /// <summary>
    /// A 3-valued repeating background (Bg-D, Bg, Bg+D) rather than a simple two-valued
    /// checkerboard: with only two distinct values, the sample median lands on whichever value
    /// happens to be very slightly more frequent (not their average), giving an unpredictable,
    /// not-actually-Bg median. Three evenly-spread values keep the median pinned at Bg and the
    /// spread predictable -- D*sqrt(2/3)=8.16 under a squared-deviation estimator, or
    /// D*1.4826=14.83 under RobustMedianSigma's MAD-based one (StarCentroid uses the latter) --
    /// both confirmed against this exact generator via a standalone diagnostic before relying on
    /// them here.
    /// </summary>
    private static float[] MakeBackground()
    {
        var pixels = new float[Size * Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                pixels[y * Size + x] = (float)(Bg + D * ((x + y) % 3 - 1));
        return pixels;
    }

    [Fact]
    public void LowSnrStar_FallsBackToDefaultRadius_InsteadOfNoisyFwhm()
    {
        var pixels = MakeBackground();
        // A unique, strictly-highest center pixel (so the peak search unambiguously lands on
        // it, not a tied corner of a flat plateau) surrounded by a slightly-elevated 5x5 patch.
        // StarCentroid's background box now uses ApertureStats.RobustMedianSigma (MAD-based),
        // which correctly reports a higher, more realistic sigma (14.83) for this background
        // than the old squared-deviation estimator did (8.16) -- so peak SNR = (250-100)/14.83 =
        // 10.1, comfortably below the 15.0 reliability threshold, while the patch is still
        // bright/wide enough to pass the initial "is this a star at all" significance gate.
        for (int y = 33; y <= 37; y++)
            for (int x = 33; x <= 37; x++)
                pixels[y * Size + x] = 200f;
        pixels[35 * Size + 35] = 250f;

        var result = StarCentroid.TryCentroid(pixels, Size, Size, 35, 35);

        Assert.NotNull(result);
        Assert.Equal(6.0, result!.Value.Radius, 3);
        Assert.Equal(12.0, result.Value.AnnulusInner, 3);
        Assert.Equal(22.0, result.Value.AnnulusOuter, 3);
    }

    [Fact]
    public void HighSnrStar_StillUsesMeasuredFwhm_NotTheDefault()
    {
        var pixels = MakeBackground();
        // A genuine, gradually-declining radial profile (peak SNR (400-100)/14.83 = 20.2 under
        // the robust estimator, still well above the 15.0 threshold) wide enough that its real
        // measured radius (15, confirmed via a standalone diagnostic run) is unambiguously
        // different from the low-SNR fallback's fixed 6.0 -- so this test can't accidentally
        // pass via a coincidental match between the two paths' radii.
        for (int y = 20; y <= 50; y++)
            for (int x = 20; x <= 50; x++)
            {
                double d = Math.Sqrt((x - 35) * (x - 35) + (y - 35) * (y - 35));
                if (d <= 5) pixels[y * Size + x] = 400f;
                else if (d <= 10) pixels[y * Size + x] = 250f;
                else if (d <= 15) pixels[y * Size + x] = 150f;
            }

        var result = StarCentroid.TryCentroid(pixels, Size, Size, 35, 35);

        Assert.NotNull(result);
        Assert.NotEqual(6.0, result!.Value.Radius, 3);
        Assert.InRange(result.Value.Radius, 3.0, 20.0);
    }

    [Fact]
    public void BlankSky_ReturnsNull()
    {
        var pixels = MakeBackground();
        Assert.Null(StarCentroid.TryCentroid(pixels, Size, Size, 10, 10));
    }
}
