using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleFitsViewer.Services;

/// <summary>
/// Drives StarFix's frozen plate solver (solve.exe) on a single file and parses its --json result.
/// FluxLab shells out exactly the way StarFix's own GUI does -- same exe, same JSON envelope, same
/// STARFIX_GAIA_CATALOG_DIR env var -- so it inherits StarFix's solving behaviour without
/// re-implementing any of it.
///
/// The solver writes the WCS into the FITS file in place (ASTAP-style) unless --dry-run is given;
/// FluxLab solves in place, so the caller just re-reads the file's header afterwards.
/// </summary>
public static class PlateSolveService
{
    public record SolveResult(
        bool Ok, double? CenterRa, double? CenterDec, double? PixelScaleArcsec,
        int NumMatched, double? RmsArcsec, string Message);

    /// <param name="raHint">/decHint: degrees. Pass null to let the solver read RA/DEC from the
    /// header itself (its default). Only supply them when the header has no usable position.</param>
    public static async Task<SolveResult> SolveAsync(
        string filePath, double? raHint, double? decHint,
        string solverExe, string catalogDir, double radiusDeg, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = solverExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(filePath);
        if (raHint is { } ra && decHint is { } dec)
        {
            psi.ArgumentList.Add("--ra"); psi.ArgumentList.Add(ra.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--dec"); psi.ArgumentList.Add(dec.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-r"); psi.ArgumentList.Add(radiusDeg.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--json");
        psi.EnvironmentVariables["STARFIX_GAIA_CATALOG_DIR"] = catalogDir;

        DiagnosticsLog.Log($"[PlateSolve] {solverExe} \"{filePath}\" "
                         + $"{(raHint is { } r ? $"--ra {r:F5} --dec {decHint:F5} " : "(header RA/DEC) ")}"
                         + $"-r {radiusDeg} --json   [catalog: {catalogDir}]");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        try
        {
            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            if (!proc.Start())
                return new SolveResult(false, null, null, null, 0, null, "Could not start the solver process.");

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                DiagnosticsLog.Log("[PlateSolve] cancelled by user.");
                return new SolveResult(false, null, null, null, 0, null, "Cancelled.");
            }

            string outText = stdout.ToString();
            string errText = stderr.ToString().Trim();

            if (proc.ExitCode != 0)
            {
                DiagnosticsLog.Log($"[PlateSolve] exit {proc.ExitCode}. stderr: {errText}");
                return new SolveResult(false, null, null, null, 0, null,
                    MapError(errText, proc.ExitCode));
            }

            return ParseJson(outText, errText);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.LogException("Running plate solver", ex);
            return new SolveResult(false, null, null, null, 0, null, $"Solver error: {ex.Message}");
        }
    }

    private static SolveResult ParseJson(string stdout, string stderrForContext)
    {
        // The solver prints one JSON line; be tolerant of any leading progress text by scanning for
        // the last line that parses as an object with a "summary".
        string? jsonLine = null;
        foreach (var line in stdout.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("{") && t.Contains("\"summary\"")) jsonLine = t;
        }
        if (jsonLine is null)
        {
            DiagnosticsLog.Log($"[PlateSolve] no JSON in solver output. stdout: {Trunc(stdout)}  stderr: {Trunc(stderrForContext)}");
            return new SolveResult(false, null, null, null, 0, null,
                "Solver returned no result. See Tools > Diagnostics for details.");
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            var summary = root.GetProperty("summary");
            double ra = summary.GetProperty("center_ra_deg").GetDouble();
            double dec = summary.GetProperty("center_dec_deg").GetDouble();
            double scale = summary.TryGetProperty("pixel_scale_arcsec", out var ps) ? ps.GetDouble() : 0;
            double rms = summary.TryGetProperty("rms_arcsec", out var ra2) ? ra2.GetDouble() : 0;
            int matched = root.TryGetProperty("num_matched", out var nm) ? nm.GetInt32() : 0;

            DiagnosticsLog.Log($"[PlateSolve] solved: center {ra:F5},{dec:F5}  scale {scale:F3}\"/px  "
                             + $"matched {matched}  RMS {rms:F3}\"");
            return new SolveResult(true, ra, dec, scale, matched, rms,
                $"Solved: {matched} stars matched, RMS {rms:F2}\", scale {scale:F3}\"/px.");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.LogException("Parsing solver JSON", ex);
            return new SolveResult(false, null, null, null, 0, null, $"Could not parse solver result: {ex.Message}");
        }
    }

    private static string MapError(string stderr, int exitCode)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return $"Solver failed (exit code {exitCode}). See Tools > Diagnostics.";
        // Surface the solver's own first line -- it already writes readable messages (e.g. the
        // no-RA/DEC-hint case, or a catalog-not-found case).
        var first = stderr.Split('\n')[0].Trim();
        return first.Length > 0 ? first : $"Solver failed (exit code {exitCode}).";
    }

    private static string Trunc(string s, int max = 400) => s.Length <= max ? s : s[..max] + "…";
}
