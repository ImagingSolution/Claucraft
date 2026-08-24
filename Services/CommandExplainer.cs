using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Claucraft.Services;

/// <summary>How risky it looks for the user to approve this action.</summary>
public enum RiskLevel { ReadOnly, FileChange, Dangerous }

/// <summary>A plain-language explanation of a permission prompt, so a non-expert user can decide whether to approve it.</summary>
public sealed class CommandExplanation
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public RiskLevel Risk { get; init; }
    public string RiskLabel { get; init; } = "";
    public string? Command { get; init; }
}

/// <summary>
/// Extracts the command or file target from a Claude Code permission prompt and explains it in plain
/// language, with a risk rating the UI can color-code. Pure logic - no Avalonia dependency, no mutable
/// static state, and no exceptions escape <see cref="Explain"/>.
/// </summary>
public static class CommandExplainer
{
    private enum PromptSubjectKind { None, BashCommand, EditFile, WriteFile, CreateFile, ReadFile }

    // ── Extraction patterns ─────────────────────────────────────────────

    /// <summary>Matches a "Bash command" heading, e.g. "Bash command" or "│ Bash command │".</summary>
    private static readonly Regex BashHeadingRegex = new(@"^\s*[│|]?\s*bash\s+command\s*:?\s*[│|]?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches an "Edit file" / "Write file" / "Create file" / "Read file" heading.</summary>
    private static readonly Regex FileHeadingRegex = new(@"^\s*[│|]?\s*(edit|write|create|read)\s+file\s*:?\s*[│|]?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches the confirmation question, e.g. "Do you want to proceed?".</summary>
    private static readonly Regex ProceedRegex = new(@"do you want to proceed", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches a line made up only of box-drawing/decoration characters, e.g. "╭──────────────╮".</summary>
    private static readonly Regex BoxBorderOnlyRegex = new(@"^[\s│|╭╮╰╯─\-+=]*$", RegexOptions.Compiled);

    // ── Risk-escalation patterns ─────────────────────────────────────────

    /// <summary>Matches "sudo ...", "taskkill ...", "shutdown ...", "reg delete ...", "format c:", or any command piped into a shell (e.g. "curl url | sh").</summary>
    private static readonly Regex DangerousPatternRegex = new(
        @"format\s+[a-z]:|\|\s*(sudo\s+)?(sh|bash|zsh)\b|\bsudo\b|\btaskkill\b|\bshutdown\b|reg\s+delete",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches a file-overwrite redirect, e.g. "curl url &gt; file.txt", but not "&gt;&gt;" (append) or "2&gt;&amp;1".</summary>
    private static readonly Regex RedirectOverwriteRegex = new(@"(?<![>&\d\-])>(?!>)", RegexOptions.Compiled);

    /// <summary>Splits a compound command on "&amp;&amp;", ";", or "|" so each segment can be checked independently.</summary>
    private static readonly Regex ChainSplitRegex = new(@"&&|;|\|", RegexOptions.Compiled);

    private readonly record struct CatalogEntry(RiskLevel BaseRisk, string DetailKey, string DetailDefault);

    private static readonly CatalogEntry GenericEntry = new(
        RiskLevel.FileChange, "CmdExplainGenericDetail", "This command isn't recognized. Review it carefully before allowing it to run.");

    private static readonly Dictionary<string, CatalogEntry> CommandCatalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["git"] = new(RiskLevel.FileChange, "CmdExplainGitDetail", "Git is used for version control - depending on the subcommand, this can read repository history or change tracked files."),
        ["npm"] = new(RiskLevel.FileChange, "CmdExplainNpmDetail", "npm manages Node.js packages and scripts; it can install packages or run project scripts."),
        ["dotnet"] = new(RiskLevel.FileChange, "CmdExplainDotnetDetail", "The .NET CLI can build, run, restore, or publish this project."),
        ["python"] = new(RiskLevel.FileChange, "CmdExplainPythonDetail", "Runs a Python script, which can do anything the script is written to do."),
        ["python3"] = new(RiskLevel.FileChange, "CmdExplainPythonDetail", "Runs a Python script, which can do anything the script is written to do."),
        ["pip"] = new(RiskLevel.FileChange, "CmdExplainPipDetail", "pip installs or manages Python packages."),
        ["pip3"] = new(RiskLevel.FileChange, "CmdExplainPipDetail", "pip installs or manages Python packages."),
        ["node"] = new(RiskLevel.FileChange, "CmdExplainNodeDetail", "Runs a Node.js script or program."),
        ["ls"] = new(RiskLevel.ReadOnly, "CmdExplainLsDetail", "Lists files in a folder. It does not change anything."),
        ["dir"] = new(RiskLevel.ReadOnly, "CmdExplainDirDetail", "Lists files in a folder. It does not change anything."),
        ["cat"] = new(RiskLevel.ReadOnly, "CmdExplainCatDetail", "Prints a file's contents. It does not change anything."),
        ["type"] = new(RiskLevel.ReadOnly, "CmdExplainTypeDetail", "Prints a file's contents. It does not change anything."),
        ["cd"] = new(RiskLevel.ReadOnly, "CmdExplainCdDetail", "Changes the current directory for the session."),
        ["mkdir"] = new(RiskLevel.FileChange, "CmdExplainMkdirDetail", "Creates a new folder."),
        ["rm"] = new(RiskLevel.Dangerous, "CmdExplainRmDetail", "Deletes files or folders. This cannot be undone."),
        ["del"] = new(RiskLevel.Dangerous, "CmdExplainDelDetail", "Deletes files. This cannot be undone."),
        ["rmdir"] = new(RiskLevel.Dangerous, "CmdExplainRmdirDetail", "Deletes a folder and, depending on flags, everything inside it."),
        ["mv"] = new(RiskLevel.FileChange, "CmdExplainMvDetail", "Moves or renames a file or folder."),
        ["move"] = new(RiskLevel.FileChange, "CmdExplainMoveDetail", "Moves or renames a file or folder."),
        ["cp"] = new(RiskLevel.FileChange, "CmdExplainCpDetail", "Copies a file or folder."),
        ["copy"] = new(RiskLevel.FileChange, "CmdExplainCopyDetail", "Copies a file or folder."),
        ["curl"] = new(RiskLevel.ReadOnly, "CmdExplainCurlDetail", "Fetches data from a URL. It only writes a file if given an output flag."),
        ["wget"] = new(RiskLevel.FileChange, "CmdExplainWgetDetail", "Downloads a file from a URL and saves it to disk."),
        ["ssh"] = new(RiskLevel.FileChange, "CmdExplainSshDetail", "Opens a connection to a remote machine and can run commands there."),
        ["scp"] = new(RiskLevel.FileChange, "CmdExplainScpDetail", "Copies files to or from a remote machine."),
        ["docker"] = new(RiskLevel.FileChange, "CmdExplainDockerDetail", "Manages Docker containers or images."),
        ["gh"] = new(RiskLevel.FileChange, "CmdExplainGhDetail", "Interacts with GitHub (issues, pull requests, releases, etc.)."),
        ["winget"] = new(RiskLevel.FileChange, "CmdExplainWingetDetail", "Installs or manages software packages on Windows."),
        ["choco"] = new(RiskLevel.FileChange, "CmdExplainChocoDetail", "Installs or manages software packages via Chocolatey."),
        ["powershell"] = new(RiskLevel.FileChange, "CmdExplainPowershellDetail", "Runs a PowerShell script or command, which can do anything the script is written to do."),
        ["taskkill"] = new(RiskLevel.Dangerous, "CmdExplainTaskkillDetail", "Forcibly ends a running process."),
        ["netstat"] = new(RiskLevel.ReadOnly, "CmdExplainNetstatDetail", "Shows network connection information. It does not change anything."),
        ["findstr"] = new(RiskLevel.ReadOnly, "CmdExplainFindstrDetail", "Searches for text inside files. It does not change anything."),
        ["grep"] = new(RiskLevel.ReadOnly, "CmdExplainGrepDetail", "Searches for text inside files. It does not change anything."),
        ["sed"] = new(RiskLevel.ReadOnly, "CmdExplainSedDetail", "Processes text. With an in-place flag (-i) it can rewrite files."),
        ["awk"] = new(RiskLevel.ReadOnly, "CmdExplainAwkDetail", "Processes text, typically printing results rather than changing files."),
        ["tar"] = new(RiskLevel.FileChange, "CmdExplainTarDetail", "Creates or extracts an archive of files."),
        ["zip"] = new(RiskLevel.FileChange, "CmdExplainZipDetail", "Creates or extracts a compressed archive of files."),
        ["unzip"] = new(RiskLevel.FileChange, "CmdExplainZipDetail", "Creates or extracts a compressed archive of files."),
        ["find"] = new(RiskLevel.ReadOnly, "CmdExplainFindDetail", "Searches for files matching a pattern. It does not change anything by default."),
        ["echo"] = new(RiskLevel.ReadOnly, "CmdExplainEchoDetail", "Prints text to the terminal. It does not change anything unless combined with a redirect."),
    };

    /// <summary>Extracts and explains the subject of a permission prompt. Returns null if nothing could be extracted. Never throws.</summary>
    /// <param name="promptText">The raw, multi-line text surrounding a permission prompt.</param>
    public static CommandExplanation? Explain(string promptText)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(promptText)) return null;

            var lines = promptText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var (kind, text) = ExtractSubject(lines);

            return kind switch
            {
                PromptSubjectKind.BashCommand => ExplainCommand(text),
                PromptSubjectKind.EditFile or PromptSubjectKind.WriteFile or
                PromptSubjectKind.CreateFile or PromptSubjectKind.ReadFile => ExplainFileAction(kind, text),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    // ── Extraction ───────────────────────────────────────────────────────

    private static (PromptSubjectKind Kind, string Text) ExtractSubject(string[] lines)
    {
        // 1) "Bash command" heading, followed by the command body.
        for (var i = 0; i < lines.Length; i++)
        {
            if (!BashHeadingRegex.IsMatch(lines[i])) continue;
            var cmd = FindNextContentLine(lines, i + 1);
            if (cmd != null) return (PromptSubjectKind.BashCommand, cmd);
        }

        // 2) "Edit file" / "Write file" / "Create file" / "Read file" heading, followed by the path.
        for (var i = 0; i < lines.Length; i++)
        {
            var m = FileHeadingRegex.Match(lines[i]);
            if (!m.Success) continue;

            var target = FindNextContentLine(lines, i + 1);
            if (target == null) continue;

            var kind = m.Groups[1].Value.ToLowerInvariant() switch
            {
                "edit" => PromptSubjectKind.EditFile,
                "write" => PromptSubjectKind.WriteFile,
                "create" => PromptSubjectKind.CreateFile,
                "read" => PromptSubjectKind.ReadFile,
                _ => PromptSubjectKind.None
            };
            if (kind != PromptSubjectKind.None) return (kind, target);
        }

        // 3) Fallback: the block of text directly above "Do you want to proceed?".
        for (var i = 0; i < lines.Length; i++)
        {
            if (!ProceedRegex.IsMatch(lines[i])) continue;

            for (var j = i - 1; j >= 0; j--)
            {
                if (BoxBorderOnlyRegex.IsMatch(lines[j])) continue;
                var stripped = StripDecorations(lines[j]);
                if (stripped.Length == 0) continue;
                return (PromptSubjectKind.BashCommand, stripped);
            }
        }

        return (PromptSubjectKind.None, "");
    }

    /// <summary>Scans forward from <paramref name="start"/> for the first real content line, skipping blank/border lines.</summary>
    private static string? FindNextContentLine(string[] lines, int start)
    {
        var limit = Math.Min(lines.Length, start + 6);
        for (var i = start; i < limit; i++)
        {
            if (ProceedRegex.IsMatch(lines[i])) break;
            if (BoxBorderOnlyRegex.IsMatch(lines[i])) continue;

            var stripped = StripDecorations(lines[i]);
            if (stripped.Length == 0) continue;
            return stripped;
        }
        return null;
    }

    private static string StripDecorations(string line) => line.Trim().Trim('│', '|').Trim();

    // ── Explanation building ─────────────────────────────────────────────

    private static CommandExplanation ExplainCommand(string rawCommand)
    {
        var command = rawCommand.Trim();
        var headVerb = FirstToken(command).ToLowerInvariant();
        var entry = CommandCatalog.TryGetValue(headVerb, out var found) ? found : GenericEntry;

        var risk = ClassifyRisk(command, headVerb, entry.BaseRisk);

        return new CommandExplanation
        {
            Title = string.Format(Loc.Get("CmdExplainRunTitle", "Run command: {0}"), command),
            Detail = Loc.Get(entry.DetailKey, entry.DetailDefault),
            Risk = risk,
            RiskLabel = RiskLabelFor(risk),
            Command = command
        };
    }

    private static CommandExplanation ExplainFileAction(PromptSubjectKind kind, string target)
    {
        string titleKey, titleDefault, detailKey, detailDefault;
        RiskLevel risk;

        switch (kind)
        {
            case PromptSubjectKind.EditFile:
                titleKey = "CmdExplainEditFileTitle"; titleDefault = "Edit file: {0}";
                detailKey = "CmdExplainEditFileDetail"; detailDefault = "Claude wants to modify the contents of this file.";
                risk = RiskLevel.FileChange;
                break;
            case PromptSubjectKind.WriteFile:
                titleKey = "CmdExplainWriteFileTitle"; titleDefault = "Write file: {0}";
                detailKey = "CmdExplainWriteFileDetail"; detailDefault = "Claude wants to overwrite this file with new contents.";
                risk = RiskLevel.FileChange;
                break;
            case PromptSubjectKind.CreateFile:
                titleKey = "CmdExplainCreateFileTitle"; titleDefault = "Create file: {0}";
                detailKey = "CmdExplainCreateFileDetail"; detailDefault = "Claude wants to create a new file.";
                risk = RiskLevel.FileChange;
                break;
            default:
                titleKey = "CmdExplainReadFileTitle"; titleDefault = "Read file: {0}";
                detailKey = "CmdExplainReadFileDetail"; detailDefault = "Claude wants to read the contents of this file.";
                risk = RiskLevel.ReadOnly;
                break;
        }

        return new CommandExplanation
        {
            Title = string.Format(Loc.Get(titleKey, titleDefault), target),
            Detail = Loc.Get(detailKey, detailDefault),
            Risk = risk,
            RiskLabel = RiskLabelFor(risk),
            Command = null
        };
    }

    private static string RiskLabelFor(RiskLevel risk) => risk switch
    {
        RiskLevel.ReadOnly => Loc.Get("RiskReadOnly", "Read-only"),
        RiskLevel.FileChange => Loc.Get("RiskFileChange", "Changes files"),
        RiskLevel.Dangerous => Loc.Get("RiskDangerous", "Deletion or network access"),
        _ => ""
    };

    // ── Risk classification ─────────────────────────────────────────────

    private static RiskLevel ClassifyRisk(string command, string headVerb, RiskLevel baseRisk)
    {
        var lower = command.ToLowerInvariant();
        var risk = RefineForVerb(lower, headVerb, baseRisk);

        if (DangerousPatternRegex.IsMatch(lower)) risk = Max(risk, RiskLevel.Dangerous);
        if (RedirectOverwriteRegex.IsMatch(lower)) risk = Max(risk, RiskLevel.Dangerous);

        foreach (var rawSegment in ChainSplitRegex.Split(lower))
        {
            var segment = rawSegment.Trim();
            if (segment.Length == 0) continue;

            if (DangerousPatternRegex.IsMatch(segment))
            {
                risk = Max(risk, RiskLevel.Dangerous);
                continue;
            }

            var segmentVerb = FirstToken(segment);
            if (segmentVerb.Length > 0 && CommandCatalog.TryGetValue(segmentVerb, out var segmentEntry))
                risk = Max(risk, RefineForVerb(segment, segmentVerb, segmentEntry.BaseRisk));
        }

        return risk;
    }

    /// <summary>
    /// Some verbs are only as risky as their subcommand or flags: "git status" just reads,
    /// "git push --force" rewrites history. The refined verdict replaces the catalog's base
    /// risk rather than being merged with it, so a read-only subcommand can rate read-only.
    /// </summary>
    private static RiskLevel RefineForVerb(string lowerCommand, string verb, RiskLevel baseRisk) => verb switch
    {
        "git" => ClassifyGit(lowerCommand),
        "curl" or "wget" => ClassifyDownload(lowerCommand, verb),
        "sed" => lowerCommand.Contains("-i") ? RiskLevel.FileChange : RiskLevel.ReadOnly,
        _ => baseRisk,
    };

    private static RiskLevel ClassifyGit(string lower)
    {
        var sub = SecondToken(lower);
        return sub switch
        {
            "status" or "diff" or "log" or "show" or "branch" or "remote" or "blame" => RiskLevel.ReadOnly,
            "push" => lower.Contains("--force") || Regex.IsMatch(lower, @"(^|\s)-f(\s|$)") ? RiskLevel.Dangerous : RiskLevel.FileChange,
            "reset" => lower.Contains("--hard") ? RiskLevel.Dangerous : RiskLevel.FileChange,
            "" => RiskLevel.ReadOnly,
            _ => RiskLevel.FileChange
        };
    }

    private static RiskLevel ClassifyDownload(string lower, string headVerb)
    {
        if (headVerb == "wget") return RiskLevel.FileChange; // wget saves to disk by default
        return Regex.IsMatch(lower, @"(^|\s)-o(\s|$)|--output\b") ? RiskLevel.FileChange : RiskLevel.ReadOnly;
    }

    private static RiskLevel Max(RiskLevel a, RiskLevel b) => (RiskLevel)Math.Max((int)a, (int)b);

    private static string FirstToken(string s)
    {
        var trimmed = s.TrimStart();
        var idx = trimmed.IndexOfAny(new[] { ' ', '\t' });
        return idx < 0 ? trimmed : trimmed[..idx];
    }

    private static string SecondToken(string s)
    {
        var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[1] : "";
    }
}
