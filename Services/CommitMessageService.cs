using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Claucraft.Services;

/// <summary>
/// Drafts a commit message by handing the staged diff to whichever AI CLI is selected, run once
/// in the background rather than typed into the session the user is working in - a draft must
/// not cost that conversation any context.
///
/// This runs on every commit, so it is deliberately cheap: the provider preset pins the smallest
/// model and skips MCP startup, and the diff is cut down before it is sent, because the input
/// tokens are the bill.
/// </summary>
public static class CommitMessageService
{
    /// <summary>
    /// How much diff travels with the prompt. Summarising a change does not need every line of
    /// it, and a single generated file would otherwise eat the whole budget.
    /// </summary>
    private const int MaxDiffCharsStdin = 8000;

    /// <summary>Tighter when the diff has to go on the command line, which has its own limit.</summary>
    private const int MaxDiffCharsArgs = 6000;

    /// <summary>Room for the file list. A change touching more files than this fits is rare.</summary>
    private const int MaxStatChars = 2000;

    private const int TimeoutMs = 120_000;

    /// <summary>
    /// What a Claude Code session exports to everything it starts. Claucraft may itself have been
    /// launched from inside one, and a CLI that inherits these joins that session instead of
    /// running once on its own - so they are cleared for this call.
    /// </summary>
    private static readonly string[] SessionVars =
    {
        "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_EXECPATH", "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_BRIDGE_SESSION_ID", "CLAUDE_CODE_MESSAGING_SOCKET",
        "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_PID", "CLAUDE_EFFORT",
    };

    private static Dictionary<string, string> CleanEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // ProcessRunner reads an empty value as "remove this variable".
        foreach (var name in SessionVars) env[name] = "";

        // Claude Code's knob for extended thinking, ignored by every other CLI. Summarising a
        // diff does not need it, and it was over half the bill: measured here, turning it off
        // took a draft from 5,900 output tokens to 138.
        env["MAX_THINKING_TOKENS"] = "0";
        return env;
    }

    /// <summary>
    /// Writes a commit message for what is staged, or null when there is nothing to describe,
    /// the CLI has no one-shot preset, or it produced nothing usable. The panel turns a null
    /// into a message of its own rather than leaving the box mysteriously empty.
    /// </summary>
    public static async Task<string?> GenerateAsync(string repoRoot, CliProvider provider,
        string language, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(repoRoot) || provider == null) return null;
        if (string.IsNullOrWhiteSpace(provider.OneShotArgs)) return null;

        var diff = await GitWriteService.GetStagedDiffAsync(repoRoot);
        if (string.IsNullOrWhiteSpace(diff)) return null;

        ct.ThrowIfCancellationRequested();

        var stat = await GitWriteService.GetStagedStatAsync(repoRoot);

        var args = SplitArgs(provider.OneShotArgs);
        bool inline = Array.Exists(args, a => a.Contains("{prompt}", StringComparison.Ordinal));

        var prompt = BuildPrompt(language, stat,
            Truncate(diff, inline ? MaxDiffCharsArgs : MaxDiffCharsStdin));
        var exe = string.IsNullOrEmpty(provider.ResolvedPath) ? provider.Exe : provider.ResolvedPath!;

        string? stdin = inline ? null : prompt;
        if (inline)
        {
            for (int i = 0; i < args.Length; i++)
                args[i] = args[i].Replace("{prompt}", prompt, StringComparison.Ordinal);
        }

        var result = await Task.Run(
            () => ProcessRunner.Run(exe, repoRoot, stdin, TimeoutMs, CleanEnvironment(), args), ct);

        ct.ThrowIfCancellationRequested();
        if (!result.Ok) return null;

        var text = Clean(result.StdOut);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Which language a draft is written in. "auto" follows the UI, so the setting only has to
    /// be touched by someone who wants the two to differ.
    /// </summary>
    public static string ResolveLanguage(string? setting)
    {
        if (string.Equals(setting, "ja", StringComparison.OrdinalIgnoreCase)) return "ja";
        if (string.Equals(setting, "en", StringComparison.OrdinalIgnoreCase)) return "en";
        return Loc.Language == "日本語" ? "ja" : "en";
    }

    // ── Prompt ─────────────────────────────────────────────────────────

    internal static string BuildPrompt(string language, string stat, string diff)
    {
        var sb = new StringBuilder();

        if (ResolveLanguage(language) == "ja")
        {
            sb.Append("次の変更を読み、コミットメッセージを1つだけ日本語で出力してください。\n");
            sb.Append("- 1行目は50文字以内の要約。文末に句点は付けない\n");
            sb.Append("- 補足が要る場合のみ、空行を挟んで本文を続ける\n");
            sb.Append("- ファイル一覧が示す変更全体をまとめる。diff は途中で省略されることがある\n");
            sb.Append("- 前置き・説明・コードフェンス・引用符は書かない。メッセージ本体だけを出力する\n");
        }
        else
        {
            sb.Append("Read the change below and write exactly one commit message in English.\n");
            sb.Append("- First line: a summary of 50 characters or less, no trailing period\n");
            sb.Append("- Only if it needs one, a body after a blank line\n");
            sb.Append("- Describe the whole change the file list shows; the diff may be truncated\n");
            sb.Append("- No preamble, no explanation, no code fences, no quotes. Output the message only\n");
        }

        // The file list goes first and is never truncated: it is the only part that always shows
        // the full scope of what is being committed.
        if (!string.IsNullOrWhiteSpace(stat))
        {
            var files = stat.Trim();
            if (files.Length > MaxStatChars) files = files[..MaxStatChars];
            sb.Append("\n--- files changed ---\n");
            sb.Append(files).Append('\n');
        }

        sb.Append("\n--- git diff ---\n");
        sb.Append(diff);
        return sb.ToString();
    }

    /// <summary>
    /// Cuts the diff down to <paramref name="maxChars"/>, always at a file or hunk boundary so
    /// the model never reads half a hunk header, and says how many files were left out rather
    /// than stopping mid-sentence.
    /// </summary>
    internal static string Truncate(string diff, int maxChars)
    {
        if (string.IsNullOrEmpty(diff) || diff.Length <= maxChars) return diff ?? "";

        var lines = diff.Split('\n');
        var sb = new StringBuilder();

        int safeLength = 0;   // length of sb at the last boundary
        int safeLine = 0;     // the line that boundary sits on
        int cutLine = lines.Length;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            bool boundary = line.StartsWith("diff --git ", StringComparison.Ordinal)
                || line.StartsWith("@@", StringComparison.Ordinal);

            if (boundary)
            {
                safeLength = sb.Length;
                safeLine = i;
            }

            if (sb.Length + line.Length + 1 > maxChars)
            {
                // Prefer the last clean boundary; fall back to the raw cut when the very first
                // hunk is already over budget, which beats sending nothing at all.
                if (safeLength > 0)
                {
                    sb.Length = safeLength;
                    cutLine = safeLine;
                }
                else
                {
                    cutLine = i;
                }
                break;
            }

            sb.Append(line).Append('\n');
        }

        int omitted = 0;
        for (int i = cutLine; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("diff --git ", StringComparison.Ordinal)) omitted++;
        }

        sb.Append(Loc.Language == "日本語"
            ? $"... 以降は省略（他 {omitted} ファイル）"
            : $"... truncated ({omitted} more files)");
        return sb.ToString();
    }

    // ── Output ─────────────────────────────────────────────────────────

    /// <summary>
    /// Strips what a CLI adds around an answer: a fenced block, and the blank lines either side.
    /// A message that legitimately contains a fenced block keeps it - only an outer fence that
    /// wraps the whole reply is removed.
    /// </summary>
    internal static string Clean(string output)
    {
        var text = (output ?? "").Replace("\r\n", "\n").Trim();
        if (text.Length == 0) return "";

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int firstBreak = text.IndexOf('\n');
            int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstBreak > 0 && lastFence > firstBreak)
                text = text[(firstBreak + 1)..lastFence].Trim();
        }

        return text.Trim();
    }

    // ── Argument parsing ───────────────────────────────────────────────

    /// <summary>
    /// Splits a stored argument string into arguments, honouring double quotes so a preset can
    /// hold a flag value with a space in it.
    /// </summary>
    internal static string[] SplitArgs(string text)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return args.ToArray();

        var current = new StringBuilder();
        bool quoted = false;
        bool any = false;

        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
                continue;
            }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (any) { args.Add(current.ToString()); current.Clear(); any = false; }
                continue;
            }
            current.Append(c);
            any = true;
        }

        if (any) args.Add(current.ToString());
        return args.ToArray();
    }
}
