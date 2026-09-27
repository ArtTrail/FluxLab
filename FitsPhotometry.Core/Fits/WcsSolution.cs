using System;
using System.Collections.Generic;
using System.Globalization;

namespace FitsPhotometry.Core.Fits;

/// <summary>
/// Gnomonic (TAN) world-coordinate solution with optional SIP distortion, read straight from a
/// plate-solved FITS header. Enough to turn a pixel into RA/Dec and back, which is all the viewer
/// needs (cursor readout, and placing an aperture on a resolved target's coordinates).
///
/// Deliberately narrow: TAN only. Every plate solver this app is likely to meet -- astrometry.net,
/// ASTAP, PlateSolve, StarFix -- writes `RA---TAN`/`DEC--TAN`, and both real frames checked while
/// building this did. Other projections return null from TryParse rather than silently producing
/// wrong coordinates from a TAN assumption.
///
/// Pixel coordinates here are 0-BASED array indices, matching how the rest of this codebase
/// addresses pixels. FITS CRPIX is 1-based, so the +1/-1 conversions live inside this class and
/// callers never see them.
/// </summary>
public sealed class WcsSolution
{
    private readonly double _crpix1, _crpix2;        // 1-based, as stored
    private readonly double _crval1, _crval2;        // degrees
    private readonly double _cd11, _cd12, _cd21, _cd22;
    private readonly double _inv11, _inv12, _inv21, _inv22;   // inverse of the CD matrix

    private readonly double[,]? _a, _b;              // SIP forward  (pixel -> corrected pixel)
    private readonly double[,]? _ap, _bp;            // SIP inverse  (corrected -> pixel)
    private readonly int _aOrder, _bOrder, _apOrder, _bpOrder;

    public bool HasSip => _a is not null && _b is not null;

    private WcsSolution(
        double crpix1, double crpix2, double crval1, double crval2,
        double cd11, double cd12, double cd21, double cd22,
        double[,]? a, int aOrder, double[,]? b, int bOrder,
        double[,]? ap, int apOrder, double[,]? bp, int bpOrder)
    {
        _crpix1 = crpix1; _crpix2 = crpix2;
        _crval1 = crval1; _crval2 = crval2;
        _cd11 = cd11; _cd12 = cd12; _cd21 = cd21; _cd22 = cd22;

        double det = cd11 * cd22 - cd12 * cd21;
        if (Math.Abs(det) < 1e-30) throw new ArgumentException("Degenerate CD matrix");
        _inv11 =  cd22 / det; _inv12 = -cd12 / det;
        _inv21 = -cd21 / det; _inv22 =  cd11 / det;

        _a = a; _aOrder = aOrder; _b = b; _bOrder = bOrder;
        _ap = ap; _apOrder = apOrder; _bp = bp; _bpOrder = bpOrder;
    }

    /// <summary>Returns null when the header carries no usable TAN solution.</summary>
    public static WcsSolution? TryParse(FitsHeader header)
    {
        var ctype1 = header.Get("CTYPE1")?.Trim().Trim('\'').Trim();
        var ctype2 = header.Get("CTYPE2")?.Trim().Trim('\'').Trim();
        if (ctype1 is null || ctype2 is null) return null;
        if (!ctype1.StartsWith("RA---TAN", StringComparison.OrdinalIgnoreCase) ||
            !ctype2.StartsWith("DEC--TAN", StringComparison.OrdinalIgnoreCase))
            return null;

        var crpix1 = header.GetDouble("CRPIX1");
        var crpix2 = header.GetDouble("CRPIX2");
        var crval1 = header.GetDouble("CRVAL1");
        var crval2 = header.GetDouble("CRVAL2");
        if (crpix1 is null || crpix2 is null || crval1 is null || crval2 is null) return null;

        // Linear transform: PC+CDELT takes PRECEDENCE over CD when both are present.
        //
        // That ordering is not the obvious one and was established by measurement, not assumed.
        // The FITS standard treats CDi_j and PCi_j+CDELTi as alternative representations that
        // should not both appear, but real solvers do write both -- and when they disagree, the
        // choice matters. On a real plate-solved frame here, CD1_1 = 7.4376e-5 while
        // CDELT1*PC1_1 = 7.4297e-5, a 0.106% scale difference: negligible near the reference
        // pixel but ~3.3 px out at the frame corners. Checking against astropy (i.e. WCSLIB, the
        // de facto reference) over a grid spanning the whole frame: using CD disagreed by up to
        // 986 mas, while using CDELT*PC matched to 0.00 mas everywhere. WCSLIB's altlin
        // precedence prefers PC, so this follows it.
        double cd11, cd12, cd21, cd22;
        bool hasPc = header.GetDouble("PC1_1") is not null || header.GetDouble("PC2_2") is not null
                  || header.GetDouble("PC1_2") is not null || header.GetDouble("PC2_1") is not null;

        if (hasPc)
        {
            double cdelt1 = header.GetDouble("CDELT1") ?? 1.0;   // CDELT defaults to 1 per standard
            double cdelt2 = header.GetDouble("CDELT2") ?? 1.0;
            double pc11 = header.GetDouble("PC1_1") ?? 1.0;
            double pc12 = header.GetDouble("PC1_2") ?? 0.0;
            double pc21 = header.GetDouble("PC2_1") ?? 0.0;
            double pc22 = header.GetDouble("PC2_2") ?? 1.0;
            cd11 = cdelt1 * pc11; cd12 = cdelt1 * pc12;
            cd21 = cdelt2 * pc21; cd22 = cdelt2 * pc22;
        }
        else if (header.GetDouble("CD1_1") is double c11)
        {
            cd11 = c11;
            cd12 = header.GetDouble("CD1_2") ?? 0.0;
            cd21 = header.GetDouble("CD2_1") ?? 0.0;
            cd22 = header.GetDouble("CD2_2") ?? 0.0;
        }
        else
        {
            // CDELT alone, with an implicit identity PC.
            double cdelt1 = header.GetDouble("CDELT1") ?? 0.0;
            double cdelt2 = header.GetDouble("CDELT2") ?? 0.0;
            if (cdelt1 == 0.0 || cdelt2 == 0.0) return null;
            cd11 = cdelt1; cd12 = 0.0; cd21 = 0.0; cd22 = cdelt2;
        }

        var (a, aOrder) = ReadSip(header, "A");
        var (b, bOrder) = ReadSip(header, "B");
        var (ap, apOrder) = ReadSip(header, "AP");
        var (bp, bpOrder) = ReadSip(header, "BP");

        try
        {
            return new WcsSolution(
                crpix1.Value, crpix2.Value, crval1.Value, crval2.Value,
                cd11, cd12, cd21, cd22,
                a, aOrder, b, bOrder, ap, apOrder, bp, bpOrder);
        }
        catch (ArgumentException) { return null; }
    }

    private static (double[,]? Coeffs, int Order) ReadSip(FitsHeader header, string prefix)
    {
        var orderVal = header.GetInt($"{prefix}_ORDER");
        if (orderVal is not int order || order < 1) return (null, 0);

        var c = new double[order + 1, order + 1];
        bool any = false;
        for (int p = 0; p <= order; p++)
            for (int q = 0; q <= order - p; q++)
            {
                var v = header.GetDouble($"{prefix}_{p}_{q}");
                if (v is double d && d != 0.0) { c[p, q] = d; any = true; }
            }
        return any ? (c, order) : (null, 0);
    }

    private static double Poly(double[,] c, int order, double u, double v)
    {
        double sum = 0.0;
        for (int p = 0; p <= order; p++)
            for (int q = 0; q <= order - p; q++)
            {
                double t = c[p, q];
                if (t != 0.0) sum += t * Math.Pow(u, p) * Math.Pow(v, q);
            }
        return sum;
    }

    /// <summary>0-based pixel -> (RA, Dec) in degrees.</summary>
    public (double Ra, double Dec) PixelToWorld(double x0, double y0)
    {
        // Offsets from the reference pixel, in FITS 1-based convention.
        double u = (x0 + 1.0) - _crpix1;
        double v = (y0 + 1.0) - _crpix2;

        if (_a is not null && _b is not null)
        {
            double du = Poly(_a, _aOrder, u, v);
            double dv = Poly(_b, _bOrder, u, v);
            u += du; v += dv;
        }

        // Intermediate world coordinates (degrees), then deproject.
        double xi  = (_cd11 * u + _cd12 * v) * Math.PI / 180.0;
        double eta = (_cd21 * u + _cd22 * v) * Math.PI / 180.0;

        double ra0 = _crval1 * Math.PI / 180.0;
        double dec0 = _crval2 * Math.PI / 180.0;
        double sinDec0 = Math.Sin(dec0), cosDec0 = Math.Cos(dec0);

        double d = cosDec0 - eta * sinDec0;
        double ra = ra0 + Math.Atan2(xi, d);
        double dec = Math.Atan2(sinDec0 + eta * cosDec0, Math.Sqrt(xi * xi + d * d));

        double raDeg = ra * 180.0 / Math.PI;
        raDeg %= 360.0;
        if (raDeg < 0) raDeg += 360.0;
        return (raDeg, dec * 180.0 / Math.PI);
    }

    /// <summary>(RA, Dec) in degrees -> 0-based pixel.</summary>
    public (double X, double Y) WorldToPixel(double raDeg, double decDeg)
    {
        double ra = raDeg * Math.PI / 180.0;
        double dec = decDeg * Math.PI / 180.0;
        double ra0 = _crval1 * Math.PI / 180.0;
        double dec0 = _crval2 * Math.PI / 180.0;

        double dRa = ra - ra0;
        double sinDec = Math.Sin(dec), cosDec = Math.Cos(dec);
        double sinDec0 = Math.Sin(dec0), cosDec0 = Math.Cos(dec0);
        double cosDRa = Math.Cos(dRa), sinDRa = Math.Sin(dRa);

        double denom = sinDec0 * sinDec + cosDec0 * cosDec * cosDRa;   // cos of angular distance
        if (Math.Abs(denom) < 1e-12) denom = denom < 0 ? -1e-12 : 1e-12;

        double xi  = cosDec * sinDRa / denom;
        double eta = (cosDec0 * sinDec - sinDec0 * cosDec * cosDRa) / denom;

        // radians -> degrees, then undo the CD matrix
        xi *= 180.0 / Math.PI;
        eta *= 180.0 / Math.PI;
        double u = _inv11 * xi + _inv12 * eta;
        double v = _inv21 * xi + _inv22 * eta;

        if (_ap is not null && _bp is not null)
        {
            // Header-supplied inverse coefficients: one direct evaluation.
            double du = Poly(_ap, _apOrder, u, v);
            double dv = Poly(_bp, _bpOrder, u, v);
            u += du; v += dv;
        }
        else if (_a is not null && _b is not null)
        {
            // No AP_/BP_ in the header: invert the forward SIP numerically. Converges in a few
            // iterations because the distortion is small compared with the linear term.
            double uu = u, vv = v;
            for (int i = 0; i < 20; i++)
            {
                double fu = uu + Poly(_a, _aOrder, uu, vv) - u;
                double fv = vv + Poly(_b, _bOrder, uu, vv) - v;
                if (Math.Abs(fu) < 1e-9 && Math.Abs(fv) < 1e-9) break;
                uu -= fu; vv -= fv;
            }
            u = uu; v = vv;
        }

        return ((u + _crpix1) - 1.0, (v + _crpix2) - 1.0);
    }

    /// <summary>Approximate pixel scale in arcsec/pixel, from the CD matrix determinant.</summary>
    public double PixelScaleArcsec =>
        Math.Sqrt(Math.Abs(_cd11 * _cd22 - _cd12 * _cd21)) * 3600.0;

    /// <summary>RA as sexagesimal hours, e.g. "21:04:23.31".</summary>
    public static string FormatRa(double raDeg)
    {
        double hours = raDeg / 15.0;
        int h = (int)hours;
        double remMin = (hours - h) * 60.0;
        int m = (int)remMin;
        double s = (remMin - m) * 60.0;
        if (s >= 59.995) { s = 0; m++; }
        if (m >= 60) { m = 0; h++; }
        if (h >= 24) h -= 24;
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00.00}", h, m, s);
    }

    /// <summary>Dec as sexagesimal degrees, e.g. "+24:39:11.6".</summary>
    public static string FormatDec(double decDeg)
    {
        char sign = decDeg < 0 ? '-' : '+';
        double a = Math.Abs(decDeg);
        int d = (int)a;
        double remMin = (a - d) * 60.0;
        int m = (int)remMin;
        double s = (remMin - m) * 60.0;
        if (s >= 59.95) { s = 0; m++; }
        if (m >= 60) { m = 0; d++; }
        return string.Format(CultureInfo.InvariantCulture, "{0}{1:00}:{2:00}:{3:00.0}", sign, d, m, s);
    }
}
