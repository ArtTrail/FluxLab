using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleFitsViewer.Services;

/// <summary>
/// Resolves a target name to sky coordinates by querying, in order: AAVSO VSX (variable stars),
/// then the NASA Exoplanet Archive (host star or planet name), then SIMBAD (general objects, via
/// its identifier-alias table so any known alias works, not only the primary designation).
///
/// Ported from VariLab's service of the same name rather than rewritten, so the hard-won details
/// come with it:
///
///  * VSX lives at vsx.aavso.org. The older www.aavso.org/vsx/... path 301s through a route that
///    trips Cloudflare's bot challenge for a plain HttpClient, and a User-Agent header alone does
///    not get past it.
///  * Every attempt, failure and success is written to the persistent diagnostics log, not only
///    surfaced as transient status text. VariLab learned this the hard way: when VSX silently
///    moved, every failure mode here (bad status, unexpected JSON shape, parse failure, exception)
///    was invisible after the fact, leaving a "not found" with nothing to explain it. Its source
///    carries the note "Never go back to a bare catch { return null; } here" -- that applies here
///    too.
///
/// Deliberately lives in the app rather than FitsPhotometry.Core: Core is meant to stay free of
/// network I/O so it can be referenced from other hosts without dragging HTTP along.
/// </summary>
public static class TargetResolverService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// A resolved position, tagged with the reference epoch its coordinates are quoted at and its
    /// proper motion (mu_alpha*, mu_delta in mas/yr) where the source supplies one, so the caller
    /// can propagate it to the frame's own epoch.
    ///
    /// <paramref name="EpochJyear"/> is NOT the same for every source, and getting it wrong is
    /// worse than ignoring proper motion entirely. Measured, not assumed -- two independent checks
    /// on real data:
    ///
    ///  * Catalogue differencing. For HD 189733, NEA's dec is 3.880" from SIMBAD's; at its
    ///    pmdec of -250.2 mas/yr that is exactly 15.51 yr of motion. For TOI-4479 the same
    ///    comparison gives 15.50 yr. So SIMBAD's basic.ra/dec are J2000 and NEA's pscomppars
    ///    ra/dec are J2015.5 (Gaia DR2's reference epoch), on two independent stars.
    ///  * Residual against a real frame. Mapping NEA's TOI-4479 position through a plate-solved
    ///    frame's WCS (epoch 2026.693) and comparing to the star's measured centroid:
    ///        no PM correction        6.75 px (1.81")
    ///        assume J2000 epoch      8.86 px (2.37")  <-- WORSE than doing nothing
    ///        assume J2015.5 epoch    1.94 px (0.52")  <-- matches, at the plate-solve floor
    ///
    /// VSX names its own fields RA2000/Declination2000, so J2000 is explicit there.
    /// </summary>
    public record ResolveResult(
        double Ra, double Dec, string Source, string MatchedName,
        double EpochJyear, double? PmRaMasPerYr = null, double? PmDecMasPerYr = null);

    public static async Task<ResolveResult?> ResolveAsync(
        string name, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();

        progress?.Report("Trying AAVSO VSX…");
        var vsx = await TryVsxAsync(name, ct);
        if (vsx is not null) return vsx;

        progress?.Report("Not in VSX — trying NASA Exoplanet Archive…");
        var nea = await TryNeaAsync(name, ct);
        if (nea is not null) return nea;

        progress?.Report("Not in NEA — trying SIMBAD…");
        var simbad = await TrySimbadAsync(name, ct);
        if (simbad is not null) return simbad;

        DiagnosticsLog.Log($"[TargetResolver] '{name}' not resolved by VSX, NEA, or SIMBAD.");
        return null;
    }

    private static async Task<ResolveResult?> TryVsxAsync(string name, CancellationToken ct)
    {
        string url = "https://vsx.aavso.org/index.php?view=api.object" +
                     $"&ident={Uri.EscapeDataString(name)}&format=json";
        DiagnosticsLog.Log($"[TargetResolver] VSX: requesting {url}");
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                DiagnosticsLog.Log($"[TargetResolver] VSX: '{name}' -> HTTP {(int)resp.StatusCode} {resp.StatusCode}");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("VSXObject", out var obj) ||
                obj.ValueKind != JsonValueKind.Object)
            {
                DiagnosticsLog.Log($"[TargetResolver] VSX: '{name}' -> no match (empty VSXObject). Raw: {Truncate(json)}");
                return null;
            }
            if (!obj.TryGetProperty("RA2000", out var raEl) ||
                !obj.TryGetProperty("Declination2000", out var decEl))
            {
                DiagnosticsLog.Log($"[TargetResolver] VSX: '{name}' -> matched but missing RA2000/Declination2000. Raw: {Truncate(json)}");
                return null;
            }
            if (!double.TryParse(raEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ra))
            {
                DiagnosticsLog.Log($"[TargetResolver] VSX: '{name}' -> RA2000 '{raEl.GetString()}' didn't parse as a number.");
                return null;
            }
            if (!double.TryParse(decEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var dec))
            {
                DiagnosticsLog.Log($"[TargetResolver] VSX: '{name}' -> Declination2000 '{decEl.GetString()}' didn't parse as a number.");
                return null;
            }

            string matched = obj.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? name : name;
            double? pmRa = StringNumber(obj, "ProperMotionRA");
            double? pmDec = StringNumber(obj, "ProperMotionDec");
            DiagnosticsLog.Log($"[TargetResolver] VSX: '{name}' -> resolved as '{matched}', "
                             + $"RA={ra:F6} Dec={dec:F6} (J2000), pm=({Fmt(pmRa)}, {Fmt(pmDec)}) mas/yr");
            return new ResolveResult(ra, dec, "VSX", matched, 2000.0, pmRa, pmDec);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticsLog.Log($"[TargetResolver] VSX: '{name}' -> threw {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static async Task<ResolveResult?> TryNeaAsync(string name, CancellationToken ct)
    {
        string escaped = name.Replace("'", "''");
        string adql = "SELECT pl_name,hostname,ra,dec,sy_pmra,sy_pmdec FROM pscomppars " +
                      $"WHERE hostname = '{escaped}' OR pl_name = '{escaped}'";
        string url = "https://exoplanetarchive.ipac.caltech.edu/TAP/sync?" +
                     $"query={Uri.EscapeDataString(adql)}&format=json";
        DiagnosticsLog.Log($"[TargetResolver] NEA: requesting {url}");
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                DiagnosticsLog.Log($"[TargetResolver] NEA: '{name}' -> HTTP {(int)resp.StatusCode} {resp.StatusCode}");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                DiagnosticsLog.Log($"[TargetResolver] NEA: '{name}' -> no match. Raw: {Truncate(json)}");
                return null;
            }

            var first = doc.RootElement[0];
            double ra = first.GetProperty("ra").GetDouble();
            double dec = first.GetProperty("dec").GetDouble();
            string matched = first.TryGetProperty("pl_name", out var pn) ? pn.GetString() ?? name : name;
            double? pmRa = Number(first, "sy_pmra");
            double? pmDec = Number(first, "sy_pmdec");
            DiagnosticsLog.Log($"[TargetResolver] NEA: '{name}' -> resolved as '{matched}', "
                             + $"RA={ra:F6} Dec={dec:F6} (J2015.5), pm=({Fmt(pmRa)}, {Fmt(pmDec)}) mas/yr");
            return new ResolveResult(ra, dec, "NEA", matched, 2015.5, pmRa, pmDec);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticsLog.Log($"[TargetResolver] NEA: '{name}' -> threw {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static async Task<ResolveResult?> TrySimbadAsync(string name, CancellationToken ct)
    {
        string escaped = name.Replace("'", "''");
        string adql = "SELECT basic.main_id, basic.ra, basic.dec, basic.pmra, basic.pmdec FROM basic " +
                      "JOIN ident ON ident.oidref = basic.oid " +
                      $"WHERE ident.id = '{escaped}'";
        string url = "https://simbad.u-strasbg.fr/simbad/sim-tap/sync?" +
                     $"request=doQuery&lang=adql&format=json&query={Uri.EscapeDataString(adql)}";
        DiagnosticsLog.Log($"[TargetResolver] SIMBAD: requesting {url}");
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                DiagnosticsLog.Log($"[TargetResolver] SIMBAD: '{name}' -> HTTP {(int)resp.StatusCode} {resp.StatusCode}");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            {
                DiagnosticsLog.Log($"[TargetResolver] SIMBAD: '{name}' -> no match. Raw: {Truncate(json)}");
                return null;
            }

            var row = data[0];
            string matched = row[0].GetString() ?? name;
            double ra = row[1].GetDouble();
            double dec = row[2].GetDouble();
            // TAP nulls come back as JSON null, so these are only read when actually numeric.
            double? pmRa = row.GetArrayLength() > 3 && row[3].ValueKind == JsonValueKind.Number ? row[3].GetDouble() : null;
            double? pmDec = row.GetArrayLength() > 4 && row[4].ValueKind == JsonValueKind.Number ? row[4].GetDouble() : null;
            DiagnosticsLog.Log($"[TargetResolver] SIMBAD: '{name}' -> resolved as '{matched}', "
                             + $"RA={ra:F6} Dec={dec:F6} (J2000), pm=({Fmt(pmRa)}, {Fmt(pmDec)}) mas/yr");
            return new ResolveResult(ra, dec, "SIMBAD", matched, 2000.0, pmRa, pmDec);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticsLog.Log($"[TargetResolver] SIMBAD: '{name}' -> threw {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>A numeric JSON property, or null when absent or JSON null (a TAP null column).</summary>
    private static double? Number(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : null;

    /// <summary>A numeric value delivered as a JSON *string* -- VSX quotes every field, including
    /// its numbers -- or null when absent, empty, or unparseable.</summary>
    private static double? StringNumber(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
           && double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? d : null;

    private static string Fmt(double? v) => v is null ? "n/a" : v.Value.ToString("F3", CultureInfo.InvariantCulture);

    private static string Truncate(string s, int max = 300) => s.Length <= max ? s : s[..max] + "…";
}
