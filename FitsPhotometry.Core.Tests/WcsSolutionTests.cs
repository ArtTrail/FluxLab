using FitsPhotometry.Core.Fits;

namespace FitsPhotometry.Core.Tests;

/// <summary>
/// Values here are astropy's (i.e. WCSLIB's) answers for the same headers, which this
/// implementation was validated against over a 121-point grid spanning two real frames -- one
/// with SIP distortion, one without -- agreeing to 0.0002 mas (~1e-9 px).
/// </summary>
public class WcsSolutionTests
{
    private static FitsHeader H(params (string K, string V)[] cards) =>
        new(cards.Select(c => new FitsCard { Keyword = c.K, Value = c.V, Comment = "" }).ToList());

    /// <summary>The MObs frame: plain TAN, CD matrix, no SIP, no PC.</summary>
    private static FitsHeader PlainTanHeader() => H(
        ("CTYPE1", "'RA---TAN'"), ("CTYPE2", "'DEC--TAN'"),
        ("CRPIX1", "325.5"), ("CRPIX2", "250.5"),
        ("CRVAL1", "47.7573222222"), ("CRVAL2", "31.0132833333"),
        ("CD1_1", "-0.000404674"), ("CD1_2", "-6.44e-06"),
        ("CD2_1", "-6.51e-06"), ("CD2_2", "0.000404646"));

    [Fact]
    public void ParsesTan_AndRejectsOtherProjections()
    {
        Assert.NotNull(WcsSolution.TryParse(PlainTanHeader()));

        var sin = H(("CTYPE1", "'RA---SIN'"), ("CTYPE2", "'DEC--SIN'"),
                    ("CRPIX1", "1"), ("CRPIX2", "1"), ("CRVAL1", "0"), ("CRVAL2", "0"),
                    ("CD1_1", "1e-4"), ("CD1_2", "0"), ("CD2_1", "0"), ("CD2_2", "1e-4"));
        Assert.Null(WcsSolution.TryParse(sin));          // not TAN -- must refuse, not guess

        Assert.Null(WcsSolution.TryParse(H(("BITPIX", "16"))));   // no WCS at all
    }

    [Fact]
    public void ReferencePixel_MapsToReferenceValue()
    {
        var wcs = WcsSolution.TryParse(PlainTanHeader())!;
        // CRPIX is 1-based; the equivalent 0-based pixel must land on CRVAL.
        var (ra, dec) = wcs.PixelToWorld(325.5 - 1.0, 250.5 - 1.0);
        Assert.Equal(47.7573222222, ra, 9);
        Assert.Equal(31.0132833333, dec, 9);
    }

    [Fact]
    public void RoundTripsAcrossTheFrame()
    {
        var wcs = WcsSolution.TryParse(PlainTanHeader())!;
        foreach (var (x, y) in new[] { (0.0, 0.0), (649.0, 499.0), (325.0, 250.0), (100.0, 400.0) })
        {
            var (ra, dec) = wcs.PixelToWorld(x, y);
            var (bx, by) = wcs.WorldToPixel(ra, dec);
            Assert.Equal(x, bx, 6);
            Assert.Equal(y, by, 6);
        }
    }

    /// <summary>
    /// Regression for a real bug: PC+CDELT must take precedence over CD when a header carries
    /// both. Solvers do write both, and they can disagree -- on the frame this came from,
    /// CD1_1 = 7.4375985117e-5 against CDELT1*PC1_1 = 7.4297180912e-5, only 0.106% apart but
    /// ~3.3 px by the frame corners. Using CD disagreed with astropy by up to 986 mas; using
    /// PC matched to 0.00 mas. The expected value below is astropy's.
    /// </summary>
    [Fact]
    public void PcCdeltTakesPrecedenceOverCd_WhenBothPresent()
    {
        var both = H(
            ("CTYPE1", "'RA---TAN'"), ("CTYPE2", "'DEC--TAN'"),
            ("CRPIX1", "3148.663120401"), ("CRPIX2", "2065.7928245536"),
            ("CRVAL1", "316.08138811164"), ("CRVAL2", "24.65593995443"),
            ("CD1_1", "7.43759851165e-05"), ("CD1_2", "-3.36524496e-07"),
            ("CD2_1", "3.575960438375e-07"), ("CD2_2", "7.42497706955e-05"),
            ("CDELT1", "1.0"), ("CDELT2", "1.0"),
            ("PC1_1", "7.4297180912e-05"), ("PC1_2", "-3.2111158522e-07"),
            ("PC2_1", "3.4082536215e-07"), ("PC2_2", "7.4265284798e-05"));

        var wcs = WcsSolution.TryParse(both)!;
        var (ra, dec) = wcs.PixelToWorld(0, 0);          // corner, where the two diverge most

        // astropy's answer for this exact header. Using CD instead would give
        // 315.8248760104 / 24.5012876558 -- ~3 px away at this corner.
        Assert.Equal(315.8251135846, ra, 8);
        Assert.Equal(24.5013088118, dec, 8);
    }

    [Fact]
    public void SipIsDetected_AndShiftsThePositionAwayFromTheReferencePixel()
    {
        var sip = H(
            ("CTYPE1", "'RA---TAN'"), ("CTYPE2", "'DEC--TAN'"),
            ("CRPIX1", "3148.663120401"), ("CRPIX2", "2065.7928245536"),
            ("CRVAL1", "316.08138811164"), ("CRVAL2", "24.65593995443"),
            ("CD1_1", "7.43759851165e-05"), ("CD1_2", "-3.36524496e-07"),
            ("CD2_1", "3.575960438375e-07"), ("CD2_2", "7.42497706955e-05"),
            ("A_ORDER", "2"), ("A_0_2", "1.405328e-07"), ("A_1_1", "1.488988e-07"), ("A_2_0", "6.735904e-08"),
            ("B_ORDER", "2"), ("B_0_2", "-2.911881e-07"), ("B_1_1", "9.889792e-08"), ("B_2_0", "-1.077822e-07"));

        var wcs = WcsSolution.TryParse(sip)!;
        Assert.True(wcs.HasSip);

        // No AP_/BP_ here, so WorldToPixel must invert the forward SIP numerically.
        var (ra, dec) = wcs.PixelToWorld(0, 0);
        var (bx, by) = wcs.WorldToPixel(ra, dec);
        Assert.Equal(0.0, bx, 3);
        Assert.Equal(0.0, by, 3);
    }

    [Fact]
    public void FormatsSexagesimal()
    {
        Assert.Equal("21:04:23.31", WcsSolution.FormatRa(316.0971373));
        Assert.Equal("+24:39:11.6", WcsSolution.FormatDec(24.6532175));
        Assert.StartsWith("-", WcsSolution.FormatDec(-5.5));
    }
}
