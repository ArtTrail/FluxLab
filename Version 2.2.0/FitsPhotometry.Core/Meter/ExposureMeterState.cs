namespace FitsPhotometry.Core.Meter;

/// <summary>
/// The five exposure-quality states, in priority order (checked top to bottom -- the first one
/// that applies wins). Mirrors the Python app's Exposure Meter status strings, but as a proper
/// enum: a programmatic consumer (e.g. a future NINA plugin) branches on this and reads
/// ExposureMeterResult.SuggestedExposureSeconds, rather than parsing a formatted sentence.
/// </summary>
public enum ExposureMeterState
{
    /// <summary>Peak signal above sky is only a few times the measured sky noise -- too faint
    /// for reliable photometry regardless of the electron target.</summary>
    TooFaint,

    /// <summary>Peak pixel is close to clipping or going non-linear. Highest-priority safety
    /// check other than TooFaint.</summary>
    NearSaturation,

    /// <summary>Total electrons is well under the target -- photon noise is higher than it
    /// needs to be for the exposure time spent.</summary>
    LowSignal,

    /// <summary>Total electrons is well over the target with no saturation risk -- likely past
    /// the point where photon noise matters, at the cost of cadence.</summary>
    ExcessSignal,

    /// <summary>None of the above triggered.</summary>
    Good,
}
