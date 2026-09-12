using System.IO;
using System.Text.Json;

namespace FitsPhotometry.Core.Camera;

/// <summary>
/// The small set of camera/target-specific values the exposure meter needs, kept separate from
/// general app settings so it's shareable between the desktop app and (eventually) a NINA
/// plugin -- calibrate once per camera/gain combination, save, and any consumer can load the
/// same file. Mirrors the Gain/ADU-scale/Full-well/Target-electron fields from the Python app's
/// Aperture Photometry panel.
/// </summary>
public sealed class CameraProfile
{
    /// <summary>Electrons collected per ADU count. Usually auto-filled from the FITS header's
    /// EGAIN keyword; null if never resolved and never typed in.</summary>
    public double? GainEPerAdu { get; set; }

    /// <summary>How the current GainEPerAdu value was obtained: "FITS header (EGAIN)", "manual
    /// entry", or "" if unset.</summary>
    public string GainSource { get; set; } = "";

    /// <summary>Left-shift divisor recovering a sensor's native ADC reading from a wider stored
    /// container (e.g. 16 for a 12-bit ADC packed into 16-bit). Null/unset on a fresh profile,
    /// or "unverified" pending confirmation on a calibrated file -- see CameraProfileResolver.</summary>
    public double? AduScale { get; set; }

    /// <summary>"auto-detected", "manual entry", or "unverified (calibrated -- check a raw
    /// frame)".</summary>
    public string AduScaleSource { get; set; } = "";

    /// <summary>Optional. Per-pixel full-well capacity in electrons at this gain setting. Not
    /// derivable from a FITS header or any camera database -- supplied once by the user. Enables
    /// a true saturation check in electrons that works on calibrated/float files too.</summary>
    public double? FullWellElectrons { get; set; }

    /// <summary>Total aperture-summed electron count being aimed for per exposure. Default
    /// 100,000 -- the point past which photon noise from the target star is usually no longer
    /// the dominant error source in ground-based differential photometry.</summary>
    public double TargetElectrons { get; set; } = 100_000;

    public static CameraProfile LoadFromJson(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<CameraProfile>(json) ?? new CameraProfile();
    }

    public void SaveToJson(string path)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
