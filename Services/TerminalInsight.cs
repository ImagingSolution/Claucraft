using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Claucraft.Services;

/// <summary>The permission/edit mode Claude Code is currently running in, as shown on its status line.</summary>
public enum AiMode { Unknown, Normal, AcceptEdits, Plan, BypassPermissions }

/// <summary>What the AI appears to be doing right now, inferred from the most recent tool-call or status line.</summary>
public enum AiActivity { None, Thinking, Reading, Writing, Editing, RunningCommand, Searching, Browsing, Waiting }

/// <summary>Category of a detected error/warning banner, used to pick a helpful, actionable message.</summary>
public enum DiagnosisKind { None, AuthExpired, RateLimited, UsageLimit, NetworkDown, OutdatedCli, DiskOrPermission, UnknownError }

/// <summary>A human-readable explanation of an error banner found on screen, with an optional suggested action.</summary>
public sealed class ErrorDiagnosis
{
    public DiagnosisKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string? ActionLabel { get; init; }
    public string? ActionCommand { get; init; }
    public string MatchedLine { get; init; } = "";
}

/// <summary>A point-in-time reading of what the terminal screen shows about the AI's current state.</summary>
public sealed class TerminalSnapshot
{
    public AiMode Mode { get; init; }

    /// <summary>
    /// The CLI's own wording for <see cref="Mode"/>, taken verbatim from the status line
    /// ("accept edits", "plan mode", "bypass permissions"), with the trailing "on" dropped.
    /// Empty when the screen did not name a mode. The badge shows this so its label cannot
    /// drift from what the CLI prints.
    /// </summary>
    public string ModeText { get; init; } = "";

    public AiActivity Activity { get; init; }
    public string ActivityText { get; init; } = "";
    public string? ActivityTarget { get; init; }
    public int? ElapsedSeconds { get; init; }
    public bool IsWorking { get; init; }
    public int? ContextRemainingPercent { get; init; }
    public ErrorDiagnosis? Error { get; init; }
}

/// <summary>
/// Reads the plain (already ANSI-stripped) text of a terminal screen and infers what Claude Code
/// is currently doing, so the UI can show a mode badge, a "working" indicator, remaining context,
/// and a friendly explanation when something goes wrong. Pure logic - no Avalonia dependency, no
/// mutable static state, and no exceptions escape <see cref="Analyze"/>.
/// </summary>
public static class TerminalInsight
{
    /// <summary>Only the tail of the screen is scanned - the newest state is always at the bottom.</summary>
    private const int ScanWindowLines = 120;

    // ── Mode ─────────────────────────────────────────────────────────────

    /// <summary>Matches "⏵⏵ accept edits on (shift+tab to cycle)" and its 2.x wording "▶▶ auto mode on (shift+tab to cycle)".</summary>
    private static readonly Regex ModeAcceptEditsRegex = new(
        @"(?:auto[-\s]?accept\s+edits|accept\s+edits|auto\s+mode)\s+on",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "⏸ manual mode on", the 2.x wording for the plain, approve-everything mode.</summary>
    private static readonly Regex ModeManualRegex = new(@"manual\s+mode\s+on", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "⏸ plan mode on (shift+tab to cycle)".</summary>
    private static readonly Regex ModePlanRegex = new(@"plan\s+mode\s+on", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "⏵⏵ bypass permissions on".</summary>
    private static readonly Regex ModeBypassRegex = new(@"bypass\s+permissions", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches the input-box prompt line, e.g. "│ &gt;                                   │".</summary>
    private static readonly Regex InputPromptRegex = new(@"^\s*[│|]\s*>", RegexOptions.Compiled);

    // ── Activity / working state ────────────────────────────────────────

    /// <summary>Matches "✻ Cerebrating… (12s · ↓ 1.2k tokens · esc to interrupt)".</summary>
    private static readonly Regex WorkingLineRegex = new(@"esc\s+to\s+interrupt", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches the spinner's own progress readout, e.g. "✻ Deciphering… (23m 3s · ↓ 103.1k tokens)".</summary>
    private static readonly Regex WorkingSpinnerRegex = new(
        @"\(\s*(?:\d+m\s*)?\d+s\s*[·.].*tokens",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches the elapsed-time part of a working line: "(12s" or "(1m 5s".</summary>
    private static readonly Regex ElapsedRegex = new(@"\((?:(?<min>\d+)\s*m\s*)?(?<sec>\d+)\s*s\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches a tool-call line, e.g. "● Read(Services/AppSettings.cs)" or "● Web Search("…")".</summary>
    private static readonly Regex ToolLineRegex = new(@"[●•⏺✳]\s*([A-Za-z][A-Za-z ]*?)\(([^()]*)\)", RegexOptions.Compiled);

    /// <summary>Matches a permission-prompt question, e.g. "Do you want to proceed?" or "Do you want to make this edit?".</summary>
    private static readonly Regex WaitingRegex = new(@"do you want to", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Context remaining ────────────────────────────────────────────────

    /// <summary>Matches "Context left until auto-compact: 23%".</summary>
    private static readonly Regex ContextAutoCompactRegex = new(@"context\s+left\s+until\s+auto-?compact:?\s*(\d{1,3})\s*%", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "Context low (12% remaining)".</summary>
    private static readonly Regex ContextLowRegex = new(@"context\s+low\s*\(\s*(\d{1,3})\s*%\s*remaining\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "23% context left".</summary>
    private static readonly Regex ContextLeftRegex = new(@"(\d{1,3})\s*%\s*context\s+left", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches the /context readout, e.g. "Free space: 956.1k (95.6%)".</summary>
    private static readonly Regex ContextFreeSpaceRegex = new(
        @"free\s+space:\s*[\d.,]+\s*[km]?\s*\(\s*(\d{1,3})(?:\.\d+)?\s*%\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Error diagnosis ─────────────────────────────────────────────────

    /// <summary>Matches "Invalid API key", "Please run /login", "OAuth token has expired", "authentication_error", or a bare "401".</summary>
    private static readonly Regex DiagAuthExpiredRegex = new(@"invalid api key|please run /login|oauth token has expired|authentication_error|\b401\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "Rate limit exceeded, please try again", "rate_limit_error", or a bare "429".</summary>
    private static readonly Regex DiagRateLimitedRegex = new(@"rate limit|rate_limit_error|\b429\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "Usage limit reached" or "You've reached your usage limit".</summary>
    private static readonly Regex DiagUsageLimitRegex = new(@"usage limit reached|you.?ve reached your usage limit", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "resets at 3pm" so the reset time can be surfaced in the detail text.</summary>
    private static readonly Regex UsageResetRegex = new(@"resets?\s+at\s+([^\n\r.,;]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "ENOTFOUND", "ETIMEDOUT", "ECONNREFUSED", "network error", "fetch failed", or "getaddrinfo".</summary>
    private static readonly Regex DiagNetworkDownRegex = new(@"enotfound|etimedout|econnrefused|network error|fetch failed|getaddrinfo", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "Update available" or "A new version of Claude Code is available".</summary>
    private static readonly Regex DiagOutdatedCliRegex = new(@"update available|a new version of claude code", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "EACCES", "EPERM", "ENOSPC", or "permission denied".</summary>
    private static readonly Regex DiagDiskOrPermissionRegex = new(@"eacces|eperm|enospc|permission denied", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches a generic "Error: something broke" or "Failed to do X" line not caught by a more specific pattern.</summary>
    private static readonly Regex DiagGenericErrorRegex = new(@"error:|failed to", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Analyzes the visible terminal text and returns everything the UI needs to render badges/indicators. Never throws.</summary>
    /// <param name="screenText">The terminal's visible screen (plus a few dozen lines of scrollback), newline-separated with \n.</param>
    public static TerminalSnapshot Analyze(string screenText)
    {
        try
        {
            var lines = SplitWindow(screenText);

            var (mode, modeText) = DetectMode(lines);
            var (isWorking, elapsed) = DetectWorking(lines);
            var (activity, target) = DetectActivity(lines, isWorking);
            var contextPct = DetectContextRemaining(lines);
            var error = DetectError(lines);

            return new TerminalSnapshot
            {
                Mode = mode,
                ModeText = modeText,
                Activity = activity,
                ActivityText = ActivityLabel(activity),
                ActivityTarget = target,
                ElapsedSeconds = elapsed,
                IsWorking = isWorking,
                ContextRemainingPercent = contextPct,
                Error = error
            };
        }
        catch
        {
            return new TerminalSnapshot { Mode = AiMode.Unknown, Activity = AiActivity.None };
        }
    }

    /// <summary>Localized long-form display name for a mode, e.g. for a tooltip.</summary>
    public static string ModeLabel(AiMode mode) => mode switch
    {
        AiMode.Normal => Loc.Get("ModeNormalLabel", "Normal mode"),
        AiMode.AcceptEdits => Loc.Get("ModeAcceptEditsLabel", "Auto-accept edits"),
        AiMode.Plan => Loc.Get("ModePlanLabel", "Plan mode"),
        AiMode.BypassPermissions => Loc.Get("ModeBypassPermissionsLabel", "Bypass permissions"),
        _ => Loc.Get("ModeUnknownLabel", "Unknown mode")
    };

    /// <summary>Localized short display name for a mode, sized for a small status badge.</summary>
    public static string ModeShortLabel(AiMode mode) => mode switch
    {
        AiMode.Normal => Loc.Get("ModeNormalShort", "normal"),
        AiMode.AcceptEdits => Loc.Get("ModeAcceptEditsShort", "auto-accept"),
        AiMode.Plan => Loc.Get("ModePlanShort", "plan"),
        AiMode.BypassPermissions => Loc.Get("ModeBypassPermissionsShort", "bypass"),
        _ => Loc.Get("ModeUnknownShort", "?")
    };

    // ── Internals ────────────────────────────────────────────────────────

    private static string[] SplitWindow(string screenText)
    {
        if (string.IsNullOrEmpty(screenText)) return Array.Empty<string>();
        var all = screenText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return all.Length <= ScanWindowLines ? all : all[^ScanWindowLines..];
    }

    private static (AiMode Mode, string Text) DetectMode(string[] lines)
    {
        var sawInputPrompt = false;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            Match m;
            if ((m = ModeBypassRegex.Match(line)).Success) return (AiMode.BypassPermissions, ModePhrase(m.Value));
            if ((m = ModePlanRegex.Match(line)).Success) return (AiMode.Plan, ModePhrase(m.Value));
            if ((m = ModeManualRegex.Match(line)).Success) return (AiMode.Normal, ModePhrase(m.Value));
            if ((m = ModeAcceptEditsRegex.Match(line)).Success) return (AiMode.AcceptEdits, ModePhrase(m.Value));
            if (!sawInputPrompt && InputPromptRegex.IsMatch(line)) sawInputPrompt = true;
        }
        return sawInputPrompt ? (AiMode.Normal, "") : (AiMode.Unknown, "");
    }

    /// <summary>Matches the trailing " on" of "accept edits on", which reads as noise on a badge.</summary>
    private static readonly Regex ModePhraseTailRegex = new(@"\s+on$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Tidies a matched mode phrase into a badge label: collapsed spaces, lower case, no trailing "on".</summary>
    private static string ModePhrase(string matched)
    {
        var phrase = Regex.Replace(matched.Trim(), @"\s+", " ");
        return ModePhraseTailRegex.Replace(phrase, "").ToLowerInvariant();
    }

    private static (bool IsWorking, int? ElapsedSeconds) DetectWorking(string[] lines)
    {
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (!WorkingLineRegex.IsMatch(lines[i]) && !WorkingSpinnerRegex.IsMatch(lines[i])) continue;

            var m = ElapsedRegex.Match(lines[i]);
            if (!m.Success) return (true, null);

            var seconds = int.TryParse(m.Groups["sec"].Value, out var s) ? s : 0;
            if (m.Groups["min"].Success && int.TryParse(m.Groups["min"].Value, out var min))
                seconds += min * 60;
            return (true, seconds);
        }
        return (false, null);
    }

    private static readonly Dictionary<string, AiActivity> ToolVerbMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read"] = AiActivity.Reading,
        ["cat"] = AiActivity.Reading,
        ["write"] = AiActivity.Writing,
        ["create"] = AiActivity.Writing,
        ["edit"] = AiActivity.Editing,
        ["update"] = AiActivity.Editing,
        ["multiedit"] = AiActivity.Editing,
        ["bash"] = AiActivity.RunningCommand,
        ["run"] = AiActivity.RunningCommand,
        ["search"] = AiActivity.Searching,
        ["grep"] = AiActivity.Searching,
        ["glob"] = AiActivity.Searching,
        ["find"] = AiActivity.Searching,
        ["webfetch"] = AiActivity.Browsing,
        ["websearch"] = AiActivity.Browsing,
        ["fetch"] = AiActivity.Browsing,
    };

    private static (AiActivity Activity, string? Target) DetectActivity(string[] lines, bool isWorking)
    {
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var m = ToolLineRegex.Match(lines[i]);
            if (!m.Success) continue;

            var verbKey = Regex.Replace(m.Groups[1].Value, @"\s+", "");
            if (!ToolVerbMap.TryGetValue(verbKey, out var activity)) continue;

            var target = m.Groups[2].Value.Trim();
            if (target.Length > 60) target = target[..60];
            return (activity, target.Length == 0 ? null : target);
        }

        if (isWorking) return (AiActivity.Thinking, null);

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (WaitingRegex.IsMatch(lines[i])) return (AiActivity.Waiting, null);
        }

        return (AiActivity.None, null);
    }

    private static string ActivityLabel(AiActivity activity) => activity switch
    {
        AiActivity.Thinking => Loc.Get("ActivityThinking", "Thinking…"),
        AiActivity.Reading => Loc.Get("ActivityReading", "Reading files…"),
        AiActivity.Writing => Loc.Get("ActivityWriting", "Writing a file…"),
        AiActivity.Editing => Loc.Get("ActivityEditing", "Editing a file…"),
        AiActivity.RunningCommand => Loc.Get("ActivityRunning", "Running a command…"),
        AiActivity.Searching => Loc.Get("ActivitySearching", "Searching…"),
        AiActivity.Browsing => Loc.Get("ActivityBrowsing", "Fetching from the web…"),
        AiActivity.Waiting => Loc.Get("ActivityWaiting", "Waiting for your answer…"),
        _ => ""
    };

    private static int? DetectContextRemaining(string[] lines)
    {
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];

            var m = ContextAutoCompactRegex.Match(line);
            if (!m.Success) m = ContextLowRegex.Match(line);
            if (!m.Success) m = ContextLeftRegex.Match(line);
            if (!m.Success) m = ContextFreeSpaceRegex.Match(line);
            if (!m.Success) continue;

            if (int.TryParse(m.Groups[1].Value, out var pct))
                return Math.Clamp(pct, 0, 100);
        }
        return null;
    }

    private static ErrorDiagnosis? DetectError(string[] lines)
    {
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (DiagAuthExpiredRegex.IsMatch(line))
                return BuildDiagnosis(DiagnosisKind.AuthExpired, trimmed,
                    "DiagAuthExpiredTitle", "Your session has expired",
                    "DiagAuthExpiredDetail", "Sign in again to continue.",
                    "DiagAuthExpiredAction", "Sign in", "/login");

            if (DiagRateLimitedRegex.IsMatch(line))
                return BuildDiagnosis(DiagnosisKind.RateLimited, trimmed,
                    "DiagRateLimitedTitle", "Rate limit reached",
                    "DiagRateLimitedDetail", "Too many requests were sent in a short time. Wait a moment and try again.",
                    null, null, null);

            if (DiagUsageLimitRegex.IsMatch(line))
            {
                var detail = Loc.Get("DiagUsageLimitDetail", "You've used up your available usage for this period.");
                var resetMatch = UsageResetRegex.Match(line);
                if (resetMatch.Success)
                {
                    var suffix = string.Format(Loc.Get("DiagUsageLimitResetSuffix", "It resets at {0}."), resetMatch.Groups[1].Value.Trim());
                    detail = detail + " " + suffix;
                }

                return new ErrorDiagnosis
                {
                    Kind = DiagnosisKind.UsageLimit,
                    Title = Loc.Get("DiagUsageLimitTitle", "Usage limit reached"),
                    Detail = detail,
                    ActionLabel = null,
                    ActionCommand = null,
                    MatchedLine = trimmed
                };
            }

            if (DiagNetworkDownRegex.IsMatch(line))
                return BuildDiagnosis(DiagnosisKind.NetworkDown, trimmed,
                    "DiagNetworkDownTitle", "Network connection problem",
                    "DiagNetworkDownDetail", "Claude Code couldn't reach the server. Check your internet connection and try again.",
                    null, null, null);

            if (DiagOutdatedCliRegex.IsMatch(line))
                return BuildDiagnosis(DiagnosisKind.OutdatedCli, trimmed,
                    "DiagOutdatedCliTitle", "A newer version of Claude Code is available",
                    "DiagOutdatedCliDetail", "Update to get the latest fixes and features.",
                    "DiagOutdatedCliAction", "Copy update command", "npm install -g @anthropic-ai/claude-code");

            if (DiagDiskOrPermissionRegex.IsMatch(line))
                return BuildDiagnosis(DiagnosisKind.DiskOrPermission, trimmed,
                    "DiagDiskOrPermissionTitle", "Disk or permission error",
                    "DiagDiskOrPermissionDetail", "Claude Code couldn't read or write a file. Check disk space and file permissions.",
                    null, null, null);

            if (DiagGenericErrorRegex.IsMatch(line))
                return BuildDiagnosis(DiagnosisKind.UnknownError, trimmed,
                    "DiagUnknownErrorTitle", "Something went wrong",
                    "DiagUnknownErrorDetail", "An error occurred. Check the terminal output above for details.",
                    null, null, null);
        }
        return null;
    }

    private static ErrorDiagnosis BuildDiagnosis(
        DiagnosisKind kind, string matchedLine,
        string titleKey, string titleDefault,
        string detailKey, string detailDefault,
        string? actionKey, string? actionDefault, string? actionCommand)
    {
        return new ErrorDiagnosis
        {
            Kind = kind,
            Title = Loc.Get(titleKey, titleDefault),
            Detail = Loc.Get(detailKey, detailDefault),
            ActionLabel = actionKey != null ? Loc.Get(actionKey, actionDefault ?? "") : null,
            ActionCommand = actionCommand,
            MatchedLine = matchedLine
        };
    }
}
