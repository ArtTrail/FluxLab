namespace SimpleFitsViewer;

/// <summary>
/// Single source of truth for the displayed version, mirroring StarFix's <c>AppVersion</c> and
/// VariLab's <c>AppVersion</c> (TransitLab calls it <c>AppInfo</c>). Everything user-facing that
/// shows a version reads from here so the strings can never drift apart.
///
/// Note the app's *display name* diverged from its internal identifiers at v2.2.0: the product is
/// "FluxLab", but the .NET assembly, root namespace, and %AppData% profile folder stay
/// "SimpleFitsViewer" (renaming the last would strand users' saved camera profiles).
/// </summary>
public static class AppVersion
{
    public const string Name = "FluxLab";
    public const string Version = "2.2.1";
    public const string Tagline = "Aperture photometry & exposure metering for FITS images";
}
