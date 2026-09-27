using FitsPhotometry.Core.Photometry;

namespace FitsPhotometry.Core.Tests;

public class AduScaleDetectorTests
{
    [Fact]
    public void Detects_Common_Power_Of_Two_Divisor()
    {
        // Every value an exact multiple of 16 (a 12-bit ADC left-shifted 4 bits into 16-bit storage)
        float[] pixels = [480 * 16, 480 * 16, 15280, 7680, 32 * 16];
        Assert.Equal(16, AduScaleDetector.Detect(pixels));
    }

    [Fact]
    public void Returns_One_When_No_Common_Divisor()
    {
        float[] pixels = [198.877f, 198.914f, 8393.11f, 199.001f];
        Assert.Equal(1, AduScaleDetector.Detect(pixels));
    }

    [Fact]
    public void Returns_One_For_Empty_Or_All_NonFinite()
    {
        Assert.Equal(1, AduScaleDetector.Detect([]));
        Assert.Equal(1, AduScaleDetector.Detect([float.NaN, float.PositiveInfinity]));
    }
}
