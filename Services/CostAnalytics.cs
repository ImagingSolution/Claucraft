using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Claucraft.Services;

/// <summary>Token counts and estimated USD cost for one bucket of usage (a day, model, project, or session).</summary>
public sealed class TokenTotals
{
    public long Input;
    public long Output;
    public long CacheRead;
    public long CacheCreation;
    public double CostUsd;
    public long Total => Input + Output + CacheRead + CacheCreation;

    internal void Add(long input, long output, long cacheRead, long cacheCreation, double costUsd)
    {
        Input += input;
        Output += output;
        CacheRead += cacheRead;
        CacheCreation += cacheCreation;
        CostUsd += costUsd;
    }
}

/// <summary>One row in a breakdown list (a single day, model, project, or session).</summary>
public sealed class CostBucket
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public TokenTotals Totals { get; init; } = new();
}

/// <summary>Result of aggregating Claude Code session usage over a time window.</summary>
public sealed class CostReport
{
    public TokenTotals Grand { get; init; } = new();
    public List<CostBucket> ByDay { get; init; } = new();     // ascending (oldest -> newest)
    public List<CostBucket> ByModel { get; init; } = new();   // descending by cost
    public List<CostBucket> ByProject { get; init; } = new(); // descending by cost
    public List<CostBucket> BySession { get; init; } = new(); // descending by cost, top 50
    public int Days { get; init; }
    public bool ProjectScoped { get; init; }
}

/// <summary>
/// Aggregates token usage and estimated cost from Claude Code session transcripts
/// (~/.claude/projects/*/*.jsonl). Read-only: transcripts are never modified.
/// </summary>
public static class CostAnalytics
{
    /// <summary>
    /// Approximate per-model list pricing in USD per 1M tokens, based on publicly posted
    /// rates as of 2026. Matched against the model name by case-insensitive substring, so
    /// e.g. "claude-opus-4-7" and "claude-opus-5" both match the "opus" row. This is an
    /// estimate for dashboard purposes, not a billing-accurate figure.
    /// </summary>
    private static readonly (string Match, double InputPerMTok, double OutputPerMTok)[] PriceTable =
    {
        ("opus", 15.0, 75.0),
        ("sonnet", 3.0, 15.0),
        ("haiku", 0.80, 4.0),
    };

    /// <summary>Fallback pricing row (Sonnet rates) used for models not found in <see cref="PriceTable"/>.</summary>
    private static readonly (double InputPerMTok, double OutputPerMTok) FallbackPrice = (3.0, 15.0);

    /// <summary>cache_read_input_tokens are billed at this fraction of the input-token rate.</summary>
    private const double CacheReadMultiplier = 0.1;

    /// <summary>cache_creation_input_tokens are billed at this multiple of the input-token rate.</summary>
    private const double CacheCreationMultiplier = 1.25;

    /// <summary>Lines longer than this are treated as corrupt/pathological and skipped without parsing.</summary>
    private const int MaxLineChars = 20_000_000;

    /// <summary>Sessions kept in <see cref="CostReport.BySession"/>, highest cost first.</summary>
    private const int MaxSessionRows = 50;

    /// <summary>
    /// Estimate the USD cost of one usage sample for a given model name. Matches
    /// <paramref name="model"/> against <see cref="PriceTable"/> by case-insensitive substring.
    /// A model name of exactly "unknown" (case-insensitive) costs 0; any other unmatched model
    /// falls back to Sonnet pricing.
    /// </summary>
    public static double EstimateCostUsd(string model, long input, long output, long cacheRead, long cacheCreation)
    {
        if (string.IsNullOrEmpty(model))
            return Compute(FallbackPrice.InputPerMTok, FallbackPrice.OutputPerMTok, input, output, cacheRead, cacheCreation);

        if (string.Equals(model, "unknown", StringComparison.OrdinalIgnoreCase))
            return 0.0;

        foreach (var (match, inPrice, outPrice) in PriceTable)
        {
            if (model.Contains(match, StringComparison.OrdinalIgnoreCase))
                return Compute(inPrice, outPrice, input, output, cacheRead, cacheCreation);
        }

        return Compute(FallbackPrice.InputPerMTok, FallbackPrice.OutputPerMTok, input, output, cacheRead, cacheCreation);
    }

    private static double Compute(double inPrice, double outPrice, long input, long output, long cacheRead, long cacheCreation)
    {
        const double perTok = 1.0 / 1_000_000.0;
        return input * perTok * inPrice
             + output * perTok * outPrice
             + cacheRead * perTok * (inPrice * CacheReadMultiplier)
             + cacheCreation * perTok * (inPrice * CacheCreationMultiplier);
    }

    /// <summary>
    /// Build a cost/usage report covering the last <paramref name="days"/> days (today inclusive).
    /// If <paramref name="projectFolder"/> is given, restricts to that project's sessions: first by
    /// matching the ~/.claude/projects/{encoded-folder} directory (same rule as SessionService), and
    /// if none is found, by scanning every session and keeping lines whose "cwd" matches instead.
    /// Runs entirely on a background thread and never blocks the caller.
    /// </summary>
    public static Task<CostReport> BuildAsync(int days, string? projectFolder = null, CancellationToken ct = default)
    {
        return Task.Run(() => Build(days, projectFolder, ct), ct);
    }

    private static CostReport Build(int days, string? projectFolder, CancellationToken ct)
    {
        var grand = new TokenTotals();
        var dayTotals = new Dictionary<string, TokenTotals>();
        var modelTotals = new Dictionary<string, TokenTotals>();
        var projectTotals = new Dictionary<string, TokenTotals>();
        var projectLabels = new Dictionary<string, string>();
        var sessionTotals = new Dictionary<string, TokenTotals>();
        var sessionProject = new Dictionary<string, string>();

        int dayCount = Math.Max(1, days);
        DateTime todayLocal = DateTime.Now.Date;
        DateTime cutoffLocal = todayLocal.AddDays(-(dayCount - 1));

        // Pre-fill every day in range so the day list has one entry per calendar day,
        // even days with no usage, for a stable-width bar chart.
        for (var d = cutoffLocal; d <= todayLocal; d = d.AddDays(1))
            dayTotals[d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = new TokenTotals();

        bool projectScoped = !string.IsNullOrEmpty(projectFolder);

        try
        {
            string claudeProjectsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "projects");

            if (!Directory.Exists(claudeProjectsDir))
            {
                return MakeReport(grand, dayTotals, modelTotals, projectTotals, projectLabels, sessionTotals, sessionProject, dayCount, projectScoped);
            }

            var allDirs = Directory.GetDirectories(claudeProjectsDir);

            List<string> scopedDirs = new();
            string? normalizedTarget = null;
            if (projectScoped)
            {
                normalizedTarget = NormalizeFolderName(projectFolder!);
                scopedDirs = allDirs
                    .Where(d => NormalizeFolderName(Path.GetFileName(d)).Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            bool matchedByDirectory = scopedDirs.Count > 0;
            // Scoped-but-unmatched: fall back to scanning everything and filtering by cwd per line.
            bool requireCwdMatch = projectScoped && !matchedByDirectory;

            IEnumerable<string> dirsToScan = matchedByDirectory ? scopedDirs : allDirs;

            foreach (var dir in dirsToScan)
            {
                ct.ThrowIfCancellationRequested();
                string dirKey = Path.GetFileName(dir);

                string[] files;
                try { files = Directory.GetFiles(dir, "*.jsonl"); }
                catch (Exception ex) { Debug.WriteLine($"[CostAnalytics] Failed to list {dir}: {ex.Message}"); continue; }

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    ProcessFile(file, dirKey, requireCwdMatch ? normalizedTarget : null,
                        cutoffLocal, todayLocal,
                        grand, dayTotals, modelTotals, projectTotals, projectLabels, sessionTotals, sessionProject);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CostAnalytics] Build failed: {ex.Message}");
        }

        return MakeReport(grand, dayTotals, modelTotals, projectTotals, projectLabels, sessionTotals, sessionProject, dayCount, projectScoped);
    }

    private static void ProcessFile(
        string filePath, string dirKey, string? requireCwdNormalized,
        DateTime cutoffLocal, DateTime todayLocal,
        TokenTotals grand,
        Dictionary<string, TokenTotals> dayTotals,
        Dictionary<string, TokenTotals> modelTotals,
        Dictionary<string, TokenTotals> projectTotals,
        Dictionary<string, string> projectLabels,
        Dictionary<string, TokenTotals> sessionTotals,
        Dictionary<string, string> sessionProject)
    {
        string fallbackSessionId = Path.GetFileNameWithoutExtension(filePath);

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length > MaxLineChars) continue; // pathological/corrupt line - skip

                try
                {
                    ProcessLine(line, dirKey, fallbackSessionId, requireCwdNormalized, cutoffLocal, todayLocal,
                        grand, dayTotals, modelTotals, projectTotals, projectLabels, sessionTotals, sessionProject);
                }
                catch
                {
                    // One malformed line must not abort the whole file.
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CostAnalytics] Failed to read {filePath}: {ex.Message}");
        }
    }

    private static void ProcessLine(
        string line, string dirKey, string fallbackSessionId, string? requireCwdNormalized,
        DateTime cutoffLocal, DateTime todayLocal,
        TokenTotals grand,
        Dictionary<string, TokenTotals> dayTotals,
        Dictionary<string, TokenTotals> modelTotals,
        Dictionary<string, TokenTotals> projectTotals,
        Dictionary<string, string> projectLabels,
        Dictionary<string, TokenTotals> sessionTotals,
        Dictionary<string, string> sessionProject)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "assistant") return;

        if (!root.TryGetProperty("message", out var msgProp) || msgProp.ValueKind != JsonValueKind.Object) return;
        if (!msgProp.TryGetProperty("usage", out var usageProp) || usageProp.ValueKind != JsonValueKind.Object) return;

        long input = ReadLong(usageProp, "input_tokens");
        long output = ReadLong(usageProp, "output_tokens");
        long cacheRead = ReadLong(usageProp, "cache_read_input_tokens");
        long cacheCreation = ReadLong(usageProp, "cache_creation_input_tokens");
        if (input == 0 && output == 0 && cacheRead == 0 && cacheCreation == 0) return;

        string? timestampStr = root.TryGetProperty("timestamp", out var tsProp) && tsProp.ValueKind == JsonValueKind.String
            ? tsProp.GetString() : null;
        if (timestampStr == null) return;
        if (!DateTimeOffset.TryParse(timestampStr, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var tsUtc))
            return;

        DateTime dateLocal = tsUtc.ToLocalTime().Date;
        if (dateLocal < cutoffLocal || dateLocal > todayLocal) return;

        // cwd-fallback scoping: only used when the project's own JSONL directory was not found.
        string? cwd = root.TryGetProperty("cwd", out var cwdProp) && cwdProp.ValueKind == JsonValueKind.String
            ? cwdProp.GetString() : null;
        if (requireCwdNormalized != null)
        {
            if (cwd == null || !NormalizeFolderName(cwd).Equals(requireCwdNormalized, StringComparison.OrdinalIgnoreCase))
                return;
        }

        string model = msgProp.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String
            ? modelProp.GetString() ?? "unknown" : "unknown";

        double cost = EstimateCostUsd(model, input, output, cacheRead, cacheCreation);

        string sessionId = root.TryGetProperty("sessionId", out var sidProp) && sidProp.ValueKind == JsonValueKind.String
            ? sidProp.GetString() ?? fallbackSessionId : fallbackSessionId;

        // Project bucket: keyed by the encoded project directory (or the matched cwd when
        // falling back), labeled with a real cwd path when one was seen.
        string projectKey = requireCwdNormalized ?? dirKey;
        if (!projectLabels.TryGetValue(projectKey, out var existingLabel) || string.IsNullOrEmpty(existingLabel))
        {
            if (!string.IsNullOrEmpty(cwd))
                projectLabels[projectKey] = cwd!;
            else if (!projectLabels.ContainsKey(projectKey))
                projectLabels[projectKey] = dirKey;
        }

        grand.Add(input, output, cacheRead, cacheCreation, cost);

        string dayKey = dateLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        GetOrAdd(dayTotals, dayKey).Add(input, output, cacheRead, cacheCreation, cost);
        GetOrAdd(modelTotals, model).Add(input, output, cacheRead, cacheCreation, cost);
        GetOrAdd(projectTotals, projectKey).Add(input, output, cacheRead, cacheCreation, cost);
        GetOrAdd(sessionTotals, sessionId).Add(input, output, cacheRead, cacheCreation, cost);
        sessionProject[sessionId] = projectLabels.TryGetValue(projectKey, out var lbl) ? lbl : projectKey;
    }

    private static TokenTotals GetOrAdd(Dictionary<string, TokenTotals> dict, string key)
    {
        if (!dict.TryGetValue(key, out var totals))
        {
            totals = new TokenTotals();
            dict[key] = totals;
        }
        return totals;
    }

    private static long ReadLong(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop)) return 0;
        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.TryGetInt64(out var v) ? v : (long)prop.GetDouble(),
            _ => 0,
        };
    }

    private static CostReport MakeReport(
        TokenTotals grand,
        Dictionary<string, TokenTotals> dayTotals,
        Dictionary<string, TokenTotals> modelTotals,
        Dictionary<string, TokenTotals> projectTotals,
        Dictionary<string, string> projectLabels,
        Dictionary<string, TokenTotals> sessionTotals,
        Dictionary<string, string> sessionProject,
        int days, bool projectScoped)
    {
        var byDay = dayTotals
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new CostBucket { Key = kv.Key, Label = kv.Key, Totals = kv.Value })
            .ToList();

        var byModel = modelTotals
            .OrderByDescending(kv => kv.Value.CostUsd)
            .Select(kv => new CostBucket { Key = kv.Key, Label = kv.Key, Totals = kv.Value })
            .ToList();

        var byProject = projectTotals
            .OrderByDescending(kv => kv.Value.CostUsd)
            .Select(kv => new CostBucket
            {
                Key = kv.Key,
                Label = projectLabels.TryGetValue(kv.Key, out var lbl) ? lbl : kv.Key,
                Totals = kv.Value
            })
            .ToList();

        var bySession = sessionTotals
            .OrderByDescending(kv => kv.Value.CostUsd)
            .Take(MaxSessionRows)
            .Select(kv => new CostBucket
            {
                Key = kv.Key,
                Label = sessionProject.TryGetValue(kv.Key, out var proj)
                    ? $"{ShortId(kv.Key)} ({proj})"
                    : ShortId(kv.Key),
                Totals = kv.Value
            })
            .ToList();

        return new CostReport
        {
            Grand = grand,
            ByDay = byDay,
            ByModel = byModel,
            ByProject = byProject,
            BySession = bySession,
            Days = days,
            ProjectScoped = projectScoped,
        };
    }

    private static string ShortId(string sessionId)
        => sessionId.Length > 8 ? sessionId[..8] : sessionId;

    /// <summary>
    /// Render a report as CSV (UTF-8, no BOM assumed by the caller). One section per breakdown,
    /// each starting with a "# Name" heading line followed by a header row and data rows.
    /// </summary>
    public static string ToCsv(CostReport report)
    {
        var sb = new StringBuilder();

        AppendSection(sb, "Summary", new[]
        {
            new CostBucket { Key = "grand", Label = "Grand Total", Totals = report.Grand }
        });
        AppendSection(sb, "ByDay", report.ByDay);
        AppendSection(sb, "ByModel", report.ByModel);
        AppendSection(sb, "ByProject", report.ByProject);
        AppendSection(sb, "BySession", report.BySession);

        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string name, IReadOnlyList<CostBucket> buckets)
    {
        sb.Append("# ").Append(name).Append('\n');
        sb.Append("key,label,input,output,cacheRead,cacheCreation,total,costUsd\n");
        foreach (var b in buckets)
        {
            sb.Append(CsvField(b.Key)).Append(',')
              .Append(CsvField(b.Label)).Append(',')
              .Append(b.Totals.Input).Append(',')
              .Append(b.Totals.Output).Append(',')
              .Append(b.Totals.CacheRead).Append(',')
              .Append(b.Totals.CacheCreation).Append(',')
              .Append(b.Totals.Total).Append(',')
              .Append(b.Totals.CostUsd.ToString("F6", CultureInfo.InvariantCulture))
              .Append('\n');
        }
        sb.Append('\n');
    }

    private static string CsvField(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// Normalize a path or folder name to a comparable form. Must match
    /// SessionService.NormalizeFolderName exactly.
    /// </summary>
    private static string NormalizeFolderName(string path)
    {
        path = path.Replace('/', '\\').TrimEnd('\\');

        var sb = new StringBuilder(path.Length);
        bool lastWasDash = false;
        foreach (char c in path)
        {
            if (char.IsLetterOrDigit(c) && c <= 127)
            {
                sb.Append(c);
                lastWasDash = false;
            }
            else
            {
                if (!lastWasDash)
                    sb.Append('-');
                lastWasDash = true;
            }
        }
        return sb.ToString().Trim('-');
    }
}
