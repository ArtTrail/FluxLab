using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleFitsViewer.Services;

public record UpdateInfo(string Version, string ReleaseUrl);

/// <summary>
/// Checks GitHub Releases for a newer FluxLab version, mirroring the ecosystem's UpdateService
/// (StarFix / TransitLab / VariLab). Unlike those, FluxLab ships a plain zip rather than a
/// self-installing Inno Setup, so this only reports whether a newer release exists and hands back
/// the release page URL — the UI opens it in the browser for a manual download, it does NOT try to
/// download and run an installer that does not exist.
/// </summary>
public static class UpdateService
{
    private const string ApiUrl = "https://api.github.com/repos/ArtTrail/FluxLab/releases/latest";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "FluxLab-UpdateChecker" } },
    };

    /// <summary>Returns the newer release's info, or null if the check failed or we are current.
    /// <paramref name="upToDate"/> distinguishes "current" (true) from "check failed" (false) so the
    /// caller can show the right message.</summary>
    public static async Task<UpdateInfo?> CheckAsync(
        string currentVersion, CancellationToken ct = default)
    {
        var json = await Http.GetStringAsync(ApiUrl, ct);
        var root = JsonNode.Parse(json);

        var tag = root?["tag_name"]?.GetValue<string>();
        if (tag is null) throw new InvalidOperationException("No tag_name in the latest release.");

        var latestVersion = tag.TrimStart('v', 'V');
        if (!IsNewer(latestVersion, currentVersion)) return null;

        var url = root?["html_url"]?.GetValue<string>()
                  ?? $"https://github.com/ArtTrail/FluxLab/releases/tag/{tag}";
        return new UpdateInfo(latestVersion, url);
    }

    private static bool IsNewer(string latest, string current)
    {
        if (!Version.TryParse(latest, out var l)) return false;
        if (!Version.TryParse(current, out var c)) return false;
        return l > c;
    }
}
