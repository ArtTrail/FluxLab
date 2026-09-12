using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SimpleFitsViewer.Services;

/// <summary>
/// Finds an installed StarFix's plate solver and Gaia catalog so FluxLab can drive them without
/// bundling either (the catalog alone is multi-GB). Everything is a best-effort probe of the
/// standard StarFix install layout, honouring explicit overrides from <see cref="SolverConfig"/>
/// first.
///
/// StarFix layout, established from its own source:
///   solver exe  -> &lt;StarFix install&gt;\PySolver\solve\solve.exe  (SolverRuntimeService.cs)
///   install dir -> %LOCALAPPDATA%\StarFix  (per-user install; the installer's odd AppData default)
///   catalog     -> StarFix's config.json "GaiaCatalogPath", default %APPDATA%\StarFix\gaia_catalog
///   catalog env -> the solver reads STARFIX_GAIA_CATALOG_DIR (gaia_catalog_lookup.py)
/// </summary>
public static class SolverLocatorService
{
    private static string ExeName => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "solve.exe" : "solve";

    public record Location(string? SolverExe, string? CatalogDir)
    {
        public bool SolverFound => SolverExe is not null && File.Exists(SolverExe);
        // A catalog dir is only usable if it actually holds the per-HEALPix-pixel .npz shards.
        public bool CatalogFound => CatalogDir is not null && Directory.Exists(CatalogDir)
            && Directory.EnumerateFiles(CatalogDir, "pixel_*.npz").Any();
        public bool Ready => SolverFound && CatalogFound;
    }

    public static Location Locate(SolverConfig cfg)
        => new(FindSolverExe(cfg), FindCatalogDir(cfg));

    private static string? FindSolverExe(SolverConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.SolverExePath) && File.Exists(cfg.SolverExePath))
            return cfg.SolverExePath;

        foreach (var baseDir in CandidateInstallDirs())
        {
            var p = Path.Combine(baseDir, "PySolver", "solve", ExeName);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static System.Collections.Generic.IEnumerable<string> CandidateInstallDirs()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StarFix");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "StarFix");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "/Applications/StarFix.app/Contents/MacOS";
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                      "Applications", "StarFix.app", "Contents", "MacOS");
        }
        else
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "StarFix");
            yield return "/opt/StarFix";
        }
    }

    private static string? FindCatalogDir(SolverConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.CatalogDir) && Directory.Exists(cfg.CatalogDir))
            return cfg.CatalogDir;

        // Prefer StarFix's own recorded catalog path -- the user may have installed it off the
        // default drive, and StarFix persists wherever it actually landed.
        var fromStarFixCfg = ReadStarFixCatalogPath();
        if (fromStarFixCfg is not null && Directory.Exists(fromStarFixCfg)) return fromStarFixCfg;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var def = Path.Combine(appData, "StarFix", "gaia_catalog");
        return Directory.Exists(def) ? def : null;
    }

    private static string? ReadStarFixCatalogPath()
    {
        try
        {
            var cfgPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StarFix", "config.json");
            if (!File.Exists(cfgPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
            if (doc.RootElement.TryGetProperty("GaiaCatalogPath", out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
        }
        catch (Exception ex) { DiagnosticsLog.LogException("Reading StarFix config for catalog path", ex); }
        return null;
    }
}
