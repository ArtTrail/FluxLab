using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FitsPhotometry.Core.Camera;

/// <summary>
/// A user-named, saved camera setup (e.g. "ASI183MM Pro - gain 111") -- deliberately simpler
/// than CameraProfile, which also tracks per-session resolution provenance (GainSource,
/// AduScaleSource) that doesn't make sense for a saved library entry; a saved profile is just
/// the values themselves. Lets the user pick a camera from a list instead of retyping ADU scale
/// and Full well every time, complementing (not replacing) the automatic "remember what I used
/// last" persistence CameraProfile already provides.
/// </summary>
public sealed class NamedCameraProfile
{
    public string Name { get; set; } = "";
    public double? GainEPerAdu { get; set; }
    public double? AduScale { get; set; }
    public double? FullWellElectrons { get; set; }
    public double TargetElectrons { get; set; } = 100_000;
}

public static class CameraProfileLibrary
{
    public static List<NamedCameraProfile> LoadFromJson(string path)
    {
        if (!File.Exists(path)) return [];
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<NamedCameraProfile>>(json) ?? [];
    }

    public static void SaveToJson(string path, List<NamedCameraProfile> profiles)
    {
        var json = JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
