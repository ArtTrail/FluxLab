using System;
using System.IO;

namespace SimpleFitsViewer.Services;

/// <summary>
/// Decides whether to show the first-launch "What's New" popup. Remembers the last version whose
/// notes the user dismissed in <c>%AppData%\SimpleFitsViewer\whatsnew_seen.txt</c>, so the popup
/// appears once per new version and never again for that version. Matches the ecosystem apps
/// (TransitLab/VariLab/TransitFinder). Automatic for every future version -- nothing to wire up per
/// release beyond the RevisionHistoryData entry.
/// </summary>
public static class WhatsNewService
{
    private static string SeenFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SimpleFitsViewer", "whatsnew_seen.txt");

    /// <summary>True when the current version's notes have not yet been dismissed.</summary>
    public static bool ShouldShow()
    {
        try
        {
            return !File.Exists(SeenFile)
                || File.ReadAllText(SeenFile).Trim() != AppVersion.Version;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.LogException("Checking What's New state", ex);
            return false;   // if we can't tell, don't nag
        }
    }

    /// <summary>Records that the current version's notes have been seen.</summary>
    public static void MarkSeen()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SeenFile)!);
            File.WriteAllText(SeenFile, AppVersion.Version);
        }
        catch (Exception ex) { DiagnosticsLog.LogException("Saving What's New state", ex); }
    }
}
