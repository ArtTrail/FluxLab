namespace FitsPhotometry.Core.Photometry;

/// <summary>
/// Detects a common power-of-two divisor shared by every pixel value. Some capture pipelines
/// left-shift a sensor's native ADC reading (e.g. 12-bit) into a wider container (e.g. 16-bit)
/// rather than storing it in the low bits, so every stored value ends up an exact multiple of a
/// power of two (16x for a 12-into-16-bit shift). Detected via a bitwise-OR reduction: any bit
/// that's 0 in the OR of every pixel value is 0 in every pixel, i.e. all values share that as a
/// common divisor. Ported verbatim from the Python app's _detect_adu_divisor.
///
/// This is exact and reliable for raw integer sensor data. On calibrated/float data, dark
/// subtraction and flat division destroy the exact-multiple pattern, so a "1" result here does
/// NOT mean no scaling is needed on a calibrated file -- see CameraProfile's AduScale field and
/// PhotometryEngine for how that ambiguity is handled (leave the value as-is rather than assume).
/// </summary>
public static class AduScaleDetector
{
    public static int Detect(float[] pixels, int maxShiftBits = 8)
    {
        long orAll = 0;
        bool any = false;
        foreach (var v in pixels)
        {
            if (!float.IsFinite(v)) continue;
            any = true;
            orAll |= (long)v;
        }
        if (!any || orAll == 0) return 1;

        int shift = 0;
        while (shift < maxShiftBits && (orAll & 1) == 0)
        {
            shift++;
            orAll >>= 1;
        }
        return 1 << shift;
    }
}
