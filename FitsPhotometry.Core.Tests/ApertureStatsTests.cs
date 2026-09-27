using FitsPhotometry.Core.Photometry;

namespace FitsPhotometry.Core.Tests;

public class ApertureStatsTests
{
    private static float[] MakeGrid(int width, int height, float background, (int x, int y, float value) peak)
    {
        var pixels = new float[width * height];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = background;
        pixels[peak.y * width + peak.x] = peak.value;
        return pixels;
    }

    [Fact]
    public void Sky_Subtracted_Sum_Matches_Manual_Calculation()
    {
        // 10x10 grid, background 7680 everywhere, single bright pixel at (5,5).
        var pixels = MakeGrid(10, 10, 7680f, (5, 5, 15280f));
        var aperture = new ApertureGeometry(5, 5, 3, 4, 4.9);

        var result = ApertureStats.Compute(pixels, 10, 10, aperture);

        Assert.NotNull(result);
        Assert.Equal(7680.0, result!.SkyMedian, 3);
        Assert.Equal(15280.0, result.Peak, 3);
        // sub_sum = raw_sum - sky_median * n_ap; with a uniform background plus one bright
        // pixel, sub_sum should equal exactly (peak - background), regardless of aperture size,
        // since every other aperture pixel is at the background level and cancels out.
        Assert.Equal(15280.0 - 7680.0, result.SubSum, 3);
    }

    [Fact]
    public void Returns_Null_When_Aperture_Is_Off_Frame()
    {
        var pixels = MakeGrid(10, 10, 100f, (5, 5, 500f));
        var aperture = new ApertureGeometry(-50, -50, 3, 4, 5);

        Assert.Null(ApertureStats.Compute(pixels, 10, 10, aperture));
    }

    [Fact]
    public void SigmaClippedMedianStd_Rejects_Outlier()
    {
        var values = new List<double> { 10, 10, 10, 10, 10, 10, 10, 10, 10, 1000 };
        var (median, std) = ApertureStats.SigmaClippedMedianStd(values);
        Assert.Equal(10.0, median, 3);
    }

    [Fact]
    public void RobustMedianSigma_Resists_Gradual_Contamination_Where_Naive_Estimator_Fails()
    {
        // 60 clean background samples (median 100, true sigma ~14.83 by MAD / ~8.16 by naive
        // std) plus 40 samples on a gradually-declining contaminating profile (115..388) --
        // simulating a nearby star's wing, not a single sharp outlier. A single extreme outlier
        // gets cleanly sigma-clipped by either estimator; a broad, gradually-declining
        // contaminating population -- closer to what a real neighboring star's PSF wing looks
        // like -- is what actually breaks the naive squared-deviation estimator, confirmed
        // against a real case (Kelt-8 field) where a neighbor 34px away inflated a squared-
        // deviation sigma by 2.4x.
        var values = new List<double>();
        for (int i = 0; i < 20; i++) values.Add(90);
        for (int i = 0; i < 20; i++) values.Add(100);
        for (int i = 0; i < 20; i++) values.Add(110);
        for (int i = 0; i < 40; i++) values.Add(115 + i * 7);

        var (naiveMedian, naiveStd) = ApertureStats.SigmaClippedMedianStd(values);
        var (robustMedian, robustSigma) = ApertureStats.RobustMedianSigma(values);

        // The naive estimator is dragged well off the true background by this contamination --
        // both its median and its sigma are visibly wrong.
        Assert.True(naiveStd > 50.0, $"expected naive std to be grossly inflated, got {naiveStd}");
        Assert.NotEqual(100.0, naiveMedian, 3);

        // The robust estimator recovers the true clean-background values essentially exactly.
        Assert.Equal(100.0, robustMedian, 3);
        Assert.Equal(14.826, robustSigma, 2);
    }
}
