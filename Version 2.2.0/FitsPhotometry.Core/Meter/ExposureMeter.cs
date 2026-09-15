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

    /// <summary>peak-above-sky / sky-sigma considered "too faint". AAVSO's CCD/CMOS photometry
    /// guidance calls SNR 10-20 "marginal" and below 10 "poor", so this sits inside the marginal
    /// band rather than only firing once the star is already in the poor range.</summary>
    public const double LowSnrThreshold = 15.0;

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

        // ── Saturated (peak clipped) ─────────────────────────────────────────────────────────
        // Checked FIRST: once the peak pixel hits the ADC ceiling the sensor has clamped it, so
        // `satPct` is stuck at ~100% no matter how far over the true peak is -- the old
        // `70/satPct` suggestion then collapses to ~0.7x per pass and the user has to iterate many
        // times. The true peak is unrecoverable from this frame, but the TOTAL aperture flux is a
        // strong lower bound on the over-exposure (only the few core pixels clip; the wings are
        // intact), so we base an aggressive one-step cut on reaching the electron target and tell
        // the user to re-measure the shorter (unsaturated) frame where the calc becomes exact.
        var adcCeiling = SaturationCeiling(header);
        bool clipped = adcCeiling is double ceil && aperture.Peak >= 0.995 * ceil;
        if (clipped)
        {
            if (electrons is double totE && targetE is double tgt && totE > 0)
            {
                double ratio = tgt / totE;                       // reduce toward the electron target
                double over = totE / tgt;
                return new ExposureMeterResult(
                    ExposureMeterState.Saturated,
                    "peak clipped at ADC max -- true peak unknown",
                    $"Saturated: total is ~{over:F0}x your target (a lower bound). Reduce hard"
                        + $"{SuggestText(ratio)} and re-measure to fine-tune.",
                    SuggestedSeconds(ratio), electrons, targetE, satPct, satBasis);
            }
            return new ExposureMeterResult(
                ExposureMeterState.Saturated,
                "peak clipped at ADC max -- true peak unknown",
                "Saturated: the peak is clipped, so the true over-exposure can't be measured. "
                    + "Reduce the exposure substantially and re-measure.",
                null, electrons, targetE, satPct, satBasis);
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
            // Linear scale on the part of the peak that actually varies with exposure: (peak - sky).
            // The raw peak includes the bias/sky pedestal, which does not scale, so scaling the whole
            // peak understates the reduction. sky is taken in the same units as the basis via the
            // measured peak/sky ratio (electron conversion is linear, so the ratio is unit-free).
            const double target = 70.0;                          // aim the peak at 70% of the basis
            double skyFrac = aperture.Peak > 0 ? satPct.Value * aperture.SkyMedian / aperture.Peak : 0.0;
            double denom = satPct.Value - skyFrac;               // signal% above the sky/bias floor
            double? ratio = denom > 0 ? (target - skyFrac) / denom : (satPct.Value > 0 ? 70.0 / satPct.Value : null);
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
