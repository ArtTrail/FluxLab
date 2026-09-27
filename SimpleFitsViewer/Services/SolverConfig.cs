using System;
using System.IO;
using System.Text.Json;

namespace SimpleFitsViewer.Services;

/// <summary>
/// Persisted overrides for locating StarFix's plate solver. Both null by default, meaning
/// SolverLocatorService auto-discovers an installed StarFix; a user who installed StarFix somewhere
/// unusual (or keeps the catalog on another drive) can pin explicit paths via Tools > Plate Solver.
///
/// Stored next to the camera profiles in %AppData%\SimpleFitsViewer\ so it survives upgrades and
/// rebuilds, same reasoning as the profile store.
/// </summary>
public sealed class SolverConfig
{
    public string? SolverExePath { get; set; }
    public string? CatalogDir { get; set; }

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimpleFitsViewer");
    private static string FilePath => Path.Combine(Dir, "solver_config.json");

    public static SolverConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<SolverConfig>(File.ReadAllText(FilePath)) ?? new SolverConfig();
        }
        catch (Exception ex) { DiagnosticsLog.LogException("Loading solver config", ex); }
        return new SolverConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { DiagnosticsLog.LogException("Saving solver config", ex); }
    }
}
