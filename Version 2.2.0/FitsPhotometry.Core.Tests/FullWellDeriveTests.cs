using FitsPhotometry.Core.Camera;
using FitsPhotometry.Core.Fits;

namespace FitsPhotometry.Core.Tests;

/// <summary>v2.0.1: Full well is now derived from BITPIX + EGAIN (+ ADU scale) rather than being a
/// research-and-type-it-in field. No capture software writes a full-well keyword, but the ceiling
/// that actually governs saturation is min(physical pixel capacity, ADC ceiling x gain), and the
/// ADC term is fully derivable. Verified against a real ASI2600 frame at gain 100: 65535 x 0.242863
/// = 15,916 e-, versus the 50,000 e- datasheet figure that would understate saturation by ~3x.</summary>
public class FullWellDeriveTests
{
    private static FitsHeader HeaderWith(params (string Kw, string Val)[] cards)
    {
        var list = cards
            .Select(c => new FitsCard { Keyword = c.Kw, Value = c.Val, Comment = "" })
            .ToList();
        return new FitsHeader(list);
    }

    [Fact]
    public void Derives_AdcCeilingTimesGain_WhenNothingEntered()
    {
        var profile = new CameraProfile { GainEPerAdu = 0.242863, AduScale = 1 };
        CameraProfileResolver.ResolveFullWell(profile, HeaderWith(("BITPIX", "16")));

        Assert.NotNull(profile.FullWellElectrons);
        Assert.Equal(65535 * 0.242863, profile.FullWellElectrons!.Value, 1);
        Assert.Equal("derived (ADC ceiling x gain)", profile.FullWellSource);
    }

    [Fact]
    public void Overrides_StoredValueThatExceedsAdcCeiling()
    {
        // The datasheet's gain-0 figure is the physical pixel capacity; at gain 100 the ADC
        // saturates long first, so keeping 50,000 here would understate saturation ~3x.
        var profile = new CameraProfile
        {
            GainEPerAdu = 0.242863, AduScale = 1, FullWellElectrons = 50_000,
        };
        CameraProfileResolver.ResolveFullWell(profile, HeaderWith(("BITPIX", "16")));

        Assert.Equal(65535 * 0.242863, profile.FullWellElectrons!.Value, 1);
        Assert.Equal("derived (ADC ceiling x gain)", profile.FullWellSource);
    }

    [Fact]
    public void Keeps_StoredValueBelowAdcCeiling_BecausePixelFillsFirst()
    {
        var profile = new CameraProfile
        {
            GainEPerAdu = 0.242863, AduScale = 1, FullWellElectrons = 9_000,
        };
        CameraProfileResolver.ResolveFullWell(profile, HeaderWith(("BITPIX", "16")));

        Assert.Equal(9_000, profile.FullWellElectrons!.Value, 1);
        Assert.Equal("manual entry (pixel-limited)", profile.FullWellSource);
    }

    [Fact]
    public void AccountsForAduScale_OnBitShiftedData()
    {
        // 12-bit ADC left-shifted into a 16-bit container: the real ADC ceiling is 65535/16,
        // not 65535. Ignoring AduScale would inflate the result by exactly 16x.
        var profile = new CameraProfile { GainEPerAdu = 1.0, AduScale = 16 };
        CameraProfileResolver.ResolveFullWell(profile, HeaderWith(("BITPIX", "16")));

        Assert.Equal(65535 / 16.0, profile.FullWellElectrons!.Value, 1);
    }

    [Fact]
    public void DoesNothing_OnFloatData_WhereThereIsNoAdcCeiling()
    {
        var profile = new CameraProfile { GainEPerAdu = 0.5, AduScale = 1 };
        CameraProfileResolver.ResolveFullWell(profile, HeaderWith(("BITPIX", "-32")));

        Assert.Null(profile.FullWellElectrons);
        Assert.Equal("", profile.FullWellSource);
    }

    [Fact]
    public void DoesNothing_WhenGainUnknown()
    {
        var profile = new CameraProfile { AduScale = 1 };
        CameraProfileResolver.ResolveFullWell(profile, HeaderWith(("BITPIX", "16")));

        Assert.Null(profile.FullWellElectrons);
    }
}
