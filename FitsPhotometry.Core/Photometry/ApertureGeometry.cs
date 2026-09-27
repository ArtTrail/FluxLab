namespace FitsPhotometry.Core.Photometry;

/// <summary>Circular aperture + sky annulus, in image pixel coordinates.</summary>
public sealed record ApertureGeometry(double CenterX, double CenterY, double Radius, double AnnulusInner, double AnnulusOuter)
{
    /// <summary>Keeps annulus geometry sane relative to the aperture radius, matching the
    /// Python app's auto-correction in _compute_photometry.</summary>
    public ApertureGeometry Normalized()
    {
        var rIn = AnnulusInner;
        var rOut = AnnulusOuter;
        if (rIn <= Radius) rIn = Radius + 2.0;
        if (rOut <= rIn) rOut = rIn + 8.0;
        return new ApertureGeometry(CenterX, CenterY, Radius, rIn, rOut);
    }
}
