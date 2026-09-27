namespace FitsPhotometry.Core.Meter;

/// <summary>
/// Result of one Exposure Meter evaluation. <see cref="SuggestedExposureSeconds"/> is a
/// first-class numeric field (not baked into <see cref="Recommendation"/>'s text) specifically
/// so a programmatic consumer -- a future NINA plugin deciding the next exposure length -- can
/// read the number directly instead of string-parsing a human-readable sentence.
/// </summary>
public sealed record ExposureMeterResult(
    ExposureMeterState State,
    string Detail,
    string Recommendation,
    double? SuggestedExposureSeconds,
    double? Electrons,
    double? TargetElectrons,
    double? SaturationPercent,
    string? SaturationBasis);
