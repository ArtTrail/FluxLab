using System;
using FitsPhotometry.Core.Camera;
using FitsPhotometry.Core.Fits;
using FitsPhotometry.Core.Photometry;

namespace FitsPhotometry.Core.Meter;

/// <summary>
/// Combined exposure-quality evaluation: saturation headroom, SNR floor, and total-electron
/// target, folded into one recommendation. Ported from the Python app's
/// _evaluate_exposure_meter/_classify_exposure_level.
/// </summary>
public static class ExposureMeter
{
    /// <summary>% of the ADC ceiling / full well considered "near saturation".</summary>
    public const double SaturationWarnPct = 85.0;

    /// <summary>peak-above-sky / sky-sigma considered "too faint".</summary>
    public const double LowSnrThreshold = 10.0;

    /// <summary>Below this fraction of the target, exposure is judged too short for a good
    /// photon-noise floor.</summary>
    public const double TargetLowFrac = 0.7;

    /// <summary>Above this multiple of the target, photon noise is almost certainly already
    /// well under typical ground-based systematics, so more signal just costs cadence.</summary>
    public const double TargetHighMult = 3.0;

    /// <summary>Max representable ADU, in the same stored/raw units as ApertureResult.Peak --
    /// derived from BITPIX alone, not from any header field like SATURATE/FULLWELL (capture
    /// software rarely writes one). Returns null for float-typed data (BITPIX &lt;= 0), where a
    /// bit-depth ceiling isn't meaningful (e.g. calibrated/stacked/normalized images) -- that's
    /// exactly the case CameraProfile.FullWellElectrons exists to cover instead.</summary>
    public static double? SaturationCeiling(FitsHeader header)
    {
        var bitpix = header.GetInt("BITPIX");
        if (bitpix is null || bitpix <= 0) return null;
        return Math.Pow(2, bitpix.Value) - 1;
    }

    public static double? GetExptime(FitsHeader header)
        => header.GetDouble("EXPTIME") ?? header.GetDouble("EXPOSURE");

    public static ExposureMeterResult Evaluate(
        ApertureResult aperture, double? peakElectrons, double? electrons,
        FitsHeader header, CameraProfile profile)
    {
        double? snr = aperture.SkySigma > 0 ? (aperture.Peak - aperture.SkyMedian) / aperture.SkySigma : null;

        double? satPct = null;
        string? satBasis = null;
        if (profile.FullWellElectrons is > 0 && peakElectrons is not null)
        {
            satPct = peakElectrons.Value / profile.FullWellElectrons.Value * 100.0;
            satBasis = "full well";
        }
        if (satPct is null)
        {
            var ceiling = SaturationCeiling(header);
            if (ceiling is not null)
            {
                satPct = aperture.Peak / ceiling.Value * 100.0;
                satBasis = "ADC ceiling";
            }
        }

        double? targetE = profile.TargetElectrons > 0 ? profile.TargetElectrons : null;
        double? exptime = GetExptime(header);

        double? SuggestedSeconds(double? ratio)
            => (exptime is not null && ratio is > 0) ? exptime.Value * ratio.Value : null;

        string SuggestText(double? ratio)
        {
            var s = SuggestedSeconds(ratio);
            return s is null ? "" : $" -- try ~{s.Value:F1}s";
        }

        if (snr is not null && snr < LowSnrThreshold)
        {
            return new ExposureMeterResult(
                ExposureMeterState.TooFaint,
                $"peak only {snr.Value:F1}x sky noise",
                "Increase exposure -- signal is barely above the noise floor.",
                null, electrons, targetE, satPct, satBasis);
        }

        if (satPct is not null && satPct >= SaturationWarnPct)
        {
            double? ratio = satPct > 0 ? 70.0 / satPct : null;
            return new ExposureMeterResult(
                ExposureMeterState.NearSaturation,
                $"{satPct.Value:F0}% of {satBasis}",
                $"Reduce exposure{SuggestText(ratio)}.",
                SuggestedSeconds(ratio), electrons, targetE, satPct, satBasis);
        }

        if (targetE is not null && electrons is not null)
        {
            if (electrons < TargetLowFrac * targetE)
            {
                double? ratio = electrons > 0 ? targetE / electrons : null;
                string satNote = satPct is not null ? $"; {satPct.Value:F0}% of {satBasis}, room to grow" : "";
                return new ExposureMeterResult(
                    ExposureMeterState.LowSignal,
                    $"{electrons.Value:N0} e- vs {targetE.Value:N0} target{satNote}",
                    $"Increase exposure{SuggestText(ratio)}.",
                    SuggestedSeconds(ratio), electrons, targetE, satPct, satBasis);
            }
            if (electrons > TargetHighMult * targetE)
            {
                double? ratio = electrons > 0 ? targetE / electrons : null;
                return new ExposureMeterResult(
                    ExposureMeterState.ExcessSignal,
                    $"{electrons.Value:N0} e- vs {targetE.Value:N0} target",
                    "Past the photon-noise floor -- shorter exposures would trade unneeded "
                        + $"precision for better cadence{SuggestText(ratio)}.",
                    SuggestedSeconds(ratio), electrons, targetE, satPct, satBasis);
            }
        }

        var bits = new System.Collections.Generic.List<string>();
        if (electrons is not null && targetE is not null) bits.Add($"{electrons.Value:N0} e- (target {targetE.Value:N0})");
        else if (electrons is not null) bits.Add($"{electrons.Value:N0} e-");
        if (satPct is not null) bits.Add($"{satPct.Value:F0}% of {satBasis}");
        if (bits.Count == 0) bits.Add("no BITPIX / gain not set");

        return new ExposureMeterResult(
            ExposureMeterState.Good,
            string.Join("; ", bits),
            "No change needed.",
            null, electrons, targetE, satPct, satBasis);
    }
}
