using System;
using System.Globalization;

namespace FitsPhotometry.Core.Fits;

/// <summary>
/// Small astrometric helpers for turning a catalogue position into a position on *this* frame:
/// proper-motion propagation, and reading the frame's observation epoch out of its header.
/// </summary>
public static class Astrometry
{
    /// <summary>
    /// Propagates (ra, dec) from <paramref name="fromEpochJyear"/> to
    /// <paramref name="toEpochJyear"/> using proper motion in mas/yr.
    ///
    /// <paramref name="pmRaMasPerYr"/> is mu_alpha* -- i.e. it already includes the cos(dec)
    /// factor, which is the convention every source this app queries uses (verified against Gaia
    /// DR3 for RR Lyr: VSX reports -109.577 mas/yr where Gaia's mu_alpha* is -109.562; a
    /// mu_alpha-without-cos-dec value would have been ~1.36x larger at that declination). So the
    /// RA increment divides by cos(dec) to convert the great-circle rate back into a coordinate
    /// increment.
    ///
    /// A plain linear (not rigorous space-motion) propagation: over the few decades between any
    /// catalogue epoch and a present-day frame, the radial-velocity and perspective terms are far
    /// below the ~0.5" plate-solve zero-point floor measured on real frames. Near the pole the
    /// 1/cos(dec) term does blow up, so it is floored -- an unpropagated position is a better
    /// answer there than a wildly wrong one.
    /// </summary>
    public static (double Ra, double Dec) ApplyProperMotion(
        double ra, double dec, double pmRaMasPerYr, double pmDecMasPerYr,
        double fromEpochJyear, double toEpochJyear)
    {
        double dt = toEpochJyear - fromEpochJyear;
        if (dt == 0.0) return (ra, dec);

        const double MasToDeg = 1.0 / 3600.0 / 1000.0;
        double cosDec = Math.Cos(dec * Math.PI / 180.0);
        if (Math.Abs(cosDec) < 1e-4) cosDec = 1e-4;   // within ~0.006 deg of a pole

        double newDec = dec + pmDecMasPerYr * MasToDeg * dt;
        double newRa = ra + pmRaMasPerYr * MasToDeg * dt / cosDec;

        // Keep RA in [0, 360) so a star propagating across 0h stays usable.
        newRa %= 360.0;
        if (newRa < 0) newRa += 360.0;
        return (newRa, newDec);
    }

    /// <summary>
    /// The frame's observation epoch as a Julian year (e.g. 2026.6933), from DATE-OBS, or null if
    /// the header carries no parseable date. Falls back to DATE-AVG then DATE, which some capture
    /// software writes instead.
    ///
    /// Uses the Julian year (365.25 d) rather than the calendar year, matching the "J2000.0" /
    /// "J2015.5" convention catalogue epochs are quoted in -- mixing the two would introduce a
    /// small but needless error.
    /// </summary>
    public static double? TryGetObservationEpochJyear(FitsHeader header)
    {
        foreach (var key in new[] { "DATE-OBS", "DATE-AVG", "DATE" })
        {
            string raw = header.Get(key).Trim();
            if (raw.Length == 0) continue;

            // FITS dates are ISO-8601 and always UTC -- never local -- so parse them as such
            // rather than letting the machine's time zone shift the epoch.
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                                  DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                  out var dt))
                return JulianYearOf(dt);
        }
        return null;
    }

    /// <summary>Julian year of a UTC instant: J2000.0 is 2000-01-01T12:00:00 UTC, and a Julian
    /// year is exactly 365.25 days.</summary>
    public static double JulianYearOf(DateTime utc)
    {
        var j2000 = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        return 2000.0 + (utc.ToUniversalTime() - j2000).TotalDays / 365.25;
    }
}
