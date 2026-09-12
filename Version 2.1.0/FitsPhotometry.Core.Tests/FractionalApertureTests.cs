using FitsPhotometry.Core.Camera;
using FitsPhotometry.Core.Fits;
using FitsPhotometry.Core.Photometry;

namespace FitsPhotometry.Core.Tests;

/// <summary>v2.1.0 OSC/aperture work: fractional-pixel aperture overlap, SNR-optimal radius
/// selection, and Bayer-pattern reporting.</summary>
public class FractionalApertureTests
{
    private static float[] Flat(int size, float value)
    {
        var p = new float[size * size];
        for (int i = 0; i < p.Length; i++) p[i] = value;
        return p;
    }

    [Fact]
    public void EffectiveArea_ApproachesCircleArea_NotPixelCount()
    {
        // On a flat field the effective aperture area should track pi*r^2. The old pixel-centre
        // test quantised this to whole pixels; fractional overlap should land within ~1%.
        const int S = 81;
        var pixels = Flat(S, 100f);
        var geom = new ApertureGeometry(40.0, 40.0, 7.3, 12.0, 20.0);

        var r = ApertureStats.Compute(pixels, S, S, geom);

        Assert.NotNull(r);
        double expected = Math.PI * 7.3 * 7.3;
        Assert.InRange(r!.NAperturePixels, expected * 0.99, expected * 1.01);
    }

    [Fact]
    public void EffectiveArea_VariesSmoothlyWithSubPixelPosition()
    {
        // The whole point of the change: area (and therefore measured flux on a flat field)
        // must not jump as the centre moves sub-pixel. Hard-edged masks stepped by whole pixels.
        const int S = 81;
        var pixels = Flat(S, 100f);
        var areas = new List<double>();
        for (double off = 0.0; off < 1.0; off += 0.1)
        {
            var g = new ApertureGeometry(40.0 + off, 40.0 + off, 7.0, 12.0, 20.0);
            areas.Add(ApertureStats.Compute(pixels, S, S, g)!.NAperturePixels);
        }
        double spread = areas.Max() - areas.Min();
        Assert.True(spread < 1.0, $"area varied by {spread:F2}px across sub-pixel offsets; expected <1");
    }

    [Fact]
    public void FlatField_SubtractsToZero_WithFractionalArea()
    {
        // Sky subtraction uses sky_median * effective_area. If the area were an integer count
        // while the sum used fractional weights, a flat field would not cancel to ~0.
        const int S = 81;
        var pixels = Flat(S, 250f);
        var geom = new ApertureGeometry(40.4, 40.6, 6.5, 12.0, 20.0);

        var r = ApertureStats.Compute(pixels, S, S, geom);

        Assert.NotNull(r);
        Assert.InRange(r!.SubSum, -0.01, 0.01);
    }

    [Fact]
    public void SnrOptimalRadius_StopsAtTheStar_NotBeyondIt()
    {
        // Flux confined to a small core: the optimum must sit near the core, because every
        // further annulus adds sky noise and no signal.
        const int S = 81;
        var pixels = Flat(S, 100f);
        for (int y = 38; y <= 42; y++)
            for (int x = 38; x <= 42; x++)
                pixels[y * S + x] = 400f;

        double r = ApertureStats.FindSnrOptimalRadius(pixels, S, S, 40, 40, 100.0, 10.0, gain: null);

        Assert.InRange(r, 2.0, 5.0);
    }

    [Fact]
    public void SnrOptimalRadius_StopsBeforeSwallowingANeighbour()
    {
        // Regression for a real failure: on an MObs frame the search grew to its 30px ceiling and
        // enclosed stars 18-29px away, because their flux RAISED the SNR metric again after the
        // target's own profile had flattened. Taking a global SNR maximum is unsafe; the search
        // must stop once the target's own light runs out.
        const int S = 121;
        var pixels = Flat(S, 100f);
        for (int y = 58; y <= 62; y++)           // target at (60,60)
            for (int x = 58; x <= 62; x++)
                pixels[y * S + x] = 400f;
        for (int y = 82; y <= 88; y++)           // bright neighbour ~25px away
            for (int x = 57; x <= 63; x++)
                pixels[y * S + x] = 900f;

        double r = ApertureStats.FindSnrOptimalRadius(pixels, S, S, 60, 60, 100.0, 10.0, gain: null);

        Assert.True(r < 15.0, $"radius {r} grew toward the neighbour at ~25px; expected to stop near the target");
    }

    [Fact]
    public void ResolveGain_StopsClaimingHeaderSource_WhenFileHasNoEgain()
    {
        // MObs frames carry no EGAIN. Opening one after an ASI2600 frame kept that camera's
        // 0.2429 e-/ADU while still reporting "FITS header (EGAIN)" -- silently measuring one
        // camera's data with another camera's gain, with nothing in the UI to reveal it.
        var profile = new CameraProfile { GainEPerAdu = 0.242863, GainSource = "FITS header (EGAIN)" };
        var noEgain = new FitsHeader(new List<FitsCard>
            { new() { Keyword = "BITPIX", Value = "16", Comment = "" } });

        CameraProfileResolver.ResolveGain(profile, noEgain);

        Assert.Equal(0.242863, profile.GainEPerAdu);          // still used -- nothing better exists
        Assert.DoesNotContain("FITS header", profile.GainSource);
        Assert.Contains("carried over", profile.GainSource);
    }

    [Fact]
    public void DescribeColorFilter_ReportsBayerPattern_AndMonoWhenAbsent()
    {
        var osc = new FitsHeader(new List<FitsCard>
            { new() { Keyword = "BAYERPAT", Value = "RGGB", Comment = "" } });
        Assert.Contains("RGGB", CameraProfileResolver.DescribeColorFilter(osc));
        Assert.Contains("OSC", CameraProfileResolver.DescribeColorFilter(osc));

        var mono = new FitsHeader(new List<FitsCard>
            { new() { Keyword = "EGAIN", Value = "1.0", Comment = "" } });
        Assert.Contains("mono", CameraProfileResolver.DescribeColorFilter(mono));
    }
}
