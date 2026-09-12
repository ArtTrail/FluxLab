using FitsPhotometry.Core.Fits;

namespace FitsPhotometry.Core.Tests;

/// <summary>
/// Ground truth here is real catalogue data, not hand-built numbers. NEA's pscomppars positions and
/// SIMBAD's basic positions are the *same stars* at two different reference epochs (J2015.5 and
/// J2000 respectively, established by differencing them against their own proper motions), so
/// propagating SIMBAD's position forward 15.5 yr must land on NEA's. That makes these tests a check
/// on the real convention -- including that pmra is mu_alpha* -- rather than on my own arithmetic.
/// </summary>
public class AstrometryTests
{
    // Live queries, this session.
    //   SIMBAD basic (J2000):  main_id, ra, dec, pmra, pmdec
    //   NEA pscomppars (J2015.5): hostname, ra, dec, sy_pmra, sy_pmdec
    // pm values differ slightly between the two services (different source catalogues); each row
    // uses SIMBAD's, since that's the position being propagated.
    public static TheoryData<string, double, double, double, double, double, double> Stars => new()
    {
        //          name         simbadRa            simbadDec          pmra      pmdec      neaRa         neaDec
        { "HD 189733", 300.18213726402,   22.71085365096,  -3.208,  -250.323, 300.1821223, 22.7097759 },
        { "TOI-4479",  316.09650756763165, 24.654195186681115, 91.023, -121.080, 316.0969388, 24.6536739 },
    };

    [Theory]
    [MemberData(nameof(Stars))]
    public void PropagatingSimbadJ2000ForwardLandsOnNeasJ2015_5Position(
        string name, double simbadRa, double simbadDec, double pmRa, double pmDec,
        double neaRa, double neaDec)
    {
        var (ra, dec) = Astrometry.ApplyProperMotion(simbadRa, simbadDec, pmRa, pmDec, 2000.0, 2015.5);

        // Agreement to 20 mas. The residual is real (the two services draw proper motion from
        // different reductions -- SIMBAD has HD 189733 at pmdec -250.323, NEA at -250.225, a
        // 0.098 mas/yr difference that alone accounts for ~1.5 mas over 15.5 yr), and 20 mas is
        // still ~13x tighter than the 0.27"/px scale of the frames this feature runs on.
        const double TolDeg = 0.020 / 3600.0;
        Assert.True(Math.Abs(dec - neaDec) < TolDeg,
            $"{name}: dec off by {(dec - neaDec) * 3600_000:F1} mas");
        Assert.True(Math.Abs(ra - neaRa) * Math.Cos(neaDec * Math.PI / 180.0) < TolDeg,
            $"{name}: ra off by {(ra - neaRa) * 3600_000 * Math.Cos(neaDec * Math.PI / 180.0):F1} mas");
    }

    /// <summary>
    /// The direction that actually matters at runtime: a catalogue position propagated to the
    /// frame's epoch. Verified independently against the real frame -- NEA's TOI-4479 position
    /// propagated from J2015.5 to the frame's 2026.6933 lands 1.94 px from the star's measured
    /// centroid, versus 6.75 px uncorrected, on a 0.2675"/px frame.
    /// </summary>
    [Fact]
    public void PropagatesNeaPositionToAFrameEpoch()
    {
        var (ra, dec) = Astrometry.ApplyProperMotion(
            316.0969388, 24.6536739, 90.926, -121.112, 2015.5, 2026.6933);

        // 11.1933 yr of motion: dec moves pmdec*dt, ra moves pmra*dt/cos(dec) -- where cos(dec) is
        // taken at the STARTING declination, the standard convention for a linear propagation.
        // (Using the propagated declination instead shifts RA by 3.4 microarcsec here, which is
        // physically meaningless but enough to fail an assertion at this precision.)
        double dt = 2026.6933 - 2015.5;
        Assert.Equal(24.6536739 + -121.112 / 3.6e6 * dt, dec, 10);
        Assert.Equal(316.0969388 + 90.926 / 3.6e6 * dt / Math.Cos(24.6536739 * Math.PI / 180.0), ra, 10);

        // And it must be a real move, not a no-op: ~1.36" of declination over 11 yr.
        Assert.InRange((24.6536739 - dec) * 3600.0, 1.3, 1.4);
    }

    [Fact]
    public void ZeroElapsedTimeIsANoOp()
    {
        var (ra, dec) = Astrometry.ApplyProperMotion(123.456, -45.678, 500.0, -900.0, 2000.0, 2000.0);
        Assert.Equal(123.456, ra);
        Assert.Equal(-45.678, dec);
    }

    [Fact]
    public void WrapsRaAcrossZeroHours()
    {
        // Just west of 0h with eastward motion -- must come out just east of 0h, not at 360.0001.
        var (ra, _) = Astrometry.ApplyProperMotion(359.99999, 0.0, 1_000_000.0, 0.0, 2000.0, 2010.0);
        Assert.InRange(ra, 0.0, 10.0);
    }

    [Fact]
    public void DoesNotBlowUpAtThePole()
    {
        // cos(dec) -> 0 would send the RA increment to infinity; it's floored instead.
        var (ra, dec) = Astrometry.ApplyProperMotion(10.0, 89.99999, 1000.0, 0.0, 2000.0, 2026.0);
        Assert.True(double.IsFinite(ra));
        Assert.True(double.IsFinite(dec));
        Assert.InRange(ra, 0.0, 360.0);
    }

    [Fact]
    public void ReadsObservationEpochFromDateObs()
    {
        var h = new FitsHeader(new List<FitsCard>
        {
            new() { Keyword = "DATE-OBS", Value = "2026-09-11T05:39:06.524018", Comment = "" },
        });
        // The real frame's DATE-OBS; astropy's Time(...).jyear for it is 2026.6933.
        Assert.Equal(2026.6933, Astrometry.TryGetObservationEpochJyear(h)!.Value, 4);
    }

    [Fact]
    public void ReadsObservationEpochAsUtc_NotLocalTime()
    {
        // A naive parse would shift this by the machine's UTC offset. On a -6h machine that is
        // 0.00068 yr -- only ~0.02 mas for a fast star, but the test pins the intent, and it makes
        // the result machine-independent.
        var h = new FitsHeader(new List<FitsCard>
        {
            new() { Keyword = "DATE-OBS", Value = "2000-01-01T12:00:00", Comment = "" },
        });
        Assert.Equal(2000.0, Astrometry.TryGetObservationEpochJyear(h)!.Value, 9);
    }

    [Fact]
    public void FallsBackThroughDateAvgAndDate_AndReturnsNullWhenAbsent()
    {
        var avg = new FitsHeader(new List<FitsCard>
        {
            new() { Keyword = "DATE-AVG", Value = "2020-07-01T00:00:00", Comment = "" },
        });
        Assert.NotNull(Astrometry.TryGetObservationEpochJyear(avg));

        var date = new FitsHeader(new List<FitsCard>
        {
            new() { Keyword = "DATE", Value = "2020-07-01", Comment = "" },
        });
        Assert.NotNull(Astrometry.TryGetObservationEpochJyear(date));

        Assert.Null(Astrometry.TryGetObservationEpochJyear(
            new FitsHeader(new List<FitsCard> { new() { Keyword = "BITPIX", Value = "16", Comment = "" } })));

        // Present but unparseable must be null, not an exception or a garbage epoch.
        Assert.Null(Astrometry.TryGetObservationEpochJyear(
            new FitsHeader(new List<FitsCard> { new() { Keyword = "DATE-OBS", Value = "not a date", Comment = "" } })));
    }
}
