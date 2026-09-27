using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleFitsViewer.Services;

/// <summary>
/// Posts a feedback submission to the shared Cloudflare Worker (the same app-feedback Worker
/// StarFix/TransitLab/VariLab use), which creates the GitHub Issue on FluxLab's behalf. The real
/// GitHub token lives only in that Worker's Cloudflare secret store -- never in this source, never
/// in the shipped app. FluxLab was added to the Worker's REPO_ALLOWLIST for this.
///
/// ClientToken below is NOT a real secret -- it ships in the binary like any string and only filters
/// casual discovery of the Worker URL. Worst case if it leaks: someone spams Issues on an
/// allowlisted repo (the Worker only accepts pre-approved repo names); the real GitHub token never
/// leaves the Worker. Embedding a real token here does not work anyway -- GitHub's secret scanning
/// auto-revokes it on push (confirmed during StarFix's development), which is why the Worker exists.
/// </summary>
public static class BugReportService
{
    private const string WorkerUrl = "https://app-feedback.mobs-sync-trigger.workers.dev";
    private const string RepoName = "FluxLab";
    private const string ClientToken = "vX0lTWqUQeQRkAgGna3Ux9ufCXtN816nKuvxPOlGPIM";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task SubmitAsync(
        string type, string summary, string description,
        string email, string version, string os,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { repo = RepoName, type, summary, description, email, version, os });

        var req = new HttpRequestMessage(HttpMethod.Post, WorkerUrl)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Client-Token", ClientToken);

        var response = await Http.SendAsync(req, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            string reason;
            try
            {
                using var doc = JsonDocument.Parse(body);
                reason = doc.RootElement.TryGetProperty("error", out var errProp) ? errProp.GetString() ?? body : body;
            }
            catch (JsonException) { reason = body; }
            throw new HttpRequestException(reason);
        }
    }
}
